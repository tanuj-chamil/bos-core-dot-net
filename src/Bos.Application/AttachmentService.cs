using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;

namespace Bos.Application;

internal sealed class AttachmentService(IUnitOfWorkFactory factory, IAuthorization authorization, IClock clock, IIdGenerator ids)
{
    internal const int MaximumBytes = 25 * 1024 * 1024;

    public async Task<JsonObject> UploadAsync(string fileName, string contentType,
        byte[] content, Caller caller, string key, string workspaceTimezone, CancellationToken ct)
    {
        ReceiptProtocol.ValidateKey(key);
        fileName = ValidFileName(fileName);
        contentType = ValidMediaType(contentType);
        if (content.Length > MaximumBytes) throw new BosException("REQUEST_TOO_LARGE", "Attachment exceeds the 25 MiB limit.", 413);
        CheckStorage(content.Length);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var inputHash = CanonicalJson.Hash(new JsonObject
        {
            ["fileName"] = fileName,
            ["contentType"] = contentType,
            ["sizeBytes"] = content.Length,
            ["sha256"] = sha
        });
        var context = new InvocationContext
        {
            Caller = caller,
            CommandId = ids.NewId(),
            StartedAt = clock.UtcNow,
            WorkspaceTimezone = workspaceTimezone,
            Budget = new Budget { Deadline = DateTimeOffset.UtcNow.AddMinutes(2) }
        };
        await using var uow = await factory.BeginWriteAsync(ct);
        await authorization.ReloadAsync(uow, context, ct);
        if (await uow.Attachments.GetByCommandAsync(caller.PrincipalId, key, ct) is { } replay)
        {
            if (replay["inputHash"]!.GetValue<string>() != inputHash)
                throw new BosException("IDEMPOTENCY_CONFLICT", "The command key was already used for a different attachment.", 409);
            await AuthorizeMetadataAsync(replay, caller, context, uow, ct);
            return Public(replay);
        }
        await uow.Attachments.CleanupExpiredPendingAsync(Instant(clock.UtcNow), ct);
        if (await uow.Attachments.CountPendingAsync(caller.PrincipalId, ct) >= 20)
            throw new BosException("CAPACITY_EXCEEDED", "The pending attachment limit has been reached.", 409);
        var now = Instant(clock.UtcNow);
        var metadata = new JsonObject
        {
            ["id"] = JsonArgs.Uuid(ids.NewId()),
            ["type"] = null,
            ["recordId"] = null,
            ["state"] = "pending",
            ["revision"] = 1,
            ["deleted"] = false,
            ["ownerId"] = caller.PrincipalId,
            ["fileName"] = fileName,
            ["contentType"] = contentType,
            ["sizeBytes"] = content.Length,
            ["sha256"] = sha,
            ["createdAt"] = now,
            ["createdBy"] = caller.PrincipalId,
            ["updatedAt"] = now,
            ["updatedBy"] = caller.PrincipalId,
            ["expiresAt"] = Instant(clock.UtcNow.AddHours(24)),
            ["inputHash"] = inputHash
        };
        await uow.Attachments.InsertAsync(metadata, content, key, inputHash, ct);
        await AuditAsync(uow, context, "attachments.upload", metadata, null, 1, ct);
        try { await uow.CommitAsync(ct); }
        catch
        {
            await uow.DisposeAsync();
            await using var fresh = await factory.BeginReadAsync(CancellationToken.None);
            var durable = await fresh.Attachments.GetByCommandAsync(caller.PrincipalId, key, CancellationToken.None);
            if (durable?["inputHash"]?.GetValue<string>() != inputHash)
                throw new BosException("OUTCOME_UNKNOWN", "The upload outcome could not be confirmed. Replay the same idempotency key.", 503, retryable: false);
            var reauthorized = new InvocationContext { Caller = caller, StartedAt = clock.UtcNow, WorkspaceTimezone = workspaceTimezone };
            await authorization.ReloadAsync(fresh, reauthorized, CancellationToken.None);
            await AuthorizeMetadataAsync(durable, caller, reauthorized, fresh, CancellationToken.None);
            return Public(durable);
        }
        return Public(metadata);
    }

