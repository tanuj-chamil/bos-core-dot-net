using System.Globalization;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;

namespace Bos.Application;

public sealed class RecordService(IAuthorization authorization, IQueryEngine queries, IClock clock, IIdGenerator ids)
{
    public async Task<JsonNode?> InvokeAsync(string method, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct = default)
    {
        try
        {
            context.Budget.Check(ct);
            switch (method)
            {
                case "records.create": JsonArgs.Only(args, "type", "data"); break;
                case "records.get": JsonArgs.Only(args, "type", "id"); break;
                case "records.update": JsonArgs.Only(args, "type", "id", "patch", "expectedRevision"); break;
                case "records.delete": JsonArgs.Only(args, "type", "id", "expectedRevision"); break;
                case "records.validate": JsonArgs.Only(args, "type", "data", "typeVersion"); break;
                case "records.list": JsonArgs.Only(args, "type", "query"); break;
                case "records.count": JsonArgs.Only(args, "type", "filter"); break;
                default: throw new BosException("FEATURE_NOT_AVAILABLE", "The record method is not available.", 404);
            }
            var type = JsonArgs.Name(JsonArgs.String(args, "type"));
            if (method == "records.get")
            {
                var id = JsonArgs.Uuid(JsonArgs.String(args, "id"));
                var record = await ReadAsync(type, id, context, uow, ct);
                context.Budget.ReturnRecords(1);
                return record.DeepClone();
            }
            authorization.Require(context, "record.read", type);
            context.ReadTypes.Add(type);
            switch (method)
            {
                case "records.create": return await CreateAsync(type, args, context, uow, ct);
                case "records.update": return await UpdateAsync(type, args, context, uow, ct);
                case "records.delete": return await DeleteAsync(type, args, context, uow, ct);
                case "records.validate": return await ValidateAsync(type, args, context, uow, ct);
                case "records.list":
                {
                    var (_, definition) = await ResolveAsync(type, null, context, uow, ct);
                    return await queries.ListAsync(uow, context, type, definition, JsonArgs.Object(args, "query", new JsonObject()), ct);
                }
                case "records.count":
                {
                    var (_, definition) = await ResolveAsync(type, null, context, uow, ct);
                    return await queries.CountAsync(uow, context, type, definition, JsonArgs.Object(args, "filter", new JsonObject()), ct);
                }
                default: throw new InvalidOperationException();
            }
        }
        catch
        {
            // A trusted operation cannot catch an SDK failure and commit other staged changes.
            context.Poisoned = true;
            throw;
        }
    }

    private async Task<JsonObject> ReadAsync(string type, string id, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        if (!authorization.Can(context, "record.read", type)) throw NotFound();
        var record = await uow.Records.GetAsync(id, ct);
        if (record is null || record["deleted"]!.GetValue<bool>() || record["type"]!.GetValue<string>() != type) throw NotFound();
        CheckPin(type, record["typeVersion"]!.GetValue<int>(), context);
        context.ReadTypes.Add(type);
        return record;
    }

    private async Task<(int Version, JsonObject Definition)> ResolveAsync(string type, int? version, InvocationContext context, IUnitOfWork uow, CancellationToken ct, bool creating = false)
    {
        if (version.HasValue)
        {
            if (version < 1) throw new BosException("INVALID_REQUEST", "Type version must be positive.", 400, "typeVersion");
            CheckPin(type, version.Value, context);
        }
        else if (context.TypeVersions.TryGetValue(type, out var pinned))
        {
            if (pinned.Length == 0 || (creating && pinned.Length != 1))
                throw new BosException("SCHEMA_VERSION_MISMATCH", "This operation requires a supported pinned type version.", 409);
            version = pinned.Max();
        }
        else
        {
            if (context.RunId is not null) throw new BosException("SCHEMA_VERSION_MISMATCH", "The flow has no pinned dependency for this type.", 409);
            var active = await uow.Documents.GetAsync("typeActive", type, ct) ?? throw NotFound();
            version = active["version"]!.GetValue<int>();
        }
        var published = await uow.Documents.GetAsync("typeVersions", $"{type}:{version.Value}", ct)
            ?? throw new BosException("DEPENDENCY_UNAVAILABLE", "The published type version is unavailable.", 409);
        return (version.Value, published["definition"]!.AsObject());
    }

