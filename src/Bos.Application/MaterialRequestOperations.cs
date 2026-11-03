using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;

namespace Bos.Application;

/// <summary>The immutable version-one reference application. All persistence goes through the caller's dispatcher.</summary>
public static class MaterialRequestOperations
{
    private const string Parent = "MaterialRequest";
    private const string Line = "MaterialRequestLine";

    public static void Register(IOperationRegistry registry)
    {
        foreach (var name in new[] { "CreateRequest", "ReviseRequest", "RepriceRequestLine", "FinalizeRequest", "ApproveRequest" })
            registry.Register(new Operation(name));
    }

    private sealed class Operation(string name) : IOperation
    {
        // Return detached schemas: discovery consumers cannot mutate the registered contract.
        public OperationDescriptor Descriptor => Describe(name);

        public async Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            async Task<JsonObject> Call(string method, JsonObject args)
                => (await dispatcher.CallAsync(method, 1, args, context, uow, ct) as JsonObject
                    ?? throw new BosException("INTERNAL_ERROR", "Record service returned an invalid envelope.", 500)).DeepClone().AsObject();
            Task<JsonObject> Get(string type, string id) => Call("records.get", new() { ["type"] = type, ["id"] = id });
            Task<JsonObject> Update(string type, JsonObject record, JsonObject patch) => Call("records.update", new()
            {
                ["type"] = type, ["id"] = Text(record, "id"), ["expectedRevision"] = record["revision"]!.DeepClone(), ["patch"] = patch
            });
            async Task<JsonArray> Lines(string id)
            {
                var page = await Call("records.list", new()
                {
                    ["type"] = Line,
                    ["query"] = new JsonObject
                    {
                        ["where"] = new JsonArray(new JsonObject { ["field"] = "requestId", ["op"] = "eq", ["value"] = id }),
                        ["limit"] = 100
                    }
                });
                if (!page.ContainsKey("nextCursor") || page["nextCursor"] is not null)
                    throw new BosException("TOO_MANY_LINES", "A request accepts at most 100 lines.");
                var lines = page["items"] as JsonArray ?? throw new BosException("INTERNAL_ERROR", "Invalid record page.", 500);
                if (lines.Count > 100) throw new BosException("TOO_MANY_LINES", "A request accepts at most 100 lines.");
                if (lines.Count == 0) throw new BosException("EMPTY_REQUEST", "At least one line is required.");
                foreach (var node in lines)
                    if (node is not JsonObject row || Text(Data(row), "requestId") != id)
                        throw new BosException("INTERNAL_ERROR", "Record query returned an unrelated line.", 500);
                return lines.DeepClone().AsArray();
            }

            if (name == "CreateRequest")
            {
                var data = Schema.Normalize(CreateSchema(), input);
                var source = data["lines"]!.AsArray();
                if (source.Count is < 1 or > 100) throw new BosException("INVALID_REQUEST", "Supply 1–100 lines.", path: "lines");
                var request = await Call("records.create", new()
                {
                    ["type"] = Parent,
                    ["data"] = new JsonObject
                    {
                        ["title"] = data["title"]!.DeepClone(), ["currency"] = data["currency"]!.DeepClone(),
                        ["status"] = "Draft", ["total"] = "0.00", ["contentVersion"] = "1"
                    }
                });
                var lines = new JsonArray();
                foreach (var node in source)
                {
                    var lineData = node!.DeepClone().AsObject();
                    lineData["requestId"] = Text(request, "id");
                    lineData["amount"] = "0.00";
                    lines.Add(await Call("records.create", new() { ["type"] = Line, ["data"] = lineData }));
                }
                return new JsonObject { ["request"] = request, ["lines"] = lines };
            }

            JsonArgs.Only(input, name == "ReviseRequest" ? ["id", "expectedRevision", "title", "lineChanges"] : ["id", "expectedRevision"]);
            var id = JsonArgs.Uuid(JsonArgs.String(input, "id"));
            var expected = JsonArgs.Int(input, "expectedRevision");
            if (expected < 1) throw new BosException("INVALID_REQUEST", "Expected revision must be positive.", path: "expectedRevision");