    public async Task<AttachmentDownload> DownloadAsync(string id, Caller caller, string workspaceTimezone, CancellationToken ct)
    {
        id = JsonArgs.Uuid(id);
        var context = new InvocationContext { Caller = caller, StartedAt = clock.UtcNow, WorkspaceTimezone = workspaceTimezone };
        await using var uow = await factory.BeginReadAsync(ct);
        await authorization.ReloadAsync(uow, context, ct);
        var metadata = await uow.Attachments.GetAsync(id, ct);
        if (metadata is null || metadata["deleted"]!.GetValue<bool>()) throw NotFound();
        if (metadata["state"]!.GetValue<string>() == "pending")
        {
            if (metadata["ownerId"]!.GetValue<string>() != caller.PrincipalId || string.CompareOrdinal(metadata["expiresAt"]!.GetValue<string>(), Instant(clock.UtcNow)) <= 0) throw NotFound();
        }
        else await RequireRecordAsync(metadata["type"]!.GetValue<string>(), metadata["recordId"]!.GetValue<string>(), context, uow, false, ct);
        var content = await uow.Attachments.GetContentAsync(id, ct) ?? throw NotFound();
        if (content.LongLength != metadata["sizeBytes"]!.GetValue<long>() || Convert.ToHexStringLower(SHA256.HashData(content)) != metadata["sha256"]!.GetValue<string>())
            throw new BosException("INTERNAL_ERROR", "Attachment content integrity check failed.", 500);
        return new(Public(metadata), content);
    }

    public async Task<JsonNode?> InvokeAsync(string method, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        var type = JsonArgs.Name(JsonArgs.String(args, "type"));
        var recordId = JsonArgs.Uuid(JsonArgs.String(args, "recordId"));
        if (method == "attachments.list")
        {
            JsonArgs.Only(args, "type", "recordId");
            await RequireRecordAsync(type, recordId, context, uow, false, ct);
            var values = await uow.Attachments.ListAsync(recordId, 100, ct);
            context.Budget.ReturnRecords(values.Count);
            return new JsonObject { ["items"] = new JsonArray(values.Select(value => (JsonNode)Public(value)).ToArray()) };
        }
        if (method == "attachments.link")
        {
            JsonArgs.Only(args, "type", "recordId", "id", "expectedRevision");
            await RequireRecordAsync(type, recordId, context, uow, true, ct);
            var id = JsonArgs.Uuid(JsonArgs.String(args, "id"));
            var expected = JsonArgs.Int(args, "expectedRevision");
            var metadata = await uow.Attachments.GetAsync(id, ct);
            if (metadata is null || metadata["state"]!.GetValue<string>() != "pending" || metadata["ownerId"]!.GetValue<string>() != context.PrincipalId ||
                string.CompareOrdinal(metadata["expiresAt"]!.GetValue<string>(), Instant(clock.UtcNow)) <= 0) throw NotFound();
            if (metadata["revision"]!.GetValue<int>() != expected) throw new BosException("REVISION_CONFLICT", "The attachment revision has changed.", 409);
            if ((await uow.Attachments.ListAsync(recordId, 100, ct)).Count >= 100) throw new BosException("CAPACITY_EXCEEDED", "The record attachment limit has been reached.", 409);
            Link(metadata, type, recordId, context);
            context.Budget.Mutation();
            await uow.Attachments.UpdateAsync(metadata, expected, ct);
            await AuditAsync(uow, context, method, metadata, expected, expected + 1, ct);
            return Public(metadata);
        }
        if (method == "attachments.remove")
        {
            JsonArgs.Only(args, "type", "recordId", "id", "expectedRevision");
            await RequireRecordAsync(type, recordId, context, uow, true, ct);
            var id = JsonArgs.Uuid(JsonArgs.String(args, "id"));
            var expected = JsonArgs.Int(args, "expectedRevision");
            var metadata = await uow.Attachments.GetAsync(id, ct);
            if (metadata is null || metadata["state"]!.GetValue<string>() != "linked" || metadata["type"]!.GetValue<string>() != type || metadata["recordId"]!.GetValue<string>() != recordId)
                throw NotFound();
            if (expected < 1 || metadata["revision"]!.GetValue<int>() != expected) throw new BosException("REVISION_CONFLICT", "The attachment revision has changed.", 409);
            var record = await uow.Records.GetAsync(recordId, ct) ?? throw NotFound();
            var version = record["typeVersion"]!.GetValue<int>();
            var published = await uow.Documents.GetAsync("typeVersions", $"{type}:{version}", ct)
                ?? throw new BosException("DEPENDENCY_UNAVAILABLE", "The published type version is unavailable.", 409);
            if (ContainsAttachment(published["definition"]!["fields"]!.AsObject(), record["data"]!.AsObject(), id))
                throw new BosException("IN_USE", "Remove the attachment from record fields before deleting it.", 409);
            metadata["revision"] = expected + 1;
            metadata["deleted"] = true;
            metadata["state"] = "deleted";
            metadata["updatedAt"] = Instant(clock.UtcNow);
            metadata["updatedBy"] = context.PrincipalId;
            context.Budget.Mutation();
            await uow.Attachments.UpdateAsync(metadata, expected, ct);
            await AuditAsync(uow, context, method, metadata, expected, expected + 1, ct);
            return Public(metadata);
        }
        throw new BosException("NOT_FOUND", "Attachment method not found.", 404);
    }