    private static void CheckPin(string type, int version, InvocationContext context)
    {
        if (context.RunId is not null && !context.TypeVersions.ContainsKey(type))
            throw new BosException("SCHEMA_VERSION_MISMATCH", "The flow has no pinned dependency for this type.", 409);
        if (context.TypeVersions.TryGetValue(type, out var allowed) && !allowed.Contains(version))
            throw new BosException("SCHEMA_VERSION_MISMATCH", "The record uses an unsupported type version.", 409);
    }

    private void RequireWrite(string action, string type, InvocationContext context, IUnitOfWork uow)
    {
        authorization.Require(context, action, type);
        if (context.ReadOnly || !uow.IsWrite || context.Poisoned)
            throw new BosException("POLICY_VIOLATION", "The invocation cannot mutate state.", 403);
    }

    private static void GuardType(JsonObject definition, InvocationContext context)
    {
        if (definition["accessMode"]?.GetValue<string>() == "operationsOnly" &&
            !Allowed(definition["allowedOperations"], context.GuardOwner))
            throw new BosException("POLICY_VIOLATION", "This mutation requires an allowed business operation.", 403);
    }

    private static bool Allowed(JsonNode? operations, string? owner) => owner is not null &&
        operations is JsonArray array && array.Any(x => x?.GetValue<string>() == owner);

    // On create only explicitly supplied managed values need an owner; schema defaults are permitted.
    // On update compare the normalized replacement, including nested object/array assignments.
    private static void GuardFields(JsonObject fields, JsonObject proposed, JsonObject? old, InvocationContext context, string prefix = "")
    {
        foreach (var (name, node) in fields)
        {
            var exists = proposed.TryGetPropertyValue(name, out var value);
            var field = node!.AsObject();
            var path = prefix + name;
            var oldExists = old is not null && old.ContainsKey(name);
            if (!exists && !oldExists) continue;
            var previous = oldExists ? old![name] : null;
            var changed = old is null || exists != oldExists || !JsonNode.DeepEquals(value, previous);
            if (!changed) continue;
            if (field["managedBy"] is JsonArray managed && managed.Count != 0 && !Allowed(managed, context.GuardOwner))
                throw new BosException("POLICY_VIOLATION", "The field requires an allowed business operation.", 403, path);
            GuardValue(field, value, previous, old is not null, context, path);
        }
    }

    private static void GuardValue(JsonObject field, JsonNode? value, JsonNode? previous, bool updating, InvocationContext context, string path)
    {
        if (field["kind"]?.GetValue<string>() == "object")
        {
            // Removing/replacing a container also changes its managed descendants.
            var proposed = value as JsonObject ?? new JsonObject();
            GuardFields(field["fields"]!.AsObject(), proposed, updating ? previous as JsonObject ?? new JsonObject() : null, context, path + ".");
        }
        else if (field["kind"]?.GetValue<string>() == "array")
        {
            var items = field["items"]!.AsObject();
            var values = value as JsonArray;
            var oldValues = previous as JsonArray;
            var count = Math.Max(values?.Count ?? 0, oldValues?.Count ?? 0);
            for (var i = 0; i < count; i++)
            {
                var item = values is not null && i < values.Count ? values[i] : null;
                var oldItem = oldValues is not null && i < oldValues.Count ? oldValues[i] : null;
                var present = values is not null && i < values.Count;
                var oldPresent = oldValues is not null && i < oldValues.Count;
                if (updating && present == oldPresent && JsonNode.DeepEquals(item, oldItem)) continue;
                if (items["managedBy"] is JsonArray managed && managed.Count != 0 && !Allowed(managed, context.GuardOwner))
                    throw new BosException("POLICY_VIOLATION", "The field requires an allowed business operation.", 403, $"{path}[{i}]");
                GuardValue(items, item, oldItem, updating, context, $"{path}[{i}]");
            }
        }
    }