            if (name == "RepriceRequestLine")
            {
                var line = await Get(Line, id);
                Revision(line, expected);
                var parent = await Get(Parent, Text(Data(line), "requestId"));
                State(parent, "Draft");
                var amount = Price(Data(line));
                if (Text(Data(line), "amount") == amount) return line;
                return await Update(Line, line, new() { ["amount"] = amount });
            }

            var current = await Get(Parent, id);
            // Check before even staging a write; the root transaction also covers earlier flow calls.
            Revision(current, expected);
            State(current, name == "ApproveRequest" ? "Submitted" : "Draft");
            if (name == "ApproveRequest") return await Update(Parent, current, new() { ["status"] = "Approved" });

            var allLines = await Lines(id);
            if (name == "FinalizeRequest")
            {
                BigInteger total = 0;
                foreach (var node in allLines)
                {
                    var lineData = Data(node!.AsObject());
                    var amount = Price(lineData);
                    if (Text(lineData, "amount") != amount)
                        throw new BosException("AMOUNT_MISMATCH", "Stored line amount does not match independently calculated pricing.");
                    total += Scaled(amount, 2);
                }
                return await Update(Parent, current, new() { ["total"] = Money(total), ["status"] = "Submitted" });
            }

            var patch = new JsonObject();
            if (input.ContainsKey("title"))
            {
                var title = Schema.Normalize(Fields(("title", StringField())), new JsonObject { ["title"] = input["title"]?.DeepClone() });
                if (!JsonNode.DeepEquals(title["title"], Data(current)["title"])) patch["title"] = title["title"]!.DeepClone();
            }
            var changes = new List<(JsonObject Record, JsonObject Patch)>();
            if (input.ContainsKey("lineChanges"))
            {
                if (input["lineChanges"] is not JsonArray edits || edits.Count > 100)
                    throw new BosException("INVALID_REQUEST", "lineChanges must be an array of at most 100 edits.", path: "lineChanges");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var byId = allLines.Select(x => x!.AsObject()).ToDictionary(x => Text(x, "id"), StringComparer.Ordinal);
                foreach (var node in edits)
                {
                    var edit = node as JsonObject ?? throw new BosException("INVALID_REQUEST", "A line change must be an object.");
                    JsonArgs.Only(edit, "id", "expectedRevision", "item", "quantity", "unitPrice");
                    var lineId = JsonArgs.Uuid(JsonArgs.String(edit, "id"));
                    if (!seen.Add(lineId)) throw new BosException("INVALID_REQUEST", "Duplicate line IDs are not permitted.");
                    if (!byId.TryGetValue(lineId, out var record)) throw new BosException("NOT_FOUND", "Line does not belong to this request.", 404);
                    Revision(record, JsonArgs.Int(edit, "expectedRevision"));
                    var business = new JsonObject();
                    foreach (var key in new[] { "item", "quantity", "unitPrice" })
                        if (edit.ContainsKey(key)) business[key] = edit[key]?.DeepClone();
                    var normalized = Schema.Normalize(LineFields(false), business);
                    var linePatch = new JsonObject();
                    foreach (var field in normalized)
                        if (!JsonNode.DeepEquals(field.Value, Data(record)[field.Key])) linePatch[field.Key] = field.Value?.DeepClone();
                    if (linePatch.Count == 0) throw new BosException("NO_CHANGE", "Each line edit must change an allowed field.", 409);
                    linePatch["amount"] = "0.00";
                    changes.Add((record, linePatch));
                }
            }
            if (patch.Count == 0 && changes.Count == 0) throw new BosException("NO_CHANGE", "The request is unchanged.", 409);
            var counter = Text(Data(current), "contentVersion");
            if (!long.TryParse(counter, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version is < 1 or long.MaxValue)
                throw new BosException("INTEGER_OVERFLOW", "Content version cannot be incremented.");
            patch["contentVersion"] = (version + 1).ToString(CultureInfo.InvariantCulture);
            patch["total"] = "0.00";
            // Validate all edits and preconditions before staging any changes.
            foreach (var change in changes)
            {
                var updated = await Update(Line, change.Record, change.Patch);
                for (var i = 0; i < allLines.Count; i++)
                    if (Text(allLines[i]!.AsObject(), "id") == Text(updated, "id")) { allLines[i] = updated; break; }
            }
            var revised = await Update(Parent, current, patch);
            return new JsonObject { ["request"] = revised, ["lines"] = allLines };
        }
    }

