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
                case "records.getRevision": JsonArgs.Only(args, "type", "id", "revision"); break;
                case "records.getHistory": JsonArgs.Only(args, "type", "id", "query"); break;
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
            if (method is "records.getRevision" or "records.getHistory")
            {
                var id = JsonArgs.Uuid(JsonArgs.String(args, "id"));
                await RequireHistoryTargetAsync(type, id, context, uow, ct);
                if (method == "records.getRevision")
                {
                    var revision = JsonArgs.Int(args, "revision");
                    if (revision < 1) throw new BosException("INVALID_REQUEST", "Revision must be positive.", 400, "revision");
                    var record = await uow.Records.GetRevisionAsync(id, revision, ct);
                    if (record is null || record["type"]!.GetValue<string>() != type) throw NotFound();
                    CheckPin(type, record["typeVersion"]!.GetValue<int>(), context);
                    context.Budget.ReturnRecords(1);
                    return record.DeepClone();
                }
                return await queries.RecordHistoryAsync(uow, context, type, id, JsonArgs.Object(args, "query", new JsonObject()), ct);
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

    private async Task RequireHistoryTargetAsync(string type, string id, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        if (!authorization.Can(context, "record.read", type)) throw NotFound();
        var record = await uow.Records.GetAsync(id, ct);
        if (record is null || record["type"]!.GetValue<string>() != type) throw NotFound();
        context.ReadTypes.Add(type);
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
        else if (field["kind"]?.GetValue<string>() == "map")
        {
            var values = value as JsonObject;
            var oldValues = previous as JsonObject;
            var keys = (values?.Select(pair => pair.Key) ?? []).Concat(oldValues?.Select(pair => pair.Key) ?? []).Distinct(StringComparer.Ordinal);
            var child = field["values"]!.AsObject();
            foreach (var key in keys)
            {
                JsonNode? item = null;
                JsonNode? oldItem = null;
                var present = values?.TryGetPropertyValue(key, out item) == true;
                var oldPresent = oldValues?.TryGetPropertyValue(key, out oldItem) == true;
                if (updating && present == oldPresent && JsonNode.DeepEquals(item, oldItem)) continue;
                var childPath = $"{path}[{CanonicalJson.Serialize(JsonValue.Create(key))}]";
                if (child["managedBy"] is JsonArray managed && managed.Count != 0 && !Allowed(managed, context.GuardOwner))
                    throw new BosException("POLICY_VIOLATION", "The field requires an allowed business operation.", 403, childPath);
                GuardValue(child, item, oldItem, updating, context, childPath);
            }
        }
        else if (field["kind"]?.GetValue<string>() == "taggedUnion")
        {
            var tagged = value as JsonObject;
            var oldTagged = previous as JsonObject;
            var tag = tagged?["tag"]?.GetValue<string>();
            var oldTag = oldTagged?["tag"]?.GetValue<string>();
            if (tag == oldTag && tag is not null)
                GuardVariant(tag, tagged!["value"], oldTagged?["value"], updating);
            else
            {
                if (oldTag is not null) GuardVariant(oldTag, null, oldTagged!["value"], updating);
                if (tag is not null) GuardVariant(tag, tagged!["value"], null, updating);
            }

            void GuardVariant(string variantName, JsonNode? variantValue, JsonNode? oldVariantValue, bool isUpdate)
            {
                var variant = field["variants"]![variantName]!.AsObject();
                var variantPath = path + ".value";
                if (variant["managedBy"] is JsonArray managed && managed.Count != 0 && !Allowed(managed, context.GuardOwner))
                    throw new BosException("POLICY_VIOLATION", "The field requires an allowed business operation.", 403, variantPath);
                GuardValue(variant, variantValue, oldVariantValue, isUpdate, context, variantPath);
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
        var id = JsonArgs.Uuid(ids.NewId());
        var pendingAttachments = await RequireReferencesAsync(definition, data, id, context, uow, ct);
        var now = Timestamp();
        var record = new JsonObject
        {
            ["id"] = id, ["type"] = type, ["typeVersion"] = version,
            ["revision"] = 1, ["deleted"] = false, ["data"] = data,
            ["createdAt"] = now, ["createdBy"] = context.PrincipalId,
            ["updatedAt"] = now, ["updatedBy"] = context.PrincipalId
        };
        context.Budget.Mutation();
        context.Budget.ReturnRecords(1);
        await uow.Records.SaveAsync(record, definition, context, ct);
        await LinkPendingAttachmentsAsync(pendingAttachments, type, id, context, uow, ct);
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
        var pendingAttachments = await RequireReferencesAsync(definition, data, id, context, uow, ct);
        if (expected == int.MaxValue) throw new BosException("INVALID_STATE", "The record revision limit has been reached.", 409);
        record["revision"] = expected + 1;
        record["data"] = data;
        record["updatedAt"] = Timestamp();
        record["updatedBy"] = context.PrincipalId;
        context.Budget.Mutation();
        context.Budget.ReturnRecords(1);
        await uow.Records.SaveAsync(record, definition, context, ct);
        await LinkPendingAttachmentsAsync(pendingAttachments, type, id, context, uow, ct);
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
        if (await uow.Attachments.HasLinkedAsync(id, ct)) throw new BosException("IN_USE", "Remove linked attachments before deleting the record.", 409);
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
            errors.AddRange((await ReferenceErrorsAsync(definition, data, null, context, uow, ct)).Errors);
        }
        return new JsonObject
        {
            ["valid"] = errors.Count == 0,
            ["errors"] = new JsonArray(errors.Take(100).Select(x => (JsonNode)new JsonObject { ["path"] = x.Path, ["code"] = x.Code, ["message"] = x.Message }).ToArray())
        };
    }

    private async Task<IReadOnlySet<string>> RequireReferencesAsync(JsonObject definition, JsonObject data, string? recordId, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        var result = await ReferenceErrorsAsync(definition, data, recordId, context, uow, ct);
        if (result.Errors.Count != 0) throw new BosException("INVALID_REQUEST", "Record references are unavailable.", 422) { Details = result.Errors };
        return result.PendingAttachments;
    }

    private async Task<(IReadOnlyList<FieldError> Errors, IReadOnlySet<string> PendingAttachments)> ReferenceErrorsAsync(JsonObject definition, JsonObject data, string? recordId, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        var pendingAttachments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, field) in definition["fields"]!.AsObject())
            await VisitAsync(field!.AsObject(), data[name], name);
        return (errors, pendingAttachments);

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
                case "polymorphicReference":
                {
                    var reference = value.AsObject();
                    await CurrentReferenceAsync(reference["type"]!.GetValue<string>(), reference["id"]!.GetValue<string>(), path);
                    break;
                }
                case "versionedReference":
                {
                    var type = field["targetType"]!.GetValue<string>();
                    var reference = value.AsObject();
                    if (!authorization.Can(context, "record.read", type)) { Unavailable(path); break; }
                    var target = await uow.Records.GetRevisionAsync(reference["id"]!.GetValue<string>(), reference["revision"]!.GetValue<int>(), ct);
                    if (target is null || target["deleted"]!.GetValue<bool>() || target["type"]!.GetValue<string>() != type) Unavailable(path);
                    else { CheckPin(type, target["typeVersion"]!.GetValue<int>(), context); context.ReadTypes.Add(type); }
                    break;
                }
                case "userReference":
                    if (await uow.Documents.GetAsync("users", value.GetValue<string>(), ct) is null) Unavailable(path);
                    break;
                case "attachment":
                {
                    var attachmentId = value.GetValue<string>();
                    var attachment = await uow.Attachments.GetAsync(attachmentId, ct);
                    if (attachment is null || attachment["state"]!.GetValue<string>() == "deleted") { Unavailable(path); break; }
                    if (attachment["state"]!.GetValue<string>() == "pending")
                    {
                        if (attachment["ownerId"]!.GetValue<string>() != context.PrincipalId ||
                            string.CompareOrdinal(attachment["expiresAt"]!.GetValue<string>(), Timestamp()) <= 0) Unavailable(path);
                        else pendingAttachments.Add(attachmentId);
                    }
                    else if (recordId is null || attachment["recordId"]!.GetValue<string>() != recordId) Unavailable(path);
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
                case "map":
                    foreach (var (name, item) in value.AsObject()) await VisitAsync(field["values"]!.AsObject(), item, path + "." + name);
                    break;
                case "taggedUnion":
                    var tagged = value.AsObject();
                    await VisitAsync(field["variants"]![tagged["tag"]!.GetValue<string>()]!.AsObject(), tagged["value"], path + ".value");
                    break;
            }

            async Task CurrentReferenceAsync(string type, string id, string referencePath)
            {
                if (!authorization.Can(context, "record.read", type)) { Unavailable(referencePath); return; }
                var target = await uow.Records.GetAsync(id, ct);
                if (target is null || target["deleted"]!.GetValue<bool>() || target["type"]!.GetValue<string>() != type) Unavailable(referencePath);
                else { CheckPin(type, target["typeVersion"]!.GetValue<int>(), context); context.ReadTypes.Add(type); }
            }
            void Unavailable(string referencePath) => errors.Add(new(referencePath, "INVALID_REQUEST", "Reference is unavailable."));
        }
    }

    private async Task LinkPendingAttachmentsAsync(IReadOnlySet<string> idsToLink, string type, string recordId, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        if (idsToLink.Count == 0) return;
        if ((await uow.Attachments.ListAsync(recordId, 100, ct)).Count + idsToLink.Count > 100)
            throw new BosException("CAPACITY_EXCEEDED", "The record attachment limit has been reached.", 409);
        foreach (var attachmentId in idsToLink)
        {
            var metadata = await uow.Attachments.GetAsync(attachmentId, ct) ?? throw new BosException("INVALID_REQUEST", "Attachment is unavailable.", 422);
            var expected = metadata["revision"]!.GetValue<int>();
            AttachmentService.Link(metadata, type, recordId, context);
            context.Budget.Mutation();
            await uow.Attachments.UpdateAsync(metadata, expected, ct);
            await uow.Documents.AppendAuditAsync(new JsonObject
            {
                ["timestamp"] = metadata["updatedAt"]!.DeepClone(), ["actor"] = context.PrincipalId,
                ["credentialId"] = context.Caller.CredentialId, ["method"] = "attachments.link", ["commandId"] = context.CommandId,
                ["objectId"] = attachmentId, ["type"] = type, ["oldRevision"] = expected,
                ["newRevision"] = expected + 1, ["outcome"] = "succeeded"
            }, ct);
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
