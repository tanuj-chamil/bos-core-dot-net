using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;
using Microsoft.Data.Sqlite;

namespace Bos.Sqlite;

/// <summary>Typed, bounded queries over the transaction's record indexes and catalogues.</summary>
public sealed class SqliteQueryEngine : IQueryEngine
{
    private readonly byte[] _cursorKey;

    public SqliteQueryEngine(byte[] cursorKey)
    {
        ArgumentNullException.ThrowIfNull(cursorKey);
        if (cursorKey.Length < 32) throw new ArgumentException("Cursor keys must contain at least 256 bits.", nameof(cursorKey));
        _cursorKey = SHA256.HashData(cursorKey);
    }

    public async Task<JsonObject> ListAsync(IUnitOfWork uow, InvocationContext context, string type,
        JsonObject definition, JsonObject query, CancellationToken ct = default)
    {
        Only(query, "where", "orderBy", "select", "limit", "cursor");
        var limit = Limit(query);
        var fields = Fields(definition);
        using var command = Command(uow);
        var comparisons = RecordWhere(command, query, fields, type);
        var sort = RecordSort(query, fields);
        var select = Projection(query, fields);
        var binding = Binding(context, "records", type, comparisons, sort.Json, select, definition);
        var last = DecodeCursor(query, binding);
        var typeParameter = Parameter(command, type);
        var predicate = $"r.type={typeParameter} AND r.deleted=0" + comparisons.Sql;
        string joins = "", state = "2", key = "r.id";
        if (sort.Field != "id")
        {
            joins = $" LEFT JOIN IndexValues s ON s.recordId=r.id AND s.type=r.type AND s.field={Parameter(command, sort.Field)}";
            state = "COALESCE(s.state,0)";
            key = "COALESCE(s.orderKey,'') COLLATE BOS_ORDINAL";
        }
        var orders = sort.Field == "id"
            ? new[] { new Order("r.id", sort.Descending) }
            : new[] { new Order(state, sort.Descending), new Order(key, sort.Descending), new Order("r.id", sort.IdDescending) };
        if (last is not null) predicate += " AND " + After(command, orders, last);
        command.CommandText = $"SELECT r.json,r.typeVersion,{string.Join(',', orders.Select(o => o.Expression))} FROM Records r{joins} WHERE {predicate} ORDER BY {OrderSql(orders)} LIMIT {Parameter(command, limit + 1)}";

        ReadType(context, type);
        var items = new JsonArray();
        JsonArray? next = null;
        using var budget = new QueryBudget(command.Connection!, context, ct);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                budget.Check();
                AcceptVersion(context, type, reader.GetInt32(1));
                if (items.Count == limit) { next = last; break; }
                var record = ParseObject(reader.GetString(0));
                items.Add(Project(record, select));
                last = Keys(reader, 2, orders.Length);
            }
            budget.Check();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9) { throw Interrupted(ct); }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
        context.Budget.ReturnRecords(items.Count);
        return new JsonObject { ["items"] = items, ["nextCursor"] = next is null ? null : EncodeCursor(next, binding) };
    }

    public async Task<JsonObject> CountAsync(IUnitOfWork uow, InvocationContext context, string type,
        JsonObject definition, JsonObject filter, CancellationToken ct = default)
    {
        Only(filter, "where");
        using var command = Command(uow);
        var comparisons = RecordWhere(command, filter, Fields(definition), type);
        command.CommandText = $"SELECT r.typeVersion,COUNT(*) FROM Records r WHERE r.type={Parameter(command, type)} AND r.deleted=0{comparisons.Sql} GROUP BY r.typeVersion";
        ReadType(context, type);
        long count = 0;
        using var budget = new QueryBudget(command.Connection!, context, ct);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                budget.Check();
                AcceptVersion(context, type, reader.GetInt32(0));
                count = checked(count + reader.GetInt64(1));
            }
            budget.Check();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9) { throw Interrupted(ct); }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
        return new JsonObject { ["count"] = count.ToString(CultureInfo.InvariantCulture) };
    }

    public async Task<JsonObject> CatalogueAsync(IUnitOfWork uow, InvocationContext context, string collection,
        JsonObject query, string[] allowedFilters, string[] orderFields, Func<JsonObject, bool> visible,
        CancellationToken ct = default)
    {
        Only(query, "where", "limit", "cursor");
        var limit = Limit(query);
        using var command = Command(uow);
        var normalized = CatalogueComparisons(query, allowedFilters);
        var audit = collection == "audit";
        var source = audit ? "(SELECT sequence AS key,json FROM Audit) d" : "Documents d";
        var predicates = new List<string>();
        if (!audit) predicates.Add($"d.collection={Parameter(command, collection)}");
        foreach (var node in normalized)
        {
            var comparison = node!.AsObject();
            var field = Text(comparison, "field");
            var value = comparison["value"]!;
            var path = Parameter(command, JsonPath(field));
            var kind = JsonKind(value);
            // json_type keeps booleans, numbers and strings distinct (SQLite extracts true as integer 1).
            predicates.Add($"(json_type(d.json,{path})={Parameter(command, kind)} AND json_extract(d.json,{path})={Parameter(command, SqlValue(value))} COLLATE BOS_ORDINAL)");
        }
        var orders = new List<Order>();
        var sortJson = new JsonArray();
        foreach (var fieldSpec in orderFields)
        {
            var (field, descending) = CatalogueOrder(fieldSpec);
            orders.Add(new Order($"json_extract(d.json,{Parameter(command, JsonPath(field))}) COLLATE BOS_ORDINAL", descending));
            sortJson.Add(new JsonObject { ["field"] = field, ["direction"] = descending ? "desc" : "asc" });
        }
        // Audit uses its numeric sequence; document collections use their ordinal string key.
        orders.Add(new Order(audit ? "d.key" : "d.key COLLATE BINARY", false));
        var binding = Binding(context, "catalogue", collection, new Where("", normalized),
            sortJson, new JsonArray(allowedFilters.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()), null);
        var last = DecodeCursor(query, binding);
        if (last is not null) predicates.Add(After(command, orders, last));
        var where = predicates.Count == 0 ? "1" : string.Join(" AND ", predicates);
        command.CommandText = $"SELECT d.json,{string.Join(',', orders.Select(o => o.Expression))} FROM {source} WHERE {where} ORDER BY {OrderSql(orders)}";
        var items = new JsonArray();
        JsonArray? next = null;
        using var budget = new QueryBudget(command.Connection!, context, ct);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                budget.Check();
                var document = ParseObject(reader.GetString(0));
                if (!visible(document)) { budget.Check(); continue; }
                budget.Check();
                if (items.Count == limit) { next = last; break; }
                items.Add(document);
                last = Keys(reader, 1, orders.Count);
            }
            budget.Check();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9) { throw Interrupted(ct); }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
        return new JsonObject { ["items"] = items, ["nextCursor"] = next is null ? null : EncodeCursor(next, binding) };
    }

    public Task<JsonObject> CatalogueValuesAsync(InvocationContext context, string scope,
        IReadOnlyList<JsonObject> values, JsonObject query, string[] allowedFilters, string[] orderFields,
        Func<JsonObject, bool> visible, CancellationToken ct = default)
    {
        Only(query, "where", "limit", "cursor");
        var limit = Limit(query);
        // The registry may grow beyond a single page; run steps have their own smaller producer bound.
        if (values.Count > 10000) throw new BosException("QUERY_LIMIT", "The metadata catalogue exceeds its input bound.");
        var comparisons = CatalogueComparisons(query, allowedFilters);
        var orders = orderFields.Select(CatalogueOrder).ToArray();
        var sortJson = new JsonArray();
        foreach (var (field, descending) in orders)
        {
            JsonPath(field);
            sortJson.Add(new JsonObject { ["field"] = field, ["direction"] = descending ? "desc" : "asc" });
        }
        var binding = Binding(context, "catalogueValues", scope, new Where("", comparisons), sortJson,
            new JsonArray(allowedFilters.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()), null);
        var last = DecodeCursor(query, binding);
        if (last is not null && (last.Count != orders.Length + 1 || last.Any(k => k is JsonObject or JsonArray))) throw BadCursor();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1);
        if (context.Budget.Deadline < deadline) deadline = context.Budget.Deadline;
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline) throw Interrupted(ct);
        }
        int Compare(JsonArray a, JsonArray b)
        {
            for (var i = 0; i < a.Count; i++)
            {
                var result = CompareCatalogueScalar(a[i], b[i]);
                if (result != 0) return i < orders.Length && orders[i].Descending ? -result : result;
            }
            return 0;
        }

        Check();
        // Retain only the earliest limit+1 visible rows, independent of input ordering.
        var earliest = new PriorityQueue<CatalogueRow, CatalogueRow>(Comparer<CatalogueRow>.Create((a, b) => Compare(b.Keys, a.Keys)));
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            Check();
            context.Budget.Node();
            if (!MatchesCatalogue(value, comparisons) || !visible(value)) { Check(); continue; }
            Check();
            var keys = new JsonArray();
            foreach (var (field, _) in orders)
            {
                var key = CanonicalJson.Normalize(value[field]);
                if (key is not null) ValidateCatalogueValue(field, key);
                keys.Add(key);
            }
            // Metadata such as operation descriptors and run steps need no synthetic positional ID.
            // A content identity supplies a stable final tie when the source has no public id.
            keys.Add(value["id"] is JsonValue id && id.TryGetValue<string>(out var text)
                ? "id:" + CanonicalJson.Normalize(JsonValue.Create(text))!.GetValue<string>()
                : "hash:" + CanonicalJson.Hash(value));
            if (!identities.Add(CanonicalJson.Hash(keys)))
                throw new BosException("INVALID_STATE", "The metadata catalogue contains duplicate ordering identities.", 409);
            if (last is not null && Compare(keys, last) <= 0) continue;
            var row = new CatalogueRow(value, keys);
            if (earliest.Count < limit + 1) earliest.Enqueue(row, row);
            else if (Compare(keys, earliest.Peek().Keys) < 0) earliest.DequeueEnqueue(row, row);
        }
        Check();
        var rows = earliest.UnorderedItems.Select(i => i.Element).ToArray();
        Array.Sort(rows, (a, b) => Compare(a.Keys, b.Keys));
        var items = new JsonArray();
        foreach (var row in rows.Take(limit)) { Check(); items.Add(row.Value.DeepClone()); }
        var next = rows.Length > limit ? EncodeCursor(rows[limit - 1].Keys, binding) : null;
        Check();
        context.Budget.ReturnRecords(items.Count);
        return Task.FromResult(new JsonObject { ["items"] = items, ["nextCursor"] = next });
    }

    private static JsonArray CatalogueComparisons(JsonObject query, string[] allowedFilters)
    {
        var normalized = new JsonArray();
        foreach (var comparison in Comparisons(query))
        {
            Only(comparison, "field", "op", "value");
            var field = Text(comparison, "field");
            if (!allowedFilters.Contains(field, StringComparer.Ordinal)) throw Unknown(field);
            JsonPath(field);
            if (Text(comparison, "op") != "eq") throw Invalid("Catalogue filters permit only eq.");
            if (!comparison.TryGetPropertyValue("value", out var value) || value is null || value is JsonObject or JsonArray)
                throw Invalid("Catalogue equality requires a non-null scalar.");
            value = CanonicalJson.Normalize(value)!;
            ValidateCatalogueValue(field, value);
            normalized.Add(new JsonObject { ["field"] = field, ["op"] = "eq", ["value"] = value });
        }
        return NormalizeComparisons(normalized);
    }

    private static bool MatchesCatalogue(JsonObject value, JsonArray comparisons)
    {
        foreach (var comparison in comparisons)
        {
            var actual = value[comparison!["field"]!.GetValue<string>()];
            var expected = comparison["value"]!;
            if (actual is null || actual.GetValueKind() != expected.GetValueKind()) return false;
            if (CompareCatalogueScalar(CanonicalJson.Normalize(actual), expected) != 0) return false;
        }
        return true;
    }

    private static int CompareCatalogueScalar(JsonNode? a, JsonNode? b)
    {
        if (a is null) return b is null ? 0 : -1;
        if (b is null) return 1;
        var left = SqlValue(a);
        var right = SqlValue(b);
        if (left is long number) return right is long other ? number.CompareTo(other) : -1;
        return right is long ? 1 : string.CompareOrdinal((string)left, (string)right);
    }

    private static Where RecordWhere(SqliteCommand command, JsonObject query, JsonObject fields, string type)
    {
        var sql = new StringBuilder();
        var normalized = new JsonArray();
        foreach (var comparison in Comparisons(query))
        {
            Only(comparison, "field", "op", "value");
            var field = Text(comparison, "field");
            var schema = IndexedField(fields, field, type);
            var kind = Text(schema, "kind");
            var op = Text(comparison, "op");
            if (!comparison.TryGetPropertyValue("value", out var value)) throw Invalid("Comparison value is required.");
            var id = field == "id";
            var state = id ? "2" : "i.state";
            var key = id ? "r.id COLLATE BINARY" : "i.orderKey COLLATE BOS_ORDINAL";
            string predicate;
            JsonNode? canonical;
            if (op is "isNull" or "isMissing")
            {
                if (value is not JsonValue boolean || !boolean.TryGetValue<bool>(out var flag)) throw Invalid($"{op} requires a Boolean.");
                canonical = JsonValue.Create(flag);
                predicate = op == "isNull" ? $"{state}={(flag ? 1 : 2)}" : $"{state}{(flag ? "=" : "<>")}0";
            }
            else if (op is "in" or "notIn")
            {
                if (value is not JsonArray values || values.Count > 50) throw Invalid("IN requires an array of at most 50 non-null values.");
                var valuesNormalized = values.Select(v => NormalizeValue(schema, v)).ToArray();
                canonical = new JsonArray(valuesNormalized.OrderBy(v => CanonicalJson.Serialize(v), StringComparer.Ordinal)
                    .DistinctBy(v => CanonicalJson.Serialize(v)).Select(v => v.DeepClone()).ToArray());
                var parameters = valuesNormalized.Select(v => Parameter(command, id ? v.GetValue<string>() : TypedIndex.Key(kind, v))).ToArray();
                predicate = parameters.Length == 0 ? (op == "in" ? "0" : $"{state}=2")
                    : $"{state}=2 AND {key} {(op == "notIn" ? "NOT IN" : "IN")} ({string.Join(',', parameters)})";
            }
            else
            {
                var symbol = op switch { "eq" => "=", "ne" => "<>", "gt" => ">", "gte" => ">=", "lt" => "<", "lte" => "<=", _ => throw Invalid("Unknown comparison operator.") };
                canonical = NormalizeValue(schema, value);
                var parameter = Parameter(command, id ? canonical.GetValue<string>() : TypedIndex.Key(kind, canonical));
                predicate = $"{state}=2 AND {key}{symbol}{parameter}";
            }
            sql.Append(" AND (");
            if (id) sql.Append(predicate);
            else sql.Append($"EXISTS (SELECT 1 FROM IndexValues i WHERE i.recordId=r.id AND i.type=r.type AND i.field={Parameter(command, field)} AND ({predicate}))");
            sql.Append(')');
            normalized.Add(new JsonObject { ["field"] = field, ["op"] = op, ["value"] = canonical });
        }
        return new Where(sql.ToString(), NormalizeComparisons(normalized));
    }

    private static JsonNode NormalizeValue(JsonObject schema, JsonNode? value)
    {
        if (value is null) throw Invalid("Ordinary comparisons do not accept null; use isNull.");
        var field = (JsonObject)schema.DeepClone();
        // Query operands use exactly the field's codec and constraints, without write-only behavior/defaults.
        field["required"] = true;
        field["nullable"] = false;
        field.Remove("default");
        field.Remove("managedBy");
        field.Remove("indexed");
        field.Remove("unique");
        var synthetic = new JsonObject { ["fields"] = new JsonObject { ["value"] = field } };
        return Schema.Normalize(synthetic, new JsonObject { ["value"] = value.DeepClone() }, false)["value"]!.DeepClone();
    }

    private static JsonObject IndexedField(JsonObject fields, string field, string type)
    {
        if (field == "id") return new JsonObject { ["kind"] = "reference", ["targetType"] = type };
        if (fields[field] is not JsonObject schema) throw Unknown(field);
        if (!(schema["indexed"]?.GetValue<bool>() ?? false) && !(schema["unique"]?.GetValue<bool>() ?? false))
            throw Invalid($"Field '{field}' is not indexed.");
        return schema;
    }

    private static Sort RecordSort(JsonObject query, JsonObject fields)
    {
        if (!query.TryGetPropertyValue("orderBy", out var node)) return new Sort("id", false, false);
        if (node is not JsonArray array || array.Count is < 1 or > 2) throw Invalid("orderBy permits one field and an optional id tiebreaker.");
        var primary = ParseOrder(array[0]);
        IndexedField(fields, primary.Field, "Record");
        var idDescending = primary.Descending;
        if (array.Count == 2)
        {
            var secondary = ParseOrder(array[1]);
            if (primary.Field == "id" || secondary.Field != "id") throw Invalid("Only id may be a secondary sort field.");
            idDescending = secondary.Descending;
        }
        return new Sort(primary.Field, primary.Descending, idDescending);
    }

    private static (string Field, bool Descending) ParseOrder(JsonNode? node)
    {
        if (node is not JsonObject order) throw Invalid("Invalid sort specification.");
        Only(order, "field", "direction");
        var direction = order.ContainsKey("direction") ? Text(order, "direction") : "asc";
        if (direction is not ("asc" or "desc")) throw Invalid("Sort direction must be asc or desc.");
        return (Text(order, "field"), direction == "desc");
    }

    private static JsonArray? Projection(JsonObject query, JsonObject fields)
    {
        if (!query.TryGetPropertyValue("select", out var node)) return null;
        if (node is not JsonArray array || array.Count > 200) throw Invalid("select must be an array of declared fields.");
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var field)) throw Invalid("select entries must be field names.");
            if (!fields.ContainsKey(field)) throw Unknown(field);
            names.Add(field);
        }
        return new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
    }

    private static JsonObject Project(JsonObject record, JsonArray? select)
    {
        if (select is null) return record;
        var result = new JsonObject();
        foreach (var name in new[] { "id", "type", "typeVersion", "revision" }) result[name] = record[name]?.DeepClone();
        var data = new JsonObject();
        if (record["data"] is JsonObject source)
            foreach (var field in select)
            {
                var name = field!.GetValue<string>();
                if (source.TryGetPropertyValue(name, out var value)) data[name] = value?.DeepClone();
            }
        result["data"] = data;
        return result;
    }

    private static string After(SqliteCommand command, IReadOnlyList<Order> orders, JsonArray keys)
    {
        if (keys.Count != orders.Count || keys.Any(k => k is JsonObject or JsonArray)) throw BadCursor();
        var alternatives = new List<string>();
        var equal = new List<string>();
        for (var i = 0; i < orders.Count; i++)
        {
            var expression = orders[i].Expression;
            var value = keys[i];
            var parameter = Parameter(command, value is null ? null : SqlValue(value));
            var after = value is null
                ? (orders[i].Descending ? "0" : $"{expression} IS NOT NULL")
                : (orders[i].Descending ? $"({expression}<{parameter} OR {expression} IS NULL)" : $"{expression}>{parameter}");
            alternatives.Add("(" + string.Join(" AND ", equal.Append(after)) + ")");
            equal.Add($"{expression} IS {parameter}");
        }
        return "(" + string.Join(" OR ", alternatives) + ")";
    }

    private static string Binding(InvocationContext context, string family, string target, Where where,
        JsonArray sort, JsonArray? select, JsonObject? definition)
    {
        var versions = new JsonObject();
        foreach (var (name, accepted) in context.TypeVersions.OrderBy(p => p.Key, StringComparer.Ordinal))
            versions[name] = new JsonArray(accepted.Distinct().Order().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return CanonicalJson.Hash(new JsonObject
        {
            ["principal"] = context.PrincipalId, ["family"] = family, ["target"] = target, ["scope"] = context.CursorScope,
            ["where"] = where.Normalized.DeepClone(), ["sort"] = sort, ["select"] = select?.DeepClone(),
            ["versions"] = versions, ["schema"] = definition is null ? null : CanonicalJson.Hash(definition)
        });
    }

    private string EncodeCursor(JsonArray keys, string binding)
    {
        var plaintext = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(keys));
        var token = new byte[12 + 16 + plaintext.Length];
        RandomNumberGenerator.Fill(token.AsSpan(0, 12));
        using var aes = new AesGcm(_cursorKey, 16);
        aes.Encrypt(token.AsSpan(0, 12), plaintext, token.AsSpan(28), token.AsSpan(12, 16), Encoding.UTF8.GetBytes(binding));
        return Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private JsonArray? DecodeCursor(JsonObject query, string binding)
    {
        if (!query.TryGetPropertyValue("cursor", out var node) || node is null) return null;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length is < 38 or > 262144 || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw BadCursor();
        try
        {
            var base64 = text.Replace('-', '+').Replace('_', '/');
            var token = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
            if (token.Length < 29) throw BadCursor();
            if (Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_') != text) throw BadCursor();
            var plaintext = new byte[token.Length - 28];
            using var aes = new AesGcm(_cursorKey, 16);
            aes.Decrypt(token.AsSpan(0, 12), token.AsSpan(28), token.AsSpan(12, 16), plaintext, Encoding.UTF8.GetBytes(binding));
            return CanonicalJson.Parse(Encoding.UTF8.GetString(plaintext)) as JsonArray ?? throw BadCursor();
        }
        catch (CryptographicException) { throw BadCursor(); }
        catch (FormatException) { throw BadCursor(); }
    }

    private static SqliteCommand Command(IUnitOfWork uow)
    {
        if (uow.Connection is not SqliteConnection connection || uow.Transaction is not SqliteTransaction transaction)
            throw new ArgumentException("The query engine requires a SQLite unit of work.", nameof(uow));
        // SQLite's UTF-8 BINARY order differs from CLR ordinal order for supplementary Unicode.
        // The collation also works when Core supplies ASCII-encoded sortable keys.
        connection.CreateCollation("BOS_ORDINAL", string.CompareOrdinal);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 1;
        return command;
    }

    private static string Parameter(SqliteCommand command, object? value)
    {
        var name = "$p" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return name;
    }

    private static JsonArray Keys(SqliteDataReader reader, int start, int count)
    {
        var keys = new JsonArray();
        for (var i = 0; i < count; i++)
            keys.Add(reader.IsDBNull(start + i) ? null : JsonSerializerNode(reader.GetValue(start + i)));
        return keys;
    }

    private static JsonNode JsonSerializerNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value)!;
    private static string OrderSql(IEnumerable<Order> orders) => string.Join(',', orders.Select(o => o.Expression + (o.Descending ? " DESC" : " ASC")));
    private static JsonObject ParseObject(string json) => CanonicalJson.Parse(json) as JsonObject ?? throw new InvalidOperationException("Stored document is not an object.");
    private static JsonObject Fields(JsonObject definition) => definition["fields"] as JsonObject ?? throw Invalid("Type definition has no fields.");
    private static JsonArray NormalizeComparisons(JsonArray comparisons) => new(comparisons.OrderBy(c => CanonicalJson.Serialize(c), StringComparer.Ordinal).Select(c => c!.DeepClone()).ToArray());

    private static IEnumerable<JsonObject> Comparisons(JsonObject query)
    {
        if (!query.TryGetPropertyValue("where", out var node)) return [];
        if (node is not JsonArray array || array.Count > 10 || array.Any(n => n is not JsonObject)) throw Invalid("where must contain at most 10 comparisons.");
        return array.Cast<JsonObject>();
    }

    private static int Limit(JsonObject query)
    {
        if (!query.TryGetPropertyValue("limit", out var node)) return 50;
        if (node is not JsonValue value || !value.TryGetValue<int>(out var limit) || limit is < 1 or > 200) throw Invalid("limit must be an integer from 1 to 200.");
        return limit;
    }

    private static void Only(JsonObject value, params string[] names)
    {
        foreach (var property in value)
            if (!names.Contains(property.Key, StringComparer.Ordinal)) throw Invalid($"Unknown argument '{property.Key}'.");
    }

    private static string Text(JsonObject value, string name)
        => value[name] is JsonValue node && node.TryGetValue<string>(out var text) ? text : throw Invalid($"{name} must be a string.");

    private static string JsonPath(string field)
    {
        if (field.Length == 0 || field.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw Invalid("Invalid catalogue field.");
        return "$." + field;
    }

    private static (string Field, bool Descending) CatalogueOrder(string specification)
    {
        var field = specification.Trim();
        var descending = field.StartsWith('-') || field.EndsWith(" desc", StringComparison.OrdinalIgnoreCase);
        if (field.StartsWith('-') || field.StartsWith('+')) field = field[1..];
        if (field.EndsWith(" desc", StringComparison.OrdinalIgnoreCase)) field = field[..^5];
        else if (field.EndsWith(" asc", StringComparison.OrdinalIgnoreCase)) field = field[..^4];
        else if (!specification.StartsWith('+') && !specification.StartsWith('-')) descending = field is "startedAt" or "createdAt";
        return (field, descending);
    }

    private static string JsonKind(JsonNode value)
    {
        var kind = value.GetValueKind();
        return kind switch
        {
            System.Text.Json.JsonValueKind.String => "text", System.Text.Json.JsonValueKind.Number => "integer",
            System.Text.Json.JsonValueKind.True => "true", System.Text.Json.JsonValueKind.False => "false", _ => throw Invalid("Expected a scalar value.")
        };
    }

    private static object SqlValue(JsonNode value) => value.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
        System.Text.Json.JsonValueKind.True => 1L, System.Text.Json.JsonValueKind.False => 0L,
        System.Text.Json.JsonValueKind.Number when value.AsValue().TryGetValue<long>(out var number) => number,
        _ => throw Invalid("Expected a string, Boolean or integer metadata value.")
    };

    private static void ValidateCatalogueValue(string field, JsonNode value)
    {
        if (field is "active" or "disabled" or "revoked" or "flowCallable")
        {
            if (value.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) throw Invalid($"{field} requires a Boolean.");
        }
        else if (field is "flowVersion" or "version" or "contractVersion" or "ordinal" or "sequence")
        {
            if (!value.AsValue().TryGetValue<int>(out var number) || number < 1) throw Invalid($"{field} requires a positive Int32.");
        }
        else if (value.GetValueKind() != System.Text.Json.JsonValueKind.String) throw Invalid($"{field} requires a string.");
    }

    private static void ReadType(InvocationContext context, string type)
    {
        if (context.RunId is not null && !context.TypeVersions.ContainsKey(type))
            throw new BosException("SCHEMA_VERSION_MISMATCH", "The flow has no declared read dependency for this type.", 409);
        context.ReadTypes.Add(type);
    }

    private static void AcceptVersion(InvocationContext context, string type, int version)
    {
        if (context.TypeVersions.TryGetValue(type, out var versions) && !versions.Contains(version))
            throw new BosException("SCHEMA_VERSION_MISMATCH", "A record has a schema version outside the accepted dependency set.", 409);
    }

    private static BosException Invalid(string message) => new("INVALID_REQUEST", message, 400);
    private static BosException Unknown(string field) => new("UNKNOWN_FIELD", $"Unknown field '{field}'.", 422, field);
    private static BosException BadCursor() => new("INVALID_CURSOR", "The cursor is invalid for this query.", 400);
    private static BosException Interrupted(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new BosException("QUERY_LIMIT", "The query exceeded its work or time budget.");
    }

    private sealed record Where(string Sql, JsonArray Normalized);
    private sealed record CatalogueRow(JsonObject Value, JsonArray Keys);
    private sealed record Order(string Expression, bool Descending);
    private sealed record Sort(string Field, bool Descending, bool IdDescending)
    {
        public JsonArray Json => new(new JsonObject { ["field"] = Field, ["direction"] = Descending ? "desc" : "asc" },
            new JsonObject { ["field"] = "id", ["direction"] = IdDescending ? "desc" : "asc" });
    }

    private sealed class QueryBudget : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly CancellationToken _ct;
        private readonly DateTimeOffset _deadline;
        private readonly SQLitePCL.delegate_progress _callback;
        private int _callbacks;

        public QueryBudget(SqliteConnection connection, InvocationContext context, CancellationToken ct)
        {
            _connection = connection;
            _ct = ct;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(1);
            _deadline = context.Budget.Deadline < deadline ? context.Budget.Deadline : deadline;
            Check();
            _callback = _ => ++_callbacks >= 1000 || _ct.IsCancellationRequested || DateTimeOffset.UtcNow >= _deadline ? 1 : 0;
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000, _callback, null);
        }

        public void Check()
        {
            _ct.ThrowIfCancellationRequested();
            if (_callbacks >= 1000 || DateTimeOffset.UtcNow >= _deadline) throw Interrupted(_ct);
        }

        public void Dispose() => SQLitePCL.raw.sqlite3_progress_handler(_connection.Handle, 0, null, null);
    }
}
