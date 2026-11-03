using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;
using Microsoft.Data.Sqlite;

namespace Bos.Sqlite;

internal sealed class SqliteDocumentRepository(SqliteUnitOfWork uow) : IDocumentRepository
{
    public async Task<JsonObject?> GetAsync(string collection, string key, CancellationToken ct = default)
    {
        try
        {
            using var command = uow.Command("SELECT json FROM Documents WHERE collection=$collection AND key=$key", ("$collection", collection), ("$key", key));
            var json = await command.ExecuteScalarAsync(ct) as string;
            return json is null ? null : CanonicalJson.Parse(json, preserveSource: true)!.AsObject();
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }

    public async Task PutAsync(string collection, string key, JsonObject value, CancellationToken ct = default)
    {
        uow.RequireWrite();
        try
        {
            var json = CanonicalJson.Serialize(value, preserveSource: true);
            if (collection is "typeVersions" or "flowVersions")
            {
                var existing = await GetAsync(collection, key, ct);
                if (existing is not null && CanonicalJson.Serialize(existing, preserveSource: true) != json)
                    throw new BosException("INVALID_STATE", "Published versions are immutable.", 409);
            }
            if (collection == "typeVersions")
            {
                var name = value["name"]!.GetValue<string>();
                var version = value["version"]!.GetValue<int>();
                if (version < 1 || key != $"{name}:{version}") throw new BosException("INVALID_REQUEST", "Invalid published type key.");
                using var target = uow.Command("INSERT INTO TypeVersions(name,version) VALUES($name,$version) ON CONFLICT(name,version) DO NOTHING", ("$name", name), ("$version", version));
                await target.ExecuteNonQueryAsync(ct);
            }
            using var command = uow.Command("INSERT INTO Documents(collection,key,json) VALUES($collection,$key,$json) ON CONFLICT(collection,key) DO UPDATE SET json=excluded.json", ("$collection", collection), ("$key", key), ("$json", json));
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) { uow.Poisoned = true; throw SqliteErrors.Translate(ex); }
        catch { uow.Poisoned = true; throw; }
    }

    public async Task DeleteAsync(string collection, string key, CancellationToken ct = default)
    {
        uow.RequireWrite();
        try
        {
            if (collection is "typeVersions" or "flowVersions")
                throw new BosException("POLICY_VIOLATION", "Published versions cannot be deleted.", 403);
            using var command = uow.Command("DELETE FROM Documents WHERE collection=$collection AND key=$key", ("$collection", collection), ("$key", key));
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) { uow.Poisoned = true; throw SqliteErrors.Translate(ex); }
        catch { uow.Poisoned = true; throw; }
    }

    public async Task<IReadOnlyList<JsonObject>> ListAsync(string collection, int limit = 1000, CancellationToken ct = default)
    {
        if (limit is < 1 or > 10000) throw new BosException("QUERY_LIMIT", "Document collection bound must be between 1 and 10000.");
        try
        {
            using var command = uow.Command("SELECT json FROM Documents WHERE collection=$collection ORDER BY key COLLATE BINARY LIMIT $limit", ("$collection", collection), ("$limit", limit + 1));
            using var reader = await command.ExecuteReaderAsync(ct);
            var result = new List<JsonObject>();
            while (await reader.ReadAsync(ct))
            {
                if (result.Count == limit) throw new BosException("QUERY_LIMIT", "The document collection exceeds its read bound.");
                result.Add(CanonicalJson.Parse(reader.GetString(0), preserveSource: true)!.AsObject());
            }
            return result;
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }

    public async Task AppendAuditAsync(JsonObject entry, CancellationToken ct = default)
    {
        uow.RequireWrite();
        try
        {
            var copy = (JsonObject)entry.DeepClone();
            copy.Remove("sequence");
            using var insert = uow.Command("INSERT INTO Audit(json) VALUES($json) RETURNING sequence", ("$json", CanonicalJson.Serialize(copy)));
            var sequence = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
            copy["sequence"] = sequence;
            using var update = uow.Command("UPDATE Audit SET json=$json WHERE sequence=$sequence", ("$json", CanonicalJson.Serialize(copy)), ("$sequence", sequence));
            await update.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) { uow.Poisoned = true; throw SqliteErrors.Translate(ex); }
        catch { uow.Poisoned = true; throw; }
    }
}

internal sealed class SqliteRecordRepository(SqliteUnitOfWork uow) : IRecordRepository
{
    public async Task<JsonObject?> GetAsync(string id, CancellationToken ct = default)
    {
        try
        {
            using var command = uow.Command("SELECT json FROM Records WHERE id=$id", ("$id", id));
            var json = await command.ExecuteScalarAsync(ct) as string;
            return json is null ? null : CanonicalJson.Parse(json)!.AsObject();
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }

    public async Task<bool> HasIncomingReferencesAsync(string id, CancellationToken ct = default)
    {
        try
        {
            using var command = uow.Command("SELECT EXISTS(SELECT 1 FROM RecordReferences ref JOIN Records r ON r.id=ref.sourceId WHERE ref.targetId=$id AND r.deleted=0)", ("$id", id));
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
        }
        catch (SqliteException ex) { throw SqliteErrors.Translate(ex); }
    }

    public async Task SaveAsync(JsonObject record, JsonObject definition, InvocationContext context, CancellationToken ct = default)
    {
        uow.RequireWrite();
        uow.Contexts.Add(context);
        try
        {
            context.Budget.Check(ct);
            if (context.ReadOnly || context.Poisoned) throw new BosException("POLICY_VIOLATION", "The invocation cannot mutate state.", 403);
            var id = record["id"]!.GetValue<string>();
            var type = record["type"]!.GetValue<string>();
            var version = record["typeVersion"]!.GetValue<int>();
            var revision = record["revision"]!.GetValue<int>();
            var deleted = record["deleted"]!.GetValue<bool>();
            var old = await GetAsync(id, ct);
            if (old is null ? revision != 1 || deleted :
                old["type"]!.GetValue<string>() != type || old["typeVersion"]!.GetValue<int>() != version ||
                old["deleted"]!.GetValue<bool>() || revision != (long)old["revision"]!.GetValue<int>() + 1)
                throw new BosException("REVISION_CONFLICT", "The record revision has changed.", 409);
            if (deleted && await HasIncomingReferencesAsync(id, ct))
                throw new BosException("IN_USE", "The record is in use.", 409);
            var json = CanonicalJson.Serialize(record);
            if (old is null)
            {
                using var insert = uow.Command("INSERT INTO Records(id,type,typeVersion,revision,deleted,json) VALUES($id,$type,$version,$revision,$deleted,$json)",
                    ("$id", id), ("$type", type), ("$version", version), ("$revision", revision), ("$deleted", deleted ? 1 : 0), ("$json", json));
                await insert.ExecuteNonQueryAsync(ct);
            }
            else
            {
                using var update = uow.Command("UPDATE Records SET revision=$revision,deleted=$deleted,json=$json WHERE id=$id AND revision=$expected AND deleted=0", ("$revision", revision), ("$deleted", deleted ? 1 : 0), ("$json", json), ("$id", id), ("$expected", revision - 1));
                if (await update.ExecuteNonQueryAsync(ct) != 1) throw new BosException("REVISION_CONFLICT", "The record revision has changed.", 409);
            }
            using (var clear = uow.Command("DELETE FROM IndexValues WHERE recordId=$id; DELETE FROM UniqueValues WHERE recordId=$id; DELETE FROM RecordReferences WHERE sourceId=$id;", ("$id", id)))
                await clear.ExecuteNonQueryAsync(ct);

            if (!deleted)
            {
                var data = record["data"]!.AsObject();
                foreach (var (fieldName, node) in definition["fields"]!.AsObject())
                {
                    var field = node!.AsObject();
                    var unique = field["unique"]?.GetValue<bool>() == true;
                    var exists = data.TryGetPropertyValue(fieldName, out var value);
                    var state = !exists ? 0 : value is null ? 1 : 2;
                    if (unique || field["indexed"]?.GetValue<bool>() == true)
                    {
                        var kind = field["kind"]!.GetValue<string>();
                        var encoded = state == 2 ? CanonicalJson.Serialize(value) : null;
                        var orderKey = state == 2 ? TypedIndex.Key(kind, value!) : null;
                        using var index = uow.Command("INSERT INTO IndexValues(recordId,type,field,state,kind,value,orderKey) VALUES($id,$type,$field,$state,$kind,$value,$orderKey)",
                            ("$id", id), ("$type", type), ("$field", fieldName), ("$state", state), ("$kind", field["kind"]!.GetValue<string>()), ("$value", encoded), ("$orderKey", orderKey));
                        await index.ExecuteNonQueryAsync(ct);
                        if (unique && state == 2)
                        {
                            using var reservation = uow.Command("INSERT INTO UniqueValues(type,field,value,recordId) VALUES($type,$field,$value,$id)", ("$type", type), ("$field", fieldName), ("$value", encoded), ("$id", id));
                            try { await reservation.ExecuteNonQueryAsync(ct); }
                            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode is 1555 or 2067)
                            { throw new BosException("UNIQUE_CONFLICT", "A unique value is already in use.", 409, fieldName); }
                        }
                    }
                    foreach (var reference in References(field, value, fieldName))
                    {
                        var target = await GetAsync(reference.Id, ct);
                        if (target is null || target["deleted"]!.GetValue<bool>() || target["type"]!.GetValue<string>() != reference.Type)
                            throw new BosException("INVALID_REQUEST", "Reference is unavailable.", path: reference.Path);
                        using var link = uow.Command("INSERT INTO RecordReferences(sourceId,targetId,path) VALUES($source,$target,$path)", ("$source", id), ("$target", reference.Id), ("$path", reference.Path));
                        await link.ExecuteNonQueryAsync(ct);
                    }
                }
            }
            using (var history = uow.Command("INSERT INTO RecordHistory(id,revision,json,commandId,runId) VALUES($id,$revision,$json,$command,$run)", ("$id", id), ("$revision", revision), ("$json", json), ("$command", context.CommandId), ("$run", context.RunId)))
                await history.ExecuteNonQueryAsync(ct);
            await uow.Documents.AppendAuditAsync(new JsonObject
            {
                ["timestamp"] = record["updatedAt"]!.DeepClone(), ["actor"] = context.PrincipalId,
                ["credentialId"] = context.Caller.CredentialId,
                ["method"] = old is null ? "records.create" : deleted ? "records.delete" : "records.update",
                ["commandId"] = context.CommandId, ["runId"] = context.RunId,
                ["objectId"] = id, ["type"] = type, ["typeVersion"] = version,
                ["oldRevision"] = old?["revision"]?.DeepClone(), ["newRevision"] = revision,
                ["guardOwner"] = context.GuardOwner, ["stepId"] = context.StepId,
                ["iterationPath"] = context.IterationPath, ["outcome"] = "succeeded"
            }, ct);
        }
        catch (SqliteException ex) { uow.Poisoned = context.Poisoned = true; throw SqliteErrors.Translate(ex); }
        catch { uow.Poisoned = context.Poisoned = true; throw; }
    }

    private static IEnumerable<(string Id, string Type, string Path)> References(JsonObject field, JsonNode? value, string path)
    {
        if (value is null) yield break;
        switch (field["kind"]!.GetValue<string>())
        {
            case "reference":
                yield return (value.GetValue<string>(), field["targetType"]!.GetValue<string>(), path);
                break;
            case "object":
                foreach (var (name, child) in field["fields"]!.AsObject())
                    foreach (var reference in References(child!.AsObject(), value.AsObject()[name], $"{path}.{name}")) yield return reference;
                break;
            case "array":
                var values = value.AsArray();
                for (var i = 0; i < values.Count; i++)
                    foreach (var reference in References(field["items"]!.AsObject(), values[i], $"{path}[{i}]")) yield return reference;
                break;
        }
    }
}