    private static JsonObject Data(JsonObject record) => record["data"] as JsonObject ?? throw new BosException("INTERNAL_ERROR", "Invalid record data.", 500);
    private static string Text(JsonObject obj, string key) => obj[key]?.GetValue<string>() ?? throw new BosException("INVALID_REQUEST", $"Missing {key}.", path: key);
    private static void Revision(JsonObject record, int expected)
    {
        if (expected < 1 || JsonArgs.Int(record, "revision") != expected) throw new BosException("REVISION_CONFLICT", "The record revision has changed.", 412);
    }
    private static void State(JsonObject record, string state)
    {
        if (Text(Data(record), "status") != state) throw new BosException("INVALID_STATE", $"Request must be {state}.", 409);
    }

    private static string Price(JsonObject data)
    {
        // Normalize the operands, then multiply integers at scale five. System.Decimal
        // multiplication can silently round a 30-digit intermediate before explicit rounding.
        var operands = Schema.Normalize(LineFields(), new JsonObject
        {
            ["item"] = data["item"]?.DeepClone(), ["quantity"] = data["quantity"]?.DeepClone(), ["unitPrice"] = data["unitPrice"]?.DeepClone()
        });
        var product = Scaled(Text(operands, "quantity"), 3) * Scaled(Text(operands, "unitPrice"), 2);
        var cents = BigInteger.DivRem(product, 1000, out var remainder);
        if (remainder > 500 || (remainder == 500 && !cents.IsEven)) cents++;
        return Money(cents);
    }
    private static BigInteger Scaled(string value, int scale)
    {
        var parts = value.Split('.');
        return BigInteger.Parse(parts[0], CultureInfo.InvariantCulture) * BigInteger.Pow(10, scale)
            + (parts.Length == 2 ? BigInteger.Parse(parts[1].PadRight(scale, '0'), CultureInfo.InvariantCulture) : BigInteger.Zero);
    }
    private static string Money(BigInteger cents)
    {
        if (cents < 0 || cents >= BigInteger.Pow(10, 18)) throw new BosException("DECIMAL_OVERFLOW", "Amount exceeds decimal precision 18, scale 2.");
        return (cents / 100).ToString(CultureInfo.InvariantCulture) + "." + (cents % 100).ToString("D2", CultureInfo.InvariantCulture);
    }