    private async Task RequireRecordAsync(string type, string id, InvocationContext context, IUnitOfWork uow, bool write, CancellationToken ct)
    {
        if (!authorization.Can(context, "record.read", type) || write && !authorization.Can(context, "record.update", type)) throw NotFound();
        var record = await uow.Records.GetAsync(id, ct);
        if (record is null || record["deleted"]!.GetValue<bool>() || record["type"]!.GetValue<string>() != type) throw NotFound();
        context.ReadTypes.Add(type);
    }

    private async Task AuthorizeMetadataAsync(JsonObject metadata, Caller caller, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        if (metadata["state"]!.GetValue<string>() == "pending")
        {
            if (metadata["ownerId"]!.GetValue<string>() != caller.PrincipalId ||
                string.CompareOrdinal(metadata["expiresAt"]!.GetValue<string>(), Instant(clock.UtcNow)) <= 0) throw NotFound();
            return;
        }
        await RequireRecordAsync(metadata["type"]!.GetValue<string>(), metadata["recordId"]!.GetValue<string>(), context, uow, false, ct);
    }

    private static JsonObject Public(JsonObject metadata)
    {
        var result = (JsonObject)metadata.DeepClone();
        result.Remove("ownerId");
        result.Remove("inputHash");
        result["downloadUrl"] = "/v1/attachments/" + result["id"]!.GetValue<string>() + "/content";
        return result;
    }

    internal static void Link(JsonObject metadata, string type, string recordId, InvocationContext context)
    {
        var revision = metadata["revision"]!.GetValue<int>();
        metadata["type"] = type;
        metadata["recordId"] = recordId;
        metadata["state"] = "linked";
        metadata["revision"] = revision + 1;
        metadata["expiresAt"] = null;
        metadata["updatedAt"] = Instant(context.StartedAt);
        metadata["updatedBy"] = context.PrincipalId;
    }

    private static bool ContainsAttachment(JsonObject fields, JsonObject data, string id) =>
        fields.Any(pair => data.TryGetPropertyValue(pair.Key, out var value) && Contains(pair.Value!.AsObject(), value, id));

    private static bool Contains(JsonObject field, JsonNode? value, string id)
    {
        if (value is null) return false;
        return field["kind"]!.GetValue<string>() switch
        {
            "attachment" => value.GetValue<string>() == id,
            "object" => ContainsAttachment(field["fields"]!.AsObject(), value.AsObject(), id),
            "array" => value.AsArray().Any(item => Contains(field["items"]!.AsObject(), item, id)),
            "map" => value.AsObject().Any(item => Contains(field["values"]!.AsObject(), item.Value, id)),
            "taggedUnion" => Contains(field["variants"]![value["tag"]!.GetValue<string>()]!.AsObject(), value["value"], id),
            _ => false
        };
    }

    private static string ValidFileName(string value)
    {
        value = value.Normalize();
        if (value.Length is < 1 or > 255 || Encoding.UTF8.GetByteCount(value) > 512 || value.Any(char.IsControl))
            throw new BosException("INVALID_REQUEST", "Attachment file name is invalid.", 400, "fileName");
        return value;
    }

    private static string ValidMediaType(string value)
    {
        value = value.Trim();
        if (value.Length is < 3 or > 200 || !MediaTypeHeaderValue.TryParse(value, out var parsed) || parsed.MediaType is null ||
            parsed.MediaType.Contains('*', StringComparison.Ordinal))
            throw new BosException("INVALID_REQUEST", "Attachment content type is invalid.", 400, "contentType");
        return parsed.ToString();
    }

    private void CheckStorage(int incomingBytes)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(factory.DatabasePath))!;
        var current = (File.Exists(factory.DatabasePath) ? new FileInfo(factory.DatabasePath).Length : 0) +
            (File.Exists(factory.DatabasePath + "-wal") ? new FileInfo(factory.DatabasePath + "-wal").Length : 0);
        if (new DriveInfo(directory).AvailableFreeSpace - incomingBytes < 512L * 1024 * 1024 || current + incomingBytes >= 2L * 1024 * 1024 * 1024)
            throw new BosException("CAPACITY_EXCEEDED", "Workspace storage capacity is unavailable.", 503);
    }

    private static Task AuditAsync(IUnitOfWork uow, InvocationContext context, string method, JsonObject metadata, int? oldRevision, int newRevision, CancellationToken ct) =>
        uow.Documents.AppendAuditAsync(new JsonObject
        {
            ["timestamp"] = metadata["updatedAt"]!.DeepClone(),
            ["actor"] = context.PrincipalId,
            ["credentialId"] = context.Caller.CredentialId,
            ["method"] = method,
            ["commandId"] = context.CommandId,
            ["objectId"] = metadata["id"]!.DeepClone(),
            ["type"] = metadata["type"]?.DeepClone() ?? JsonValue.Create("Attachment"),
            ["oldRevision"] = oldRevision,
            ["newRevision"] = newRevision,
            ["outcome"] = "succeeded"
        }, ct);

    private static string Instant(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static BosException NotFound() => new("NOT_FOUND", "Attachment or record not found.", 404);
}
