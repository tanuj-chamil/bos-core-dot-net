using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;
using Microsoft.Data.Sqlite;

namespace Bos.Sqlite;

internal sealed class SqliteAttachmentRepository(SqliteUnitOfWork uow) : IAttachmentRepository
{
    public Task<JsonObject?> GetAsync(string id, CancellationToken ct = default) => ReadAsync(
        "SELECT json FROM Attachments WHERE id=$value", id, ct);

    public Task<JsonObject?> GetByCommandAsync(string ownerId, string commandKey, CancellationToken ct = default) => ReadAsync(
        "SELECT json FROM Attachments WHERE ownerId=$owner AND commandKey=$value", commandKey, ct, ownerId);

    private async Task<JsonObject?> ReadAsync(string sql, string value, CancellationToken ct, string? owner = null)
    {
        try
        {
            using var command = owner is null ? uow.Command(sql, ("$value", value)) : uow.Command(sql, ("$owner", owner), ("$value", value));
            var json = await command.ExecuteScalarAsync(ct) as string;
            return json is null ? null : CanonicalJson.Parse(json)!.AsObject();
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }

    public async Task<IReadOnlyList<JsonObject>> ListAsync(string recordId, int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new BosException("QUERY_LIMIT", "Attachment list limit must be from 1 to 100.");
        try
        {
            using var command = uow.Command("SELECT json FROM Attachments WHERE recordId=$record AND state='linked' ORDER BY id COLLATE BINARY LIMIT $limit", ("$record", recordId), ("$limit", limit + 1));
            await using var reader = await command.ExecuteReaderAsync(ct);
            var items = new List<JsonObject>();
            while (await reader.ReadAsync(ct))
            {
                if (items.Count == limit) throw new BosException("QUERY_LIMIT", "A record has more than 100 linked attachments.");
                items.Add(CanonicalJson.Parse(reader.GetString(0))!.AsObject());
            }
            return items;
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }

    public async Task<byte[]?> GetContentAsync(string id, CancellationToken ct = default)
    {
        try
        {
            using var command = uow.Command("SELECT content FROM Attachments WHERE id=$id AND state<>'deleted'", ("$id", id));
            return await command.ExecuteScalarAsync(ct) as byte[];
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }

    public async Task InsertAsync(JsonObject metadata, byte[] content, string commandKey, string inputHash, CancellationToken ct = default)
    {
        uow.RequireWrite();
        try
        {
            using var command = uow.Command("INSERT INTO Attachments(id,recordId,type,revision,deleted,state,expiresAt,ownerId,commandKey,inputHash,json,content) VALUES($id,NULL,NULL,1,0,'pending',$expires,$owner,$command,$hash,$json,$content)",
                ("$id", metadata["id"]!.GetValue<string>()), ("$expires", metadata["expiresAt"]!.GetValue<string>()), ("$owner", metadata["ownerId"]!.GetValue<string>()),
                ("$command", commandKey), ("$hash", inputHash), ("$json", CanonicalJson.Serialize(metadata)), ("$content", content));
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) { uow.Poisoned = true; throw SqliteErrors.Translate(ex); }
        catch { uow.Poisoned = true; throw; }
    }

    public async Task UpdateAsync(JsonObject metadata, int expectedRevision, CancellationToken ct = default)
    {
        uow.RequireWrite();
        try
        {
            using var command = uow.Command("UPDATE Attachments SET recordId=$record,type=$type,revision=$revision,deleted=$deleted,state=$state,expiresAt=$expires,json=$json,content=CASE WHEN $deleted=1 THEN X'' ELSE content END WHERE id=$id AND revision=$expected AND state<>'deleted'",
                ("$record", metadata["recordId"] is null ? DBNull.Value : metadata["recordId"]!.GetValue<string>()),
                ("$type", metadata["type"] is null ? DBNull.Value : metadata["type"]!.GetValue<string>()),
                ("$revision", metadata["revision"]!.GetValue<int>()), ("$deleted", metadata["deleted"]!.GetValue<bool>() ? 1 : 0),
                ("$state", metadata["state"]!.GetValue<string>()), ("$expires", metadata["expiresAt"] is null ? DBNull.Value : metadata["expiresAt"]!.GetValue<string>()),
                ("$json", CanonicalJson.Serialize(metadata)), ("$id", metadata["id"]!.GetValue<string>()), ("$expected", expectedRevision));
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new BosException("REVISION_CONFLICT", "The attachment revision has changed.", 409);
        }
        catch (SqliteException ex) { uow.Poisoned = true; throw SqliteErrors.Translate(ex); }
        catch { uow.Poisoned = true; throw; }
    }

    public async Task<int> CountPendingAsync(string ownerId, CancellationToken ct = default)
    {
        try
        {
            using var command = uow.Command("SELECT COUNT(*) FROM Attachments WHERE ownerId=$owner AND state='pending'", ("$owner", ownerId));
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }

    public async Task CleanupExpiredPendingAsync(string expiresBefore, CancellationToken ct = default)
    {
        uow.RequireWrite();
        try
        {
            using var command = uow.Command("DELETE FROM Attachments WHERE state='pending' AND expiresAt<=$expires", ("$expires", expiresBefore));
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) { uow.Poisoned = true; throw SqliteErrors.Translate(ex); }
    }

    public async Task<bool> HasLinkedAsync(string recordId, CancellationToken ct = default) => await ExistsAsync(
        "SELECT EXISTS(SELECT 1 FROM Attachments WHERE recordId=$value AND state='linked')", recordId, ct);

    public async Task<bool> BelongsToRecordAsync(string id, string recordId, CancellationToken ct = default)
    {
        try
        {
            using var command = uow.Command("SELECT EXISTS(SELECT 1 FROM Attachments WHERE id=$id AND recordId=$record AND state='linked')", ("$id", id), ("$record", recordId));
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }

    private async Task<bool> ExistsAsync(string sql, string value, CancellationToken ct)
    {
        try
        {
            using var command = uow.Command(sql, ("$value", value));
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }
}