    private async Task<JsonObject> CreateAsync(string type, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        RequireWrite("record.create", type, context, uow);
        var (version, definition) = await ResolveAsync(type, null, context, uow, ct, creating: true);
        GuardType(definition, context);
        var input = JsonArgs.Object(args, "data");
        GuardFields(definition["fields"]!.AsObject(), input, null, context);
        var data = Schema.Normalize(definition, input);
        await RequireReferencesAsync(definition, data, context, uow, ct);
        var now = Timestamp();
        var record = new JsonObject
        {
            ["id"] = JsonArgs.Uuid(ids.NewId()), ["type"] = type, ["typeVersion"] = version,
            ["revision"] = 1, ["deleted"] = false, ["data"] = data,
            ["createdAt"] = now, ["createdBy"] = context.PrincipalId,
            ["updatedAt"] = now, ["updatedBy"] = context.PrincipalId
        };
        context.Budget.Mutation();
        context.Budget.ReturnRecords(1);
        await uow.Records.SaveAsync(record, definition, context, ct);
        var id = record["id"]!.GetValue<string>();
        context.CreatedRecordIds.Add(id);
        context.ChangedRecordIds.Add(id);
        return (JsonObject)record.DeepClone();
    }

    private async Task<JsonObject> UpdateAsync(string type, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        RequireWrite("record.update", type, context, uow);
        var id = JsonArgs.Uuid(JsonArgs.String(args, "id"));
        var expected = Revision(args);
        var patch = JsonArgs.Object(args, "patch");
        if (patch.Count == 0) throw new BosException("INVALID_REQUEST", "Patch must contain at least one field.", 400, "patch");
        var record = await ReadAsync(type, id, context, uow, ct);
        CheckRevision(record, expected);
        var (_, definition) = await ResolveAsync(type, record["typeVersion"]!.GetValue<int>(), context, uow, ct);
        GuardType(definition, context);
        var old = record["data"]!.AsObject();
        var merged = (JsonObject)old.DeepClone();
        foreach (var (name, value) in patch) merged[name] = value?.DeepClone();
        var data = Schema.Normalize(definition, merged, false);
        GuardFields(definition["fields"]!.AsObject(), data, old, context);
        if (JsonNode.DeepEquals(data, old)) throw new BosException("NO_CHANGE", "The normalized record is unchanged.", 409);
        await RequireReferencesAsync(definition, data, context, uow, ct);
        if (expected == int.MaxValue) throw new BosException("INVALID_STATE", "The record revision limit has been reached.", 409);
        record["revision"] = expected + 1;
        record["data"] = data;
        record["updatedAt"] = Timestamp();
        record["updatedBy"] = context.PrincipalId;
        context.Budget.Mutation();
        context.Budget.ReturnRecords(1);
        await uow.Records.SaveAsync(record, definition, context, ct);
        context.ChangedRecordIds.Add(id);
        return (JsonObject)record.DeepClone();
    }

    private async Task<JsonObject> DeleteAsync(string type, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        RequireWrite("record.delete", type, context, uow);
        var id = JsonArgs.Uuid(JsonArgs.String(args, "id"));
        var expected = Revision(args);
        var record = await ReadAsync(type, id, context, uow, ct);
        CheckRevision(record, expected);
        var (_, definition) = await ResolveAsync(type, record["typeVersion"]!.GetValue<int>(), context, uow, ct);
        GuardType(definition, context);
        if (context.CreatedRecordIds.Contains(id)) throw new BosException("INVALID_STATE", "A record created in this invocation cannot be deleted.", 409);
        if (await uow.Records.HasIncomingReferencesAsync(id, ct)) throw new BosException("IN_USE", "The record is in use.", 409);
        if (expected == int.MaxValue) throw new BosException("INVALID_STATE", "The record revision limit has been reached.", 409);
        record["revision"] = expected + 1;
        record["deleted"] = true;
        record["updatedAt"] = Timestamp();
        record["updatedBy"] = context.PrincipalId;
        context.Budget.Mutation();
        await uow.Records.SaveAsync(record, definition, context, ct);
        context.ChangedRecordIds.Add(id);
        return new JsonObject { ["id"] = id, ["type"] = type, ["revision"] = expected + 1, ["deleted"] = true };
    }

