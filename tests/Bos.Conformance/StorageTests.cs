using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bos.Application;
using Bos.Contracts;
using Bos.Core;
using Bos.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bos.Conformance;

public sealed class StorageTests
{
    [Fact]
    public async Task Disposing_without_commit_rolls_back_documents_records_and_every_derived_table()
    {
        await using var db = await Database.CreateAsync();
        var record = Record("Note", new JsonObject { ["code"] = "A", ["quantity"] = "2" });
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            await PublishAsync(write, Definition());
            await write.Documents.PutAsync("receipts", "receipt", new JsonObject { ["status"] = "succeeded" });
            await write.Records.SaveAsync(record, Definition(), Context());
            Assert.NotNull(await write.Records.GetAsync(record["id"]!.GetValue<string>()));
            Assert.Equal(1L, await ScalarAsync(write, "SELECT COUNT(*) FROM RecordHistory"));
        }
        await using var read = await db.Factory.BeginReadAsync();
        foreach (var table in new[] { "Documents", "TypeVersions", "Records", "IndexValues", "UniqueValues", "RecordReferences", "RecordHistory", "Audit" })
            Assert.Equal(0L, await ScalarAsync(read, $"SELECT COUNT(*) FROM {table}"));
    }

    [Fact]
    public async Task Commit_retains_all_staged_revisions_and_correlated_history_and_audit()
    {
        await using var db = await Database.CreateAsync();
        var context = Context();
        context.RunId = Guid.NewGuid().ToString();
        var record = Record("Note", new JsonObject { ["code"] = "A", ["quantity"] = "2" });
        var id = record["id"]!.GetValue<string>();
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            await PublishAsync(write, Definition());
            await write.Records.SaveAsync(record, Definition(), context);
            record["revision"] = 2;
            record["data"]!["quantity"] = "10";
            await write.Records.SaveAsync(record, Definition(), context);
            record["revision"] = 3;
            record["data"]!["quantity"] = "11";
            await write.Records.SaveAsync(record, Definition(), context);
            Assert.Equal(3, (await write.Records.GetAsync(id))!["revision"]!.GetValue<int>());
            await write.CommitAsync();
        }
        await using var read = await db.Factory.BeginReadAsync();
        Assert.Equal(3L, await ScalarAsync(read, "SELECT COUNT(*) FROM RecordHistory"));
        Assert.Equal(3L, await ScalarAsync(read, "SELECT COUNT(*) FROM Audit"));
        Assert.Equal(context.CommandId, await ScalarAsync(read, "SELECT commandId FROM RecordHistory WHERE revision=2"));
        Assert.Equal(context.RunId, await ScalarAsync(read, "SELECT runId FROM RecordHistory WHERE revision=2"));
        Assert.Equal("10", await ScalarAsync(read, "SELECT json_extract(json,'$.data.quantity') FROM RecordHistory WHERE revision=2"));
        Assert.Equal(3L, await ScalarAsync(read, "SELECT json_extract(json,'$.sequence') FROM Audit WHERE sequence=3"));
        var copy = (await read.Records.GetAsync(id))!;
        copy["data"]!["quantity"] = "999";
        Assert.Equal("11", (await read.Records.GetAsync(id))!["data"]!["quantity"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unique_conflict_poisoning_prevents_commit_and_rolls_back_partial_record_and_history()
    {
        await using var db = await Database.CreateAsync();
        var first = Record("Note", new JsonObject { ["code"] = "A" });
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            await PublishAsync(write, Definition());
            await write.Records.SaveAsync(first, Definition(), Context());
            await write.CommitAsync();
        }
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            var context = Context();
            var error = await Assert.ThrowsAsync<BosException>(() => write.Records.SaveAsync(Record("Note", new JsonObject { ["code"] = "A" }), Definition(), context));
            Assert.Equal("UNIQUE_CONFLICT", error.Code);
            Assert.True(context.Poisoned);
            Assert.Equal("INVALID_STATE", (await Assert.ThrowsAsync<BosException>(() => write.CommitAsync())).Code);
        }
        await using var read = await db.Factory.BeginReadAsync();
        foreach (var table in new[] { "Records", "UniqueValues", "RecordHistory", "Audit" })
            Assert.Equal(1L, await ScalarAsync(read, $"SELECT COUNT(*) FROM {table}"));
    }

    [Fact]
    public async Task Repository_rejects_stale_revision_and_missing_published_type_fk()
    {
        await using var db = await Database.CreateAsync();
        var record = Record("Note", new JsonObject { ["code"] = "A" });
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            var error = await Assert.ThrowsAsync<BosException>(() => write.Records.SaveAsync(record, Definition(), Context()));
            Assert.Equal("INTERNAL_ERROR", error.Code);
            await write.RollbackAsync();
        }
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            await PublishAsync(write, Definition());
            await write.Records.SaveAsync(record, Definition(), Context());
            await write.CommitAsync();
        }
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            var error = await Assert.ThrowsAsync<BosException>(() => write.Records.SaveAsync(record, Definition(), Context()));
            Assert.Equal("REVISION_CONFLICT", error.Code);
        }
        await using var read = await db.Factory.BeginReadAsync();
        Assert.Equal(1L, await ScalarAsync(read, "SELECT MAX(revision) FROM RecordHistory"));
        Assert.Equal(1L, await ScalarAsync(read, "SELECT COUNT(*) FROM TypeVersions"));
    }

    [Fact]
    public async Task Document_list_fails_on_bound_overflow_and_published_types_are_immutable()
    {
        await using var db = await Database.CreateAsync();
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            for (var i = 0; i < 3; i++) await write.Documents.PutAsync("users", i.ToString(), new JsonObject { ["id"] = i });
            await PublishAsync(write, Definition());
            await write.CommitAsync();
        }
        await using (var read = await db.Factory.BeginReadAsync())
        {
            Assert.Equal("QUERY_LIMIT", (await Assert.ThrowsAsync<BosException>(() => read.Documents.ListAsync("users", 2))).Code);
            Assert.Equal(3, (await read.Documents.ListAsync("users", 3)).Count);
        }
        await using var mutation = await db.Factory.BeginWriteAsync();
        var published = (await mutation.Documents.GetAsync("typeVersions", "Note:1"))!;
        published["definition"]!["description"] = "Changed";
        Assert.Equal("INVALID_STATE", (await Assert.ThrowsAsync<BosException>(() => mutation.Documents.PutAsync("typeVersions", "Note:1", published))).Code);
    }

    [Fact]
    public async Task Every_connection_has_required_pragmas_and_read_transactions_are_enforced()
    {
        await using var db = await Database.CreateAsync();
        foreach (var write in new[] { false, true, false, true })
        {
            await using var uow = write ? await db.Factory.BeginWriteAsync() : await db.Factory.BeginReadAsync();
            Assert.Equal("wal", await ScalarAsync(uow, "PRAGMA journal_mode"));
            Assert.Equal(2L, await ScalarAsync(uow, "PRAGMA synchronous"));
            Assert.Equal(1L, await ScalarAsync(uow, "PRAGMA foreign_keys"));
            Assert.Equal(0L, await ScalarAsync(uow, "PRAGMA read_uncommitted"));
            Assert.Equal(1000L, await ScalarAsync(uow, "PRAGMA busy_timeout"));
            if (write)
            {
                await uow.Documents.PutAsync("test", "key", new JsonObject { ["ok"] = true });
                await uow.CommitAsync();
            }
            else
            {
                Assert.Equal("POLICY_VIOLATION", (await Assert.ThrowsAsync<BosException>(() => uow.Documents.PutAsync("test", "key", new JsonObject()))).Code);
                await Assert.ThrowsAsync<SqliteException>(() => ScalarAsync(uow, "INSERT INTO Documents VALUES('test','raw','{}')"));
            }
        }
    }

    [Fact]
    public async Task Begin_immediate_reserves_writer_before_any_repository_write_and_wal_reader_sees_snapshot()
    {
        await using var db = await Database.CreateAsync();
        await using var writer = await db.Factory.BeginWriteAsync();
        IStorageDiagnostics diagnostics = db.Factory;
        Assert.Equal(1, diagnostics.ActiveWriters);
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<BosException>(() => db.Factory.BeginWriteAsync());
        Assert.Equal("STORAGE_BUSY", error.Code);
        Assert.Equal(1, diagnostics.ActiveWriters);
        Assert.Equal(0, diagnostics.ActiveReaders);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(4));
        await writer.Documents.PutAsync("test", "staged", new JsonObject { ["value"] = "new" });
        await using var reader = await db.Factory.BeginReadAsync();
        Assert.Null(await reader.Documents.GetAsync("test", "staged"));
        await writer.CommitAsync();
        Assert.Null(await reader.Documents.GetAsync("test", "staged"));
        await reader.RollbackAsync();
        await using var fresh = await db.Factory.BeginReadAsync();
        Assert.NotNull(await fresh.Documents.GetAsync("test", "staged"));
    }

    [Fact]
    public async Task Index_states_and_typed_numeric_order_survive_real_sqlite_storage()
    {
        await using var db = await Database.CreateAsync();
        await using var write = await db.Factory.BeginWriteAsync();
        await PublishAsync(write, Definition());
        foreach (var data in new[]
        {
            new JsonObject { ["code"] = "missing" }, new JsonObject { ["code"] = "null", ["quantity"] = null },
            new JsonObject { ["code"] = "two", ["quantity"] = "2" }, new JsonObject { ["code"] = "ten", ["quantity"] = "10" }
        }) await write.Records.SaveAsync(Record("Note", data), Definition(), Context());
        Assert.Equal(1L, await ScalarAsync(write, "SELECT COUNT(*) FROM IndexValues WHERE field='quantity' AND state=0"));
        Assert.Equal(1L, await ScalarAsync(write, "SELECT COUNT(*) FROM IndexValues WHERE field='quantity' AND state=1"));
        Assert.Equal("\"2\",\"10\"", await ScalarAsync(write, "SELECT group_concat(value,',') FROM (SELECT value FROM IndexValues WHERE field='quantity' AND state=2 ORDER BY orderKey COLLATE BINARY)"));
        await write.CommitAsync();
    }

    [Fact]
    public async Task Record_service_applies_defaults_shallow_replacement_and_revision_checks_without_noop_history()
    {
        await using var db = await Database.CreateAsync();
        await using (var write = await db.Factory.BeginWriteAsync()) { await PublishAsync(write, Definition()); await write.CommitAsync(); }
        var service = Service();
        var context = Context();
        string id;
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            var created = (await service.InvokeAsync("records.create", new JsonObject { ["type"] = "Note", ["data"] = new JsonObject { ["code"] = "A", ["details"] = new JsonObject { ["first"] = "old", ["second"] = "remove" } } }, context, write))!.AsObject();
            id = created["id"]!.GetValue<string>();
            Assert.Equal("0", created["data"]!["quantity"]!.GetValue<string>());
            var updated = (await service.InvokeAsync("records.update", UpdateArgs(id, 1, new JsonObject { ["details"] = new JsonObject { ["first"] = "new" } }), context, write))!;
            Assert.Equal(2, updated["revision"]!.GetValue<int>());
            Assert.False(updated["data"]!["details"]!.AsObject().ContainsKey("second"));
            Assert.Contains(id, context.CreatedRecordIds);
            Assert.Contains(id, context.ChangedRecordIds);
            Assert.Contains("Note", context.ReadTypes);
            Assert.Equal(2, context.Budget.Mutations);
            await write.CommitAsync();
        }
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            Assert.Equal("NO_CHANGE", (await Assert.ThrowsAsync<BosException>(() => service.InvokeAsync("records.update", UpdateArgs(id, 2, new JsonObject { ["quantity"] = "00" }), Context(), write))).Code);
            Assert.Equal(2L, await ScalarAsync(write, "SELECT COUNT(*) FROM RecordHistory"));
        }
        await using (var write = await db.Factory.BeginWriteAsync())
            Assert.Equal("REVISION_CONFLICT", (await Assert.ThrowsAsync<BosException>(() => service.InvokeAsync("records.update", UpdateArgs(id, 1, new JsonObject { ["quantity"] = "3" }), Context(), write))).Code);
    }

    [Fact]
    public async Task Record_service_enforces_operation_and_nested_managed_field_owners()
    {
        await using var db = await Database.CreateAsync();
        var guarded = Definition();
        guarded["accessMode"] = "operationsOnly";
        guarded["allowedOperations"] = new JsonArray("CreateNote");
        guarded["fields"]!["details"]!["fields"]!["first"]!["managedBy"] = new JsonArray("SetSecret");
        await using (var write = await db.Factory.BeginWriteAsync()) { await PublishAsync(write, guarded); await write.CommitAsync(); }
        var args = new JsonObject { ["type"] = "Note", ["data"] = new JsonObject { ["code"] = "A" } };
        await using (var write = await db.Factory.BeginWriteAsync())
            Assert.Equal("POLICY_VIOLATION", (await Assert.ThrowsAsync<BosException>(() => Service().InvokeAsync("records.create", args, Context(), write))).Code);
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            var context = Context(); context.GuardOwner = "CreateNote";
            args["data"]!["details"] = new JsonObject { ["first"] = "secret" };
            Assert.Equal("POLICY_VIOLATION", (await Assert.ThrowsAsync<BosException>(() => Service().InvokeAsync("records.create", args, context, write))).Code);
        }
    }

    [Fact]
    public async Task Unreadable_get_is_not_found_and_pinned_mismatch_precedes_exposure()
    {
        await using var db = await Database.CreateAsync();
        var record = Record("Note", new JsonObject { ["code"] = "A" });
        var args = new JsonObject { ["type"] = "Note", ["id"] = record["id"]!.DeepClone() };
        await using (var write = await db.Factory.BeginWriteAsync())
        { await PublishAsync(write, Definition()); await write.Records.SaveAsync(record, Definition(), Context()); await write.CommitAsync(); }
        await using var read = await db.Factory.BeginReadAsync();
        Assert.Equal("NOT_FOUND", (await Assert.ThrowsAsync<BosException>(() => Service(new Auth("record.read:Note")).InvokeAsync("records.get", args, Context(), read))).Code);
        var context = Context(); context.TypeVersions["Note"] = [2];
        Assert.Equal("SCHEMA_VERSION_MISMATCH", (await Assert.ThrowsAsync<BosException>(() => Service().InvokeAsync("records.get", args, context, read))).Code);
        Assert.Equal(0, context.Budget.Returned);
    }

    [Fact]
    public async Task Creates_use_active_or_single_pinned_version_and_queries_accept_multiple_pinned_versions()
    {
        await using var db = await Database.CreateAsync();
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            await PublishAsync(write, Definition(), 1);
            await PublishAsync(write, Definition(), 2);
            await write.CommitAsync();
        }
        var service = Service();
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            var active = (await service.InvokeAsync("records.create", CreateArgs("active"), Context(), write))!;
            Assert.Equal(2, active["typeVersion"]!.GetValue<int>());
            var pinned = Context(); pinned.TypeVersions["Note"] = [1];
            var old = (await service.InvokeAsync("records.create", CreateArgs("pinned"), pinned, write))!;
            Assert.Equal(1, old["typeVersion"]!.GetValue<int>());
            await write.CommitAsync();
        }
        await using (var read = await db.Factory.BeginReadAsync())
        {
            var context = Context(); context.TypeVersions["Note"] = [1, 2];
            var page = (await service.InvokeAsync("records.list", new JsonObject { ["type"] = "Note" }, context, read))!;
            Assert.Equal(2, page["items"]!.AsArray().Count);
            var count = (await service.InvokeAsync("records.count", new JsonObject { ["type"] = "Note" }, context, read))!;
            Assert.Equal("2", count["count"]!.GetValue<string>());
        }
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            var context = Context(); context.TypeVersions["Note"] = [1, 2];
            Assert.Equal("SCHEMA_VERSION_MISMATCH", (await Assert.ThrowsAsync<BosException>(() => service.InvokeAsync("records.create", CreateArgs("ambiguous"), context, write))).Code);
        }
    }

    [Fact]
    public async Task Nested_references_restrict_delete_then_tombstone_releases_outgoing_links_and_unique_values()
    {
        await using var db = await Database.CreateAsync();
        var definition = Definition();
        definition["fields"]!["links"] = new JsonObject { ["kind"] = "array", ["maxItems"] = 10, ["items"] = new JsonObject { ["kind"] = "object", ["fields"] = new JsonObject { ["target"] = new JsonObject { ["kind"] = "reference", ["targetType"] = "Note" } } } };
        var service = Service();
        string targetId, sourceId;
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            await PublishAsync(write, definition);
            var context = Context();
            var target = (await service.InvokeAsync("records.create", CreateArgs("target"), context, write))!;
            targetId = target["id"]!.GetValue<string>();
            var sourceArgs = CreateArgs("source");
            sourceArgs["data"]!["links"] = new JsonArray(new JsonObject { ["target"] = targetId });
            var source = (await service.InvokeAsync("records.create", sourceArgs, context, write))!;
            sourceId = source["id"]!.GetValue<string>();
            Assert.True(await write.Records.HasIncomingReferencesAsync(targetId));
            Assert.Equal("links[0].target", await ScalarAsync(write, "SELECT path FROM RecordReferences"));
            await write.CommitAsync();
        }
        await using (var write = await db.Factory.BeginWriteAsync())
            Assert.Equal("IN_USE", (await Assert.ThrowsAsync<BosException>(() => service.InvokeAsync("records.delete", DeleteArgs(targetId), Context(), write))).Code);
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            var result = (await service.InvokeAsync("records.delete", DeleteArgs(sourceId), Context(), write))!;
            Assert.True(result["deleted"]!.GetValue<bool>());
            Assert.False(await write.Records.HasIncomingReferencesAsync(targetId));
            Assert.Equal(0L, await ScalarAsync(write, "SELECT COUNT(*) FROM UniqueValues WHERE value='\"source\"'"));
            await write.CommitAsync();
        }
        await using var read = await db.Factory.BeginReadAsync();
        Assert.True((await read.Records.GetAsync(sourceId))!["deleted"]!.GetValue<bool>());
        Assert.Equal("NOT_FOUND", (await Assert.ThrowsAsync<BosException>(() => service.InvokeAsync("records.get", new JsonObject { ["type"] = "Note", ["id"] = sourceId }, Context(), read))).Code);
    }

    [Fact]
    public async Task Create_delete_in_one_invocation_is_rejected_and_unreadable_nested_target_is_not_disclosed()
    {
        await using var db = await Database.CreateAsync();
        var definition = Definition();
        definition["fields"]!["parent"] = new JsonObject { ["kind"] = "reference", ["targetType"] = "Hidden" };
        await using (var write = await db.Factory.BeginWriteAsync()) { await PublishAsync(write, definition); await write.CommitAsync(); }
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            var context = Context();
            var created = (await Service().InvokeAsync("records.create", CreateArgs("A"), context, write))!;
            Assert.Equal("INVALID_STATE", (await Assert.ThrowsAsync<BosException>(() => Service().InvokeAsync("records.delete", DeleteArgs(created["id"]!.GetValue<string>()), context, write))).Code);
            Assert.Equal("INVALID_STATE", (await Assert.ThrowsAsync<BosException>(() => write.CommitAsync())).Code);
        }
        await using (var write = await db.Factory.BeginWriteAsync())
        {
            var args = CreateArgs("B"); args["data"]!["parent"] = Guid.NewGuid().ToString();
            var error = await Assert.ThrowsAsync<BosException>(() => Service(new Auth("record.read:Hidden")).InvokeAsync("records.create", args, Context(), write));
            Assert.Equal("Reference is unavailable.", Assert.Single(error.Details).Message);
        }
    }

    [Fact]
    public async Task Diagnostics_track_owned_uows_until_disposal_and_release_exactly_once()
    {
        await using var db = await Database.CreateAsync();
        IStorageDiagnostics diagnostics = db.Factory;
        Assert.Equal(0, diagnostics.ActiveReaders);
        Assert.Equal(0, diagnostics.ActiveWriters);
        await using var reader = await db.Factory.BeginReadAsync();
        await using var writer = await db.Factory.BeginWriteAsync();
        Assert.Equal(1, diagnostics.ActiveReaders);
        Assert.Equal(1, diagnostics.ActiveWriters);
        await reader.CommitAsync();
        await writer.RollbackAsync();
        Assert.Equal(1, diagnostics.ActiveReaders);
        Assert.Equal(1, diagnostics.ActiveWriters);
        await reader.DisposeAsync();
        await writer.DisposeAsync();
        await reader.DisposeAsync();
        await writer.DisposeAsync();
        Assert.Equal(0, diagnostics.ActiveReaders);
        Assert.Equal(0, diagnostics.ActiveWriters);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Factory.BeginReadAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Factory.BeginWriteAsync(cancelled.Token));
        Assert.Equal(0, diagnostics.ActiveReaders);
        Assert.Equal(0, diagnostics.ActiveWriters);

        var readers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => db.Factory.BeginReadAsync())));
        try
        {
            Assert.Equal(4, diagnostics.ActiveReaders);
            await Task.WhenAll(readers.SelectMany(uow => new[] { uow.DisposeAsync().AsTask(), uow.DisposeAsync().AsTask() }));
            Assert.Equal(0, diagnostics.ActiveReaders);
        }
        finally { foreach (var uow in readers) await uow.DisposeAsync(); }
    }

    [Fact]
    public async Task Diagnostics_release_after_failed_commit_and_even_when_disposal_rollback_throws()
    {
        await using var db = await Database.CreateAsync();
        IStorageDiagnostics diagnostics = db.Factory;
        await using (var writer = await db.Factory.BeginWriteAsync())
        {
            await Assert.ThrowsAsync<BosException>(() => writer.Documents.DeleteAsync("typeVersions", "Note:1"));
            await Assert.ThrowsAsync<BosException>(() => writer.CommitAsync());
            Assert.Equal(1, diagnostics.ActiveWriters);
        }
        Assert.Equal(0, diagnostics.ActiveWriters);

        await using var abandoned = await db.Factory.BeginWriteAsync();
        await abandoned.Transaction.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => abandoned.DisposeAsync().AsTask());
        Assert.Equal(0, diagnostics.ActiveWriters);
        await abandoned.DisposeAsync();
        Assert.Equal(0, diagnostics.ActiveWriters);
    }

    [Theory]
    [InlineData("flowDrafts")]
    [InlineData("flowVersions")]
    [InlineData("typeDrafts")]
    [InlineData("typeVersions")]
    public async Task Document_source_is_verbatim_in_sqlite_get_and_list(string collection)
    {
        await using var db = await Database.CreateAsync();
        const string source = "# Cafe\u0301 comment\r\nname: Note\r\nsteps: [] # keep this comment\r\n";
        var key = collection.EndsWith("Versions", StringComparison.Ordinal) ? "Note:1" : "Note";
        var document = new JsonObject
        {
            ["name"] = "Note", ["version"] = 1, ["source"] = source,
            ["definition"] = new JsonObject { ["source"] = source }, ["description"] = "Cafe\u0301"
        };
        await using (var writer = await db.Factory.BeginWriteAsync())
        {
            await writer.Documents.PutAsync(collection, key, document);
            // An identical republish must compare using source-preserving serialization too.
            await writer.Documents.PutAsync(collection, key, document);
            await writer.CommitAsync();
        }
        await using var reader = await db.Factory.BeginReadAsync();
        using var command = reader.Connection.CreateCommand();
        command.Transaction = reader.Transaction;
        command.CommandText = "SELECT json FROM Documents WHERE collection=$collection AND key=$key";
        var collectionParameter = command.CreateParameter(); collectionParameter.ParameterName = "$collection"; collectionParameter.Value = collection; command.Parameters.Add(collectionParameter);
        var keyParameter = command.CreateParameter(); keyParameter.ParameterName = "$key"; keyParameter.Value = key; command.Parameters.Add(keyParameter);
        // Inspect raw SQLite JSON independently of CanonicalJson so reader normalization cannot mask a bad write.
        using var raw = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        Assert.Equal(Encoding.UTF8.GetBytes(source), Encoding.UTF8.GetBytes(raw.RootElement.GetProperty("source").GetString()!));
        Assert.Equal(source, raw.RootElement.GetProperty("definition").GetProperty("source").GetString());
        Assert.Equal("Caf\u00e9", raw.RootElement.GetProperty("description").GetString());
        var stored = (await reader.Documents.GetAsync(collection, key))!;
        Assert.Equal(source, stored["source"]!.GetValue<string>());
        Assert.Equal(source, stored["definition"]!["source"]!.GetValue<string>());
        var listed = Assert.Single(await reader.Documents.ListAsync(collection));
        Assert.Equal(source, listed["source"]!.GetValue<string>());
        Assert.Equal(source, listed["definition"]!["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task Published_source_cannot_be_changed_even_to_unicode_equivalent_text()
    {
        await using var db = await Database.CreateAsync();
        var document = new JsonObject { ["name"] = "Flow", ["version"] = 1, ["source"] = "# Cafe\u0301\r\nsteps: []\r\n" };
        await using (var writer = await db.Factory.BeginWriteAsync())
        {
            await writer.Documents.PutAsync("flowVersions", "Flow:1", document);
            await writer.CommitAsync();
        }
        await using var mutation = await db.Factory.BeginWriteAsync();
        document["source"] = document["source"]!.GetValue<string>().Normalize();
        Assert.Equal("INVALID_STATE", (await Assert.ThrowsAsync<BosException>(() => mutation.Documents.PutAsync("flowVersions", "Flow:1", document))).Code);
    }

    [Fact]
    public async Task Business_field_named_source_keeps_schema_normalization_in_document_default_and_record_json()
    {
        await using var db = await Database.CreateAsync();
        var definition = Definition();
        definition["fields"]!["source"] = new JsonObject { ["kind"] = "string", ["default"] = "Cafe\u0301\r\n# literal business value" };
        definition = (JsonObject)CanonicalJson.Normalize(definition)!;
        var expected = "Caf\u00e9\r\n# literal business value";
        await using (var writer = await db.Factory.BeginWriteAsync())
        {
            await PublishAsync(writer, definition);
            await Service().InvokeAsync("records.create", CreateArgs("default"), Context(), writer);
            var explicitArgs = CreateArgs("explicit");
            explicitArgs["data"]!["source"] = "Cafe\u0301\r\n# literal business value";
            await Service().InvokeAsync("records.create", explicitArgs, Context(), writer);
            await writer.CommitAsync();
        }
        await using var reader = await db.Factory.BeginReadAsync();
        var rawDocument = (string)(await ScalarAsync(reader, "SELECT json FROM Documents WHERE collection='typeVersions' AND key='Note:1'"))!;
        using var published = JsonDocument.Parse(rawDocument);
        Assert.Equal(expected, published.RootElement.GetProperty("definition").GetProperty("fields").GetProperty("source").GetProperty("default").GetString());
        using var command = reader.Connection.CreateCommand(); command.Transaction = reader.Transaction;
        command.CommandText = "SELECT json FROM Records ORDER BY id";
        using var records = await command.ExecuteReaderAsync();
        var count = 0;
        while (await records.ReadAsync())
        {
            using var rawRecord = JsonDocument.Parse(records.GetString(0));
            Assert.Equal(expected, rawRecord.RootElement.GetProperty("data").GetProperty("source").GetString());
            count++;
        }
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("document", false)]
    [InlineData("record", true)]
    [InlineData("native", false)]
    public async Task Sqlite_full_rolls_back_all_staged_state_and_allows_a_subsequent_connection(string failure, bool explicitRollback)
    {
        await using var db = await Database.CreateAsync();
        var (definition, target) = await SeedStorageFaultAsync(db.Factory);
        JsonObject source;
        await using (var writer = await db.Factory.BeginWriteAsync())
        {
            source = await StageStorageFaultAsync(writer, definition, target);
            var originalLimit = (long)(await ScalarAsync(writer, "PRAGMA max_page_count"))!;
            var pages = (long)(await ScalarAsync(writer, "PRAGMA page_count"))!;
            Assert.Equal(pages, await ScalarAsync(writer, $"PRAGMA max_page_count={pages}"));
            try
            {
                if (failure == "native")
                {
                    // Capture the actual native primary code independently of BOS translation.
                    var native = await Assert.ThrowsAsync<SqliteException>(() => ScalarAsync(writer,
                        "INSERT INTO Documents(collection,key,json) VALUES('fault','oversize',json_object('payload',hex(zeroblob(100000))))"));
                    Assert.Equal(13, native.SqliteErrorCode); // SQLITE_FULL, not a simulated application failure.
                }
                else
                {
                    Task FailAsync()
                    {
                        if (failure == "document") return writer.Documents.PutAsync("fault", "oversize", new JsonObject { ["payload"] = new string('x', 200000) });
                        var data = new JsonObject
                        {
                            ["code"] = "over-quota",
                            ["payload"] = new JsonArray(Enumerable.Range(0, 12).Select(_ => (JsonNode)JsonValue.Create(new string('x', 16000))!).ToArray())
                        };
                        return writer.Records.SaveAsync(Record("Note", Schema.Normalize(definition, data)), definition, Context());
                    }
                    var translated = await Assert.ThrowsAsync<BosException>(FailAsync);
                    Assert.Equal("CAPACITY_EXCEEDED", translated.Code);
                    Assert.Equal(503, translated.Status);
                    Assert.False(translated.Retryable);
                    Assert.Equal("INVALID_STATE", (await Assert.ThrowsAsync<BosException>(() => writer.CommitAsync())).Code);
                }
            }
            finally
            {
                // max_page_count is a test-only, connection-local quota; release it before pooling the handle.
                // SQLite auto-rolls back on FULL before the provider detaches its transaction. Release only
                // the injected quota through the native handle, leaving normal UoW cleanup under test below.
                Assert.Equal(0, SQLitePCL.raw.sqlite3_exec(((SqliteConnection)writer.Connection).Handle, $"PRAGMA max_page_count={originalLimit}"));
            }
            if (explicitRollback) await writer.RollbackAsync();
        }
        Assert.Equal(0, db.Factory.ActiveWriters);
        await AssertStorageFaultRolledBackAsync(db.Factory, target, source);
        await AssertStorageResponsiveAsync(db.Factory, definition, source);
    }

    [Fact]
    public async Task Sqlite_busy_contender_cannot_change_staged_writer_and_rollback_releases_all_state_and_locks()
    {
        await using var db = await Database.CreateAsync();
        var (definition, target) = await SeedStorageFaultAsync(db.Factory);
        JsonObject source;
        await using (var writer = await db.Factory.BeginWriteAsync())
        {
            source = await StageStorageFaultAsync(writer, definition, target);
            await using var contender = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = db.Factory.DatabasePath, DefaultTimeout = 1, Pooling = false
            }.ToString());
            await contender.OpenAsync();
            var native = Assert.Throws<SqliteException>(() => contender.BeginTransaction(deferred: false));
            Assert.Equal(5, native.SqliteErrorCode); // Actual SQLITE_BUSY from a separate connection.
            var translated = await Assert.ThrowsAsync<BosException>(() => db.Factory.BeginWriteAsync());
            Assert.Equal("STORAGE_BUSY", translated.Code);
            Assert.Equal(503, translated.Status);
            Assert.True(translated.Retryable);
            Assert.Equal(1, db.Factory.ActiveWriters);
            Assert.Equal(3L, await ScalarAsync(writer, "SELECT COUNT(*) FROM RecordHistory"));
            Assert.Equal(1L, await ScalarAsync(writer, "SELECT COUNT(*) FROM RecordReferences"));
            await writer.RollbackAsync();
        }
        Assert.Equal(0, db.Factory.ActiveWriters);
        await AssertStorageFaultRolledBackAsync(db.Factory, target, source);
        await AssertStorageResponsiveAsync(db.Factory, definition, source);
    }

    private static async Task<(JsonObject Definition, JsonObject Target)> SeedStorageFaultAsync(SqliteUnitOfWorkFactory factory)
    {
        var definition = Definition();
        definition["fields"]!["links"] = new JsonObject
        {
            ["kind"] = "array", ["maxItems"] = 10,
            ["items"] = new JsonObject { ["kind"] = "reference", ["targetType"] = "Note" }
        };
        definition["fields"]!["payload"] = new JsonObject
        {
            ["kind"] = "array", ["maxItems"] = 20, ["items"] = new JsonObject { ["kind"] = "string" }
        };
        var target = Record("Note", new JsonObject { ["code"] = "committed-target", ["quantity"] = "1" });
        await using var writer = await factory.BeginWriteAsync();
        await PublishAsync(writer, definition);
        await writer.Records.SaveAsync(target, definition, Context());
        await writer.CommitAsync();
        return (definition, target);
    }

    private static async Task<JsonObject> StageStorageFaultAsync(IUnitOfWork writer, JsonObject definition, JsonObject target)
    {
        var context = Context();
        var changed = (JsonObject)target.DeepClone();
        changed["revision"] = 2;
        changed["data"]!["code"] = "changed-target";
        changed["data"]!["quantity"] = "10";
        await writer.Records.SaveAsync(changed, definition, context);
        var source = Record("Note", new JsonObject
        {
            ["code"] = "staged-source", ["quantity"] = "2",
            ["links"] = new JsonArray(target["id"]!.DeepClone())
        });
        await writer.Records.SaveAsync(source, definition, context);
        await writer.Documents.PutAsync("fault", "staged", new JsonObject { ["provisional"] = true });
        Assert.Equal(2L, await ScalarAsync(writer, "SELECT COUNT(*) FROM Records"));
        Assert.Equal(4L, await ScalarAsync(writer, "SELECT COUNT(*) FROM IndexValues"));
        Assert.Equal(2L, await ScalarAsync(writer, "SELECT COUNT(*) FROM UniqueValues"));
        Assert.Equal(1L, await ScalarAsync(writer, "SELECT COUNT(*) FROM RecordReferences"));
        Assert.Equal(3L, await ScalarAsync(writer, "SELECT COUNT(*) FROM RecordHistory"));
        Assert.Equal(3L, await ScalarAsync(writer, "SELECT COUNT(*) FROM Audit"));
        return source;
    }

    private static async Task AssertStorageFaultRolledBackAsync(SqliteUnitOfWorkFactory factory, JsonObject target, JsonObject source)
    {
        await using var reader = await factory.BeginReadAsync();
        Assert.Equal(CanonicalJson.Serialize(target), CanonicalJson.Serialize(await reader.Records.GetAsync(target["id"]!.GetValue<string>())));
        Assert.Null(await reader.Records.GetAsync(source["id"]!.GetValue<string>()));
        Assert.Equal(1L, await ScalarAsync(reader, "SELECT COUNT(*) FROM Records"));
        Assert.Equal(2L, await ScalarAsync(reader, "SELECT COUNT(*) FROM IndexValues"));
        Assert.Equal("\"1\"", await ScalarAsync(reader, "SELECT value FROM IndexValues WHERE field='quantity'"));
        Assert.Equal(1L, await ScalarAsync(reader, "SELECT COUNT(*) FROM UniqueValues"));
        Assert.Equal("\"committed-target\"", await ScalarAsync(reader, "SELECT value FROM UniqueValues"));
        Assert.Equal(0L, await ScalarAsync(reader, "SELECT COUNT(*) FROM RecordReferences"));
        Assert.Equal(1L, await ScalarAsync(reader, "SELECT COUNT(*) FROM RecordHistory"));
        Assert.Equal(1L, await ScalarAsync(reader, "SELECT COUNT(*) FROM Audit"));
        Assert.Null(await reader.Documents.GetAsync("fault", "staged"));
        Assert.Null(await reader.Documents.GetAsync("fault", "oversize"));
        Assert.Equal("ok", await ScalarAsync(reader, "PRAGMA integrity_check"));
        Assert.Null(await ScalarAsync(reader, "PRAGMA foreign_key_check"));
    }

    private static async Task AssertStorageResponsiveAsync(SqliteUnitOfWorkFactory factory, JsonObject definition, JsonObject source)
    {
        // Fresh ownership can reuse the rolled-back ID and unique value and add its reference/history/audit.
        await using (var writer = await factory.BeginWriteAsync())
        {
            await writer.Records.SaveAsync(source, definition, Context());
            await writer.CommitAsync();
        }
        await using var reader = await factory.BeginReadAsync();
        Assert.Equal(CanonicalJson.Serialize(source), CanonicalJson.Serialize(await reader.Records.GetAsync(source["id"]!.GetValue<string>())));
        Assert.Equal(2L, await ScalarAsync(reader, "SELECT COUNT(*) FROM RecordHistory"));
        Assert.Equal(2L, await ScalarAsync(reader, "SELECT COUNT(*) FROM Audit"));
        Assert.Equal(1L, await ScalarAsync(reader, "SELECT COUNT(*) FROM RecordReferences"));
        Assert.Equal("ok", await ScalarAsync(reader, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task Initialize_stamps_schema_one_and_is_idempotent_without_changing_documents_or_audit()
    {
        await using var db = await Database.CreateAsync();
        await using (var writer = await db.Factory.BeginWriteAsync())
        {
            Assert.Equal(1L, await ScalarAsync(writer, "PRAGMA user_version"));
            await writer.Documents.PutAsync("test", "marker", new JsonObject { ["value"] = "retained" });
            await writer.Documents.AppendAuditAsync(new JsonObject { ["method"] = "test.marker" });
            await writer.CommitAsync();
        }
        await db.Factory.InitializeAsync();
        await db.Factory.InitializeAsync();
        await using var reader = await db.Factory.BeginReadAsync();
        Assert.Equal(1L, await ScalarAsync(reader, "PRAGMA user_version"));
        Assert.Equal("retained", (await reader.Documents.GetAsync("test", "marker"))!["value"]!.GetValue<string>());
        Assert.Equal(1L, await ScalarAsync(reader, "SELECT COUNT(*) FROM Audit"));
        Assert.Equal("ok", await ScalarAsync(reader, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task Initialize_adopts_unreleased_version_zero_preview_documents_without_data_loss()
    {
        await using var db = UninitializedDatabase();
        await using (var seed = RawStorageConnection(db.Factory.DatabasePath))
        {
            await seed.OpenAsync();
            using var command = seed.CreateCommand();
            command.CommandText = """
                CREATE TABLE Documents(collection TEXT NOT NULL,key TEXT NOT NULL,json TEXT NOT NULL CHECK(json_valid(json)),PRIMARY KEY(collection,key));
                INSERT INTO Documents VALUES('test','preview','{"value":"retained"}');
                PRAGMA user_version=0;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await db.Factory.InitializeAsync();
        await using var reader = await db.Factory.BeginReadAsync();
        Assert.Equal(1L, await ScalarAsync(reader, "PRAGMA user_version"));
        Assert.Equal("retained", (await reader.Documents.GetAsync("test", "preview"))!["value"]!.GetValue<string>());
        Assert.Equal(0L, await ScalarAsync(reader, "SELECT COUNT(*) FROM Records"));
        Assert.Equal(0L, await ScalarAsync(reader, "SELECT COUNT(*) FROM Audit"));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(-1, false)]
    [InlineData(2, true)]
    [InlineData(-1, true)]
    public async Task Unsupported_schema_is_refused_by_initialize_and_every_begin_without_changing_any_file(int version, bool wal)
    {
        await using var db = UninitializedDatabase();
        await using (var seed = RawStorageConnection(db.Factory.DatabasePath))
        {
            await seed.OpenAsync();
            using var command = seed.CreateCommand();
            command.CommandText = $"PRAGMA journal_mode={(wal ? "WAL" : "DELETE")}; CREATE TABLE Sentinel(value TEXT); INSERT INTO Sentinel VALUES('original'); PRAGMA user_version={version};";
            await command.ExecuteNonQueryAsync();
        }
        var original = await File.ReadAllBytesAsync(db.Factory.DatabasePath);
        var directory = Path.GetDirectoryName(db.Factory.DatabasePath)!;
        Assert.Equal(new[] { "bos.db" }, Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order().ToArray());
        Func<Task>[] attempts =
        [
            () => db.Factory.InitializeAsync(),
            async () => { await using var reader = await db.Factory.BeginReadAsync(); },
            async () => { await using var writer = await db.Factory.BeginWriteAsync(); }
        ];
        foreach (var attempt in attempts)
        {
            var error = await Assert.ThrowsAsync<BosException>(attempt);
            Assert.Equal("DEPENDENCY_UNAVAILABLE", error.Code);
            Assert.Equal(original, await File.ReadAllBytesAsync(db.Factory.DatabasePath));
            Assert.Equal(new[] { "bos.db" }, Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order().ToArray());
            Assert.Equal(0, db.Factory.ActiveReaders);
            Assert.Equal(0, db.Factory.ActiveWriters);
        }
        await using var inspect = RawStorageConnection(db.Factory.DatabasePath, SqliteOpenMode.ReadOnly);
        await inspect.OpenAsync();
        using var check = inspect.CreateCommand();
        check.CommandText = "SELECT group_concat(name) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'";
        Assert.Equal("Sentinel", await check.ExecuteScalarAsync());
        check.CommandText = "PRAGMA user_version";
        Assert.Equal((long)version, await check.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Begin_rechecks_a_store_that_became_incompatible_after_factory_initialization(bool keepWalOpen)
    {
        await using var db = await Database.CreateAsync();
        // A fresh physical connection must inspect the current file, not cache the factory's initial version.
        await using var change = RawStorageConnection(db.Factory.DatabasePath);
        await change.OpenAsync();
        using (var command = change.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=2";
            await command.ExecuteNonQueryAsync();
        }
        if (keepWalOpen) Assert.True(File.Exists(db.Factory.DatabasePath + "-wal"));
        else await change.DisposeAsync();
        Assert.Equal("DEPENDENCY_UNAVAILABLE", (await Assert.ThrowsAsync<BosException>(() => db.Factory.BeginReadAsync())).Code);
        Assert.Equal("DEPENDENCY_UNAVAILABLE", (await Assert.ThrowsAsync<BosException>(() => db.Factory.BeginWriteAsync())).Code);
        Assert.Equal(0, db.Factory.ActiveReaders);
        Assert.Equal(0, db.Factory.ActiveWriters);
    }

    [Fact]
    public async Task Initialization_ddl_failure_rolls_back_created_tables_and_keeps_version_zero_until_successful_retry()
    {
        await using var db = UninitializedDatabase();
        await using (var seed = RawStorageConnection(db.Factory.DatabasePath))
        {
            await seed.OpenAsync();
            using var command = seed.CreateCommand();
            // Collision injects a real DDL error after Documents and TypeVersions have been created.
            command.CommandText = "CREATE TABLE Sentinel(value TEXT); INSERT INTO Sentinel VALUES('original'); CREATE VIEW Records AS SELECT 1 AS id; PRAGMA user_version=0;";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal("INTERNAL_ERROR", (await Assert.ThrowsAsync<BosException>(() => db.Factory.InitializeAsync())).Code);
        Assert.Equal(0, db.Factory.ActiveWriters);
        await using (var inspect = RawStorageConnection(db.Factory.DatabasePath))
        {
            await inspect.OpenAsync();
            using var command = inspect.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('Documents','TypeVersions','Audit')";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT value FROM Sentinel";
            Assert.Equal("original", await command.ExecuteScalarAsync());
            command.CommandText = "DROP VIEW Records";
            await command.ExecuteNonQueryAsync();
        }
        await db.Factory.InitializeAsync();
        await using var reader = await db.Factory.BeginReadAsync();
        Assert.Equal(1L, await ScalarAsync(reader, "PRAGMA user_version"));
        Assert.Equal("original", await ScalarAsync(reader, "SELECT value FROM Sentinel"));
        Assert.Equal(0L, await ScalarAsync(reader, "SELECT COUNT(*) FROM Records"));
        Assert.Equal("ok", await ScalarAsync(reader, "PRAGMA integrity_check"));
    }

    private static Database UninitializedDatabase()
    {
        var directory = Directory.CreateTempSubdirectory("bos-storage-").FullName;
        return new Database(directory, new SqliteUnitOfWorkFactory(Path.Combine(directory, "bos.db")));
    }

    private static SqliteConnection RawStorageConnection(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 1
    }.ToString());

    private static JsonObject Definition() => CanonicalJson.Parse("""
        {"name":"Note","accessMode":"direct","fields":{
          "code":{"kind":"string","required":true,"unique":true},
          "quantity":{"kind":"integer","nullable":true,"indexed":true,"default":"0"},
          "details":{"kind":"object","fields":{"first":{"kind":"string"},"second":{"kind":"string"}}}
        }}
        """)!.AsObject();
    private static async Task PublishAsync(IUnitOfWork uow, JsonObject definition, int version = 1)
    {
        var name = definition["name"]!.GetValue<string>();
        await uow.Documents.PutAsync("typeVersions", $"{name}:{version}", new JsonObject { ["name"] = name, ["version"] = version, ["definition"] = definition.DeepClone() });
        await uow.Documents.PutAsync("typeActive", name, new JsonObject { ["name"] = name, ["version"] = version });
    }
    private static JsonObject Record(string type, JsonObject data) => new()
    {
        ["id"] = Guid.NewGuid().ToString(), ["type"] = type, ["typeVersion"] = 1, ["revision"] = 1,
        ["deleted"] = false, ["data"] = data, ["createdAt"] = "2026-01-01T00:00:00.000Z",
        ["updatedAt"] = "2026-01-01T00:00:00.000Z", ["createdBy"] = "test", ["updatedBy"] = "test"
    };
    private static JsonObject CreateArgs(string code) => new() { ["type"] = "Note", ["data"] = new JsonObject { ["code"] = code } };
    private static JsonObject UpdateArgs(string id, int revision, JsonObject patch) => new() { ["type"] = "Note", ["id"] = id, ["expectedRevision"] = revision, ["patch"] = patch };
    private static JsonObject DeleteArgs(string id) => new() { ["type"] = "Note", ["id"] = id, ["expectedRevision"] = 1 };
    private static InvocationContext Context() => new() { Caller = new Caller("test"), Budget = new Budget { Deadline = DateTimeOffset.UtcNow.AddMinutes(1) } };
    private static RecordService Service(IAuthorization? auth = null) => new(auth ?? new Auth(), new SqliteQueryEngine(new byte[32]), new SystemClock(), new UuidGenerator());
    private static async Task<object?> ScalarAsync(IUnitOfWork uow, string sql)
    {
        using DbCommand command = uow.Connection.CreateCommand(); command.Transaction = uow.Transaction; command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    private sealed class Auth(params string[] denied) : IAuthorization
    {
        public Task ReloadAsync(IUnitOfWork uow, InvocationContext context, CancellationToken ct = default) => Task.CompletedTask;
        public bool Can(InvocationContext context, string action, string resource) => !denied.Contains(action + ":" + resource);
        public void Require(InvocationContext context, string action, string resource)
        { if (!Can(context, action, resource)) throw new BosException("FORBIDDEN", "Permission denied.", 403); }
    }
    private sealed class Database(string directory, SqliteUnitOfWorkFactory factory) : IAsyncDisposable
    {
        public SqliteUnitOfWorkFactory Factory { get; } = factory;
        public static async Task<Database> CreateAsync()
        {
            var directory = Directory.CreateTempSubdirectory("bos-storage-").FullName;
            var db = new Database(directory, new SqliteUnitOfWorkFactory(Path.Combine(directory, "bos.db")));
            await db.Factory.InitializeAsync();
            return db;
        }
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); return ValueTask.CompletedTask; }
    }
}