    private static OperationDescriptor Describe(string name)
    {
        var capabilities = new List<Grant> { new("record.read", Parent) };
        if (name != "ApproveRequest") capabilities.Add(new("record.read", Line));
        if (name == "CreateRequest") { capabilities.Add(new("record.create", Parent)); capabilities.Add(new("record.create", Line)); }
        else
        {
            if (name != "RepriceRequestLine") capabilities.Add(new("record.update", Parent));
            if (name is "ReviseRequest" or "RepriceRequestLine") capabilities.Add(new("record.update", Line));
        }
        var input = name == "CreateRequest" ? CreateSchema() : IdentitySchema();
        if (name == "ReviseRequest")
        {
            var fields = input["fields"]!.AsObject();
            fields["title"] = StringField(false);
            var edit = IdentitySchema()["fields"]!.DeepClone().AsObject();
            foreach (var field in LineFields(false)["fields"]!.AsObject()) edit[field.Key] = field.Value!.DeepClone();
            fields["lineChanges"] = new JsonObject { ["kind"] = "array", ["maxItems"] = 100, ["items"] = new JsonObject { ["kind"] = "object", ["fields"] = edit } };
        }
        var output = name is "CreateRequest" or "ReviseRequest"
            ? Fields(("request", RecordField(false)), ("lines", new JsonObject { ["kind"] = "array", ["required"] = true, ["maxItems"] = 100, ["items"] = RecordField(true) }))
            : new JsonObject { ["fields"] = RecordField(name == "RepriceRequestLine")["fields"]!.DeepClone() };
        return new(name, 1, name switch
        {
            "CreateRequest" => "Create a draft material request with 1–100 lines.",
            "ReviseRequest" => "Revise draft content and invalidate prior pricing and parent decisions.",
            "RepriceRequestLine" => "Price one draft line using exact multiplication and half-even rounding.",
            "FinalizeRequest" => "Verify every line amount and submit a nonempty, bounded draft request.",
            _ => "Approve a submitted request."
        }, input, output, "write", true, Array.AsReadOnly(new[] { new Grant("operations.execute", name) }), capabilities.AsReadOnly(),
            ["input.expectedRevision", "input.lineChanges[].expectedRevision", "output.revision", "output.typeVersion",
             "output.request.revision", "output.request.typeVersion", "output.lines[].revision", "output.lines[].typeVersion"]);
    }
    private static JsonObject Fields(params (string Name, JsonObject Field)[] fields)
    {
        var map = new JsonObject();
        foreach (var field in fields) map[field.Name] = field.Field;
        return new() { ["fields"] = map };
    }
    private static JsonObject StringField(bool required = true) => new() { ["kind"] = "string", ["required"] = required, ["maxLength"] = 200 };
    private static JsonObject IntegerField() => new() { ["kind"] = "integer", ["required"] = true, ["minimum"] = "1", ["maximum"] = "2147483647" };
    private static JsonObject DecimalField(int precision, int scale, string? minimum = null, bool required = true)
    {
        var field = new JsonObject { ["kind"] = "decimal", ["precision"] = precision, ["scale"] = scale, ["required"] = required };
        if (minimum is not null) field["minimum"] = minimum;
        return field;
    }
    private static JsonObject IdentitySchema() => Fields(("id", StringField()), ("expectedRevision", IntegerField()));
    private static JsonObject LineFields(bool required = true) => Fields(("item", StringField(required)),
        ("quantity", DecimalField(12, 3, "0.001", required)), ("unitPrice", DecimalField(18, 2, "0.00", required)));
    private static JsonObject CreateSchema() => Fields(("title", StringField()),
        ("currency", new() { ["kind"] = "enum", ["values"] = new JsonArray("LKR", "USD"), ["required"] = true }),
        ("lines", new() { ["kind"] = "array", ["required"] = true, ["maxItems"] = 100, ["items"] = new JsonObject { ["kind"] = "object", ["fields"] = LineFields()["fields"]!.DeepClone() } }));
    private static JsonObject RecordField(bool line)
    {
        var data = line ? LineFields()["fields"]!.DeepClone().AsObject() : CreateSchema()["fields"]!.DeepClone().AsObject();
        if (line)
        {
            data["requestId"] = StringField(); data["amount"] = DecimalField(18, 2);
        }
        else
        {
            data.Remove("lines");
            data["status"] = new JsonObject { ["kind"] = "enum", ["required"] = true, ["values"] = new JsonArray("Draft", "Submitted", "Approved") };
            data["total"] = DecimalField(18, 2);
            data["contentVersion"] = new JsonObject { ["kind"] = "integer", ["required"] = true, ["minimum"] = "1" };
        }
        var fields = Fields(("id", StringField()), ("type", StringField()), ("typeVersion", IntegerField()), ("revision", IntegerField()),
            ("deleted", new() { ["kind"] = "boolean", ["required"] = true }),
            ("createdAt", new() { ["kind"] = "instant", ["required"] = true }), ("createdBy", StringField()),
            ("updatedAt", new() { ["kind"] = "instant", ["required"] = true }), ("updatedBy", StringField()),
            ("data", new() { ["kind"] = "object", ["required"] = true, ["fields"] = data }))["fields"]!.DeepClone();
        return new() { ["kind"] = "object", ["required"] = true, ["fields"] = fields };
    }
}