    private async Task<JsonObject> ValidateAsync(string type, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        if (!authorization.Can(context, "record.create", type) && !authorization.Can(context, "record.update", type))
            throw new BosException("FORBIDDEN", "Validation requires record create or update permission.", 403);
        var version = args.ContainsKey("typeVersion") ? JsonArgs.Int(args, "typeVersion") : (int?)null;
        var (_, definition) = await ResolveAsync(type, version, context, uow, ct);
        var input = JsonArgs.Object(args, "data");
        var report = Schema.ValidateData(definition, input);
        var errors = report.Errors.ToList();
        if (report.Valid)
        {
            var data = Schema.Normalize(definition, input);
            errors.AddRange(await ReferenceErrorsAsync(definition, data, context, uow, ct));
        }
        return new JsonObject
        {
            ["valid"] = errors.Count == 0,
            ["errors"] = new JsonArray(errors.Take(100).Select(x => (JsonNode)new JsonObject { ["path"] = x.Path, ["code"] = x.Code, ["message"] = x.Message }).ToArray())
        };
    }

    private async Task RequireReferencesAsync(JsonObject definition, JsonObject data, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        var errors = await ReferenceErrorsAsync(definition, data, context, uow, ct);
        if (errors.Count != 0) throw new BosException("INVALID_REQUEST", "Record references are unavailable.", 422) { Details = errors };
    }

    private async Task<IReadOnlyList<FieldError>> ReferenceErrorsAsync(JsonObject definition, JsonObject data, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        foreach (var (name, field) in definition["fields"]!.AsObject())
            await VisitAsync(field!.AsObject(), data[name], name);
        return errors;

        async Task VisitAsync(JsonObject field, JsonNode? value, string path)
        {
            context.Budget.Check(ct);
            if (value is null || errors.Count >= 100) return;
            switch (field["kind"]!.GetValue<string>())
            {
                case "reference":
                {
                    var type = field["targetType"]!.GetValue<string>();
                    if (!authorization.Can(context, "record.read", type))
                    {
                        errors.Add(new(path, "INVALID_REQUEST", "Reference is unavailable."));
                        return;
                    }
                    var target = await uow.Records.GetAsync(value.GetValue<string>(), ct);
                    if (target is null || target["deleted"]!.GetValue<bool>() || target["type"]!.GetValue<string>() != type)
                        errors.Add(new(path, "INVALID_REQUEST", "Reference is unavailable."));
                    else
                    {
                        CheckPin(type, target["typeVersion"]!.GetValue<int>(), context);
                        context.ReadTypes.Add(type);
                    }
                    break;
                }
                case "object":
                    foreach (var (name, child) in field["fields"]!.AsObject())
                        await VisitAsync(child!.AsObject(), value.AsObject()[name], path + "." + name);
                    break;
                case "array":
                    var array = value.AsArray();
                    for (var i = 0; i < array.Count; i++) await VisitAsync(field["items"]!.AsObject(), array[i], $"{path}[{i}]");
                    break;
            }
        }
    }

    private string Timestamp() => clock.UtcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static int Revision(JsonObject args)
    {
        var revision = JsonArgs.Int(args, "expectedRevision");
        if (revision < 1) throw new BosException("INVALID_REQUEST", "Expected revision must be positive.", 400, "expectedRevision");
        return revision;
    }
    private static void CheckRevision(JsonObject record, int expected)
    {
        if (record["revision"]!.GetValue<int>() != expected) throw new BosException("REVISION_CONFLICT", "The record revision has changed.", 409);
    }
    private static BosException NotFound() => new("NOT_FOUND", "The record or type was not found.", 404);
}
