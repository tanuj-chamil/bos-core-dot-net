using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Bos.Application;
using Bos.Contracts;
using Bos.Core;
using Bos.Flows;
using Bos.Host;
using Bos.Identity;
using Bos.Sqlite;
using Xunit;
using static Bos.Conformance.TestWorkspace;

namespace Bos.Conformance;

public sealed class MaterialRequestTests
{
    [Fact]
    public void Version_one_descriptors_are_detached_and_declare_exact_capabilities_and_full_envelopes()
    {
        var registry = new OperationRegistry();
        MaterialRequestOperations.Register(registry);
        var expected = new Dictionary<string, string[]>
        {
            ["CreateRequest"] = ["record.read:MaterialRequest", "record.read:MaterialRequestLine", "record.create:MaterialRequest", "record.create:MaterialRequestLine"],
            ["ReviseRequest"] = ["record.read:MaterialRequest", "record.read:MaterialRequestLine", "record.update:MaterialRequest", "record.update:MaterialRequestLine"],
            ["RepriceRequestLine"] = ["record.read:MaterialRequest", "record.read:MaterialRequestLine", "record.update:MaterialRequestLine"],
            ["FinalizeRequest"] = ["record.read:MaterialRequest", "record.read:MaterialRequestLine", "record.update:MaterialRequest"],
            ["ApproveRequest"] = ["record.read:MaterialRequest", "record.update:MaterialRequest"]
        };
        foreach (var (name, caps) in expected)
        {
            var operation = registry.Resolve(name, 1);
            var descriptor = operation.Descriptor;
            Assert.Equal(1, descriptor.ContractVersion);
            Assert.Equal("write", descriptor.Mode);
            Assert.True(descriptor.FlowCallable);
            Assert.Equal(new Grant("operations.execute", name), Assert.Single(descriptor.RequiredGrants));
            Assert.Equal(caps.Order(), descriptor.Capabilities.Select(x => $"{x.Action}:{x.Resource}").Order());
            Assert.True(Schema.ValidateDefinition(descriptor.InputSchema, true).Valid);
            Assert.True(Schema.ValidateDefinition(descriptor.OutputSchema, true).Valid);
            var fields = descriptor.OutputSchema["fields"]!;
            if (name is "CreateRequest" or "ReviseRequest") fields = fields["request"]!["fields"]!;
            foreach (var key in new[] { "id", "type", "typeVersion", "revision", "deleted", "data", "createdAt", "createdBy", "updatedAt", "updatedBy" })
                Assert.NotNull(fields[key]);
            var original = CanonicalJson.Serialize(operation.Descriptor.InputSchema);
            descriptor.InputSchema["fields"]!.AsObject().Clear();
            Assert.Equal(original, CanonicalJson.Serialize(operation.Descriptor.InputSchema));
        }
    }

    [Theory]
    [InlineData("0.005", "1.00", "0.00")]
    [InlineData("0.015", "1.00", "0.02")]
    [InlineData("0.025", "1.00", "0.02")]
    [InlineData("2.675", "1.00", "2.68")]
    [InlineData("999999999.999", "9999999.99", "9999999989990000.00")]
    public async Task Repricing_is_exact_half_even_and_noop_preserves_revision_and_history(string quantity, string unitPrice, string amount)
    {
        await using var workspace = await CreateAsync();
        await PublishReferenceTypes(workspace);
        var created = await workspace.OperationAsync("CreateRequest", Input(quantity, unitPrice));
        var line = created["lines"]![0]!;
        var priced = await workspace.OperationAsync("RepriceRequestLine", RevisionInput(line));
        Assert.Equal(amount, Text(priced["data"]!, "amount"));
        Assert.Equal(amount == "0.00" ? 1 : 2, Number(priced, "revision"));
        var before = await workspace.BusinessSnapshotAsync();
        var again = await workspace.OperationAsync("RepriceRequestLine", RevisionInput(priced));
        Assert.True(JsonNode.DeepEquals(priced, again));
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
    }

    [Fact]
    public async Task Revision_validates_every_edit_before_writing_and_changes_parent_once_for_line_only_edits()
    {
        await using var workspace = await CreateAsync();
        await PublishReferenceTypes(workspace);
        var created = await workspace.OperationAsync("CreateRequest", ExampleJson("CreateRequest.input.json"));
        var parent = created["request"]!;
        var lines = created["lines"]!.AsArray();
        var first = await workspace.OperationAsync("RepriceRequestLine", RevisionInput(lines[0]!));
        var second = await workspace.OperationAsync("RepriceRequestLine", RevisionInput(lines[1]!));
        var before = await workspace.BusinessSnapshotAsync();
        var duplicate = RevisionInput(parent);
        var edit = RevisionInput(first); edit["quantity"] = "4.000";
        duplicate["lineChanges"] = new JsonArray(edit.DeepClone(), edit.DeepClone());
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("ReviseRequest", duplicate))).Code);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
        var noop = RevisionInput(parent);
        var same = RevisionInput(first); same["quantity"] = "2.000";
        noop["lineChanges"] = new JsonArray(same);
        Assert.Equal("NO_CHANGE", (await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("ReviseRequest", noop))).Code);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
        var valid = RevisionInput(parent); valid["lineChanges"] = new JsonArray(edit.DeepClone());
        var revised = await workspace.OperationAsync("ReviseRequest", valid);
        Assert.Equal(2, Number(revised["request"]!, "revision"));
        Assert.Equal("2", Text(revised["request"]!["data"]!, "contentVersion"));
        Assert.Equal("0.00", Text(revised["request"]!["data"]!, "total"));
        Assert.Equal("0.00", Text((await workspace.RecordAsync("MaterialRequestLine", Text(first, "id")))["data"]!, "amount"));
        Assert.True(JsonNode.DeepEquals(second, await workspace.RecordAsync("MaterialRequestLine", Text(second, "id"))));
        Assert.Equal("REVISION_CONFLICT", (await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("FinalizeRequest", RevisionInput(parent)))).Code);
    }

    [Fact]
    public async Task Revision_rejects_foreign_lines_and_forbidden_fields_without_mutation()
    {
        await using var workspace = await CreateAsync();
        await PublishReferenceTypes(workspace);
        var first = await workspace.OperationAsync("CreateRequest", Input("1.000", "1.00"));
        var other = await workspace.OperationAsync("CreateRequest", Input("1.000", "2.00"));
        var before = await workspace.BusinessSnapshotAsync();
        var input = RevisionInput(first["request"]!);
        var foreign = RevisionInput(other["lines"]![0]!); foreign["item"] = "Foreign";
        input["lineChanges"] = new JsonArray(foreign);
        Assert.Equal("NOT_FOUND", (await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("ReviseRequest", input))).Code);
        foreach (var key in new[] { "currency", "status", "total", "contentVersion" })
        {
            input = RevisionInput(first["request"]!); input[key] = "forbidden";
            await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("ReviseRequest", input));
        }
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
    }

    [Theory]
    [InlineData(true, "TOO_MANY_LINES")]
    [InlineData(false, "EMPTY_REQUEST")]
    public async Task Finalization_checks_cursor_and_nonempty_before_any_write(bool hasNext, string code)
    {
        await using var workspace = await CreateAsync();
        await using var uow = await workspace.Factory.BeginWriteAsync();
        var dispatcher = new BoundedPageDispatcher(hasNext);
        var operation = workspace.Registry.Resolve("FinalizeRequest", 1);
        var error = await Assert.ThrowsAsync<BosException>(() => operation.ExecuteAsync(new InvocationContext { Caller = workspace.Admin }, uow,
            new JsonObject { ["id"] = BoundedPageDispatcher.Id, ["expectedRevision"] = 1 }, dispatcher, default));
        Assert.Equal(code, error.Code);
        Assert.False(dispatcher.Wrote);
    }

    [Fact]
    public async Task Cli_backup_restore_preserves_records_receipts_and_keys_but_invalidates_credentials_and_recovers_admin()
    {
        using var files = new OperatorFiles();
        var directory = Path.Combine(files.Root, "source");
        var bootstrap = Path.Combine(files.Root, "bootstrap.secret");
        Assert.Equal(0, await OperatorCli.RunAsync(["init", "--data-dir", directory, "--bootstrap-file", bootstrap]));
        Assert.Equal(0, await OperatorCli.RunAsync(["doctor", "--data-dir", directory]));
        Private(bootstrap);
        var factory = new SqliteUnitOfWorkFactory(Path.Combine(directory, "bos.db"));
        var identity = new IdentityService(factory, new Bos.Contracts.SystemClock(), new UuidGenerator());
        var bootstrapPassword = (await File.ReadAllTextAsync(bootstrap)).Trim();
        var bootstrapLogin = await identity.LoginAsync("Admin", bootstrapPassword);
        var sourcePassword = SqliteBackupAdapter.NewAdminSecret();
        await identity.ChangePasswordAsync(bootstrapLogin.Caller, bootstrapPassword, sourcePassword);
        var login = await identity.LoginAsync("Admin", sourcePassword);
        var registry = new OperationRegistry(); MaterialRequestOperations.Register(registry);
        var auth = new Authorization();
        var clock = new Bos.Contracts.SystemClock(); var ids = new UuidGenerator();
        var runner = new FlowRunner(factory, auth, new FlowInterpreter(registry), registry, clock, ids);
        var api = new ApplicationApi(factory, auth, identity, new SqliteQueryEngine(File.ReadAllBytes(Path.Combine(directory, "cursor.key"))), registry, new FlowCompiler(registry), runner, clock, ids);
        runner.Dispatcher = api;
        await api.InvokeAsync("types.create", Parse("""{"definition":{"name":"BackupNote","fields":{"text":{"kind":"string","required":true}}}}"""), login.Caller, NewKey());
        await api.InvokeAsync("types.publish", Parse("""{"name":"BackupNote","expectedRevision":1}"""), login.Caller, NewKey());
        var storedRecord = (await api.InvokeAsync("records.create", Parse("""{"type":"BackupNote","data":{"text":"snapshot business data"}}"""), login.Caller, NewKey())).Result!;
        var attachmentBytes = "snapshot attachment data"u8.ToArray();
        var pendingAttachment = await ((IAttachmentApi)api).UploadAttachmentAsync("snapshot.txt", "text/plain", attachmentBytes, login.Caller, NewKey());
        var storedAttachment = (await api.InvokeAsync("attachments.link", new JsonObject
        {
            ["type"] = "BackupNote", ["recordId"] = storedRecord["id"]!.DeepClone(),
            ["id"] = pendingAttachment["id"]!.DeepClone(), ["expectedRevision"] = 1
        }, login.Caller, NewKey())).Result!;
        var service = await CreateServiceAsync(api, identity, login.Caller, "BackupClient", AllGrants());
        await using (var write = await factory.BeginWriteAsync())
        {
            await write.Documents.PutAsync("tokens", "restore-token", new JsonObject { ["id"] = "restore-token", ["principalId"] = login.Caller.PrincipalId, ["revokedAt"] = null });
            await write.Documents.PutAsync("receipts", "committed", new JsonObject { ["result"] = new JsonObject { ["preserved"] = true }, ["status"] = 200 });
            await write.Documents.PutAsync("runs", "abandoned", new JsonObject { ["runId"] = "abandoned", ["status"] = "running", ["receiptKey"] = "running-key" });
            await write.Documents.PutAsync("receipts", "running-key", new JsonObject { ["result"] = new JsonObject { ["runId"] = "abandoned", ["status"] = "running" }, ["status"] = 202 });
            await write.CommitAsync();
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "keys", "protected-test.xml"), "protected-key-fixture");
        var oldCursor = await File.ReadAllBytesAsync(Path.Combine(directory, "cursor.key"));
        var archive = Path.Combine(files.Root, "snapshot.bosbak");
        Assert.Equal(0, await OperatorCli.RunAsync(["backup", "--data-dir", directory, "--output", archive]));
        Private(archive);
        var restored = Path.Combine(files.Root, "restored");
        Assert.Equal(0, await OperatorCli.RunAsync(["restore", "--input", archive, "--data-dir", restored]));
        var secret = Path.Combine(restored, SqliteBackupAdapter.RecoverySecretName);
        Private(secret);
        Assert.NotEqual(Convert.ToHexString(oldCursor), Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(restored, "cursor.key"))));
        Assert.Equal("protected-key-fixture", await File.ReadAllTextAsync(Path.Combine(restored, "keys", "protected-test.xml")));
        var restoredFactory = new SqliteUnitOfWorkFactory(Path.Combine(restored, "bos.db"));
        var restoredIdentity = new IdentityService(restoredFactory, new Bos.Contracts.SystemClock(), new UuidGenerator());
        Assert.Null(await restoredIdentity.AuthenticateTokenAsync(service.Token));
        var recovered = await restoredIdentity.LoginAsync("Admin", (await File.ReadAllTextAsync(secret)).Trim());
        Assert.NotEqual(login.Caller.SecurityStamp, recovered.Caller.SecurityStamp);
        await Assert.ThrowsAsync<BosException>(() => restoredIdentity.LoginAsync("Admin", sourcePassword));
        await using var read = await restoredFactory.BeginReadAsync();
        Assert.True(JsonNode.DeepEquals(storedRecord, await read.Records.GetAsync(Text(storedRecord, "id"))));
        Assert.True(JsonNode.DeepEquals(storedAttachment, PublicAttachment(await read.Attachments.GetAsync(Text(storedAttachment, "id")))));
        Assert.Equal(attachmentBytes, await read.Attachments.GetContentAsync(Text(storedAttachment, "id")));
        Assert.NotNull((await read.Documents.GetAsync("tokens", "restore-token"))!["revokedAt"]);
        Assert.True((await read.Documents.GetAsync("receipts", "committed"))!["result"]!["preserved"]!.GetValue<bool>());
        Assert.Equal("interrupted", Text((await read.Documents.GetAsync("runs", "abandoned"))!, "status"));
        Assert.Equal("interrupted", Text((await read.Documents.GetAsync("receipts", "running-key"))!["result"]!, "status"));

        static JsonObject? PublicAttachment(JsonObject? metadata)
        {
            metadata = metadata is null ? null : (JsonObject)metadata.DeepClone();
            metadata?.Remove("ownerId"); metadata?.Remove("inputHash");
            if (metadata is not null) metadata["downloadUrl"] = "/v1/attachments/" + metadata["id"]!.GetValue<string>() + "/content";
            return metadata;
        }
    }

    [Fact]
    public async Task Backup_refuses_host_lock_and_restore_refuses_nonempty_target_and_corrupt_hash()
    {
        using var files = new OperatorFiles();
        var source = Path.Combine(files.Root, "workspace");
        Assert.Equal(0, await OperatorCli.RunAsync(["init", "--data-dir", source, "--bootstrap-file", Path.Combine(files.Root, "bootstrap.secret")]));
        var archive = Path.Combine(files.Root, "backup.bosbak");
        using (var lease = new WorkspaceLease(source))
            Assert.Equal("STORAGE_BUSY", (await Assert.ThrowsAsync<BosException>(() => new SqliteBackupAdapter(source).SnapshotAsync(archive))).Code);
        await new SqliteBackupAdapter(source).SnapshotAsync(archive);
        var occupied = Path.Combine(files.Root, "occupied"); Directory.CreateDirectory(occupied);
        await File.WriteAllTextAsync(Path.Combine(occupied, "keep"), "preserve");
        await Assert.ThrowsAsync<BosException>(() => new SqliteBackupAdapter(source).RestoreAsync(archive, occupied));
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(occupied, "keep")));
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            zip.GetEntry("bos.json")!.Delete();
            await using var output = zip.CreateEntry("bos.json").Open();
            await output.WriteAsync(Encoding.UTF8.GetBytes("{}"));
        }
        var target = Path.Combine(files.Root, "invalid");
        Assert.Equal("INVALID_BACKUP", (await Assert.ThrowsAsync<BosException>(() => new SqliteBackupAdapter(source).RestoreAsync(archive, target))).Code);
        Assert.False(File.Exists(Path.Combine(target, "bos.db")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("keys/../../escape")]
    [InlineData("C:/escape")]
    [InlineData("keys\\escape")]
    [InlineData("CON")]
    public async Task Restore_rejects_traversal_and_portability_unsafe_entries(string maliciousPath)
    {
        using var files = new OperatorFiles();
        var archive = Path.Combine(files.Root, "malicious.bosbak");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntry("manifest.json"); zip.CreateEntry("bos.db"); zip.CreateEntry(maliciousPath);
        }
        var target = Path.Combine(files.Root, "target");
        Assert.Equal("INVALID_BACKUP", (await Assert.ThrowsAsync<BosException>(() => new SqliteBackupAdapter(files.Root).RestoreAsync(archive, target))).Code);
        Assert.False(File.Exists(Path.Combine(files.Root, "escape")));
        Assert.False(File.Exists(Path.Combine(target, "bos.db")));
    }

    [Theory]
    [InlineData(true, "DEPENDENCY_UNAVAILABLE")]
    [InlineData(false, "INVALID_BACKUP")]
    public async Task Restore_rejects_future_engine_and_a_corrupt_database_even_with_matching_payload_hash(bool futureEngine, string expectedCode)
    {
        using var files = new OperatorFiles();
        var source = Path.Combine(files.Root, "workspace");
        Assert.Equal(0, await OperatorCli.RunAsync(["init", "--data-dir", source, "--bootstrap-file", Path.Combine(files.Root, "bootstrap.secret")]));
        var archive = Path.Combine(files.Root, "backup.bosbak");
        await new SqliteBackupAdapter(source).SnapshotAsync(archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            JsonObject manifest;
            using (var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open()))
                manifest = Parse(await reader.ReadToEndAsync());
            if (futureEngine) manifest["engineVersion"] = 2;
            else
            {
                var corrupt = Encoding.UTF8.GetBytes("not a SQLite database");
                zip.GetEntry("bos.db")!.Delete();
                await using (var output = zip.CreateEntry("bos.db").Open()) await output.WriteAsync(corrupt);
                var item = manifest["entries"]!.AsArray().Single(x => Text(x!, "path") == "bos.db")!.AsObject();
                item["size"] = corrupt.Length;
                item["sha256"] = Convert.ToHexStringLower(SHA256.HashData(corrupt));
            }
            zip.GetEntry("manifest.json")!.Delete();
            await using var metadata = zip.CreateEntry("manifest.json").Open();
            await metadata.WriteAsync(Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        }
        var target = Path.Combine(files.Root, "target");
        Assert.Equal(expectedCode, (await Assert.ThrowsAsync<BosException>(() => new SqliteBackupAdapter(source).RestoreAsync(archive, target))).Code);
        Assert.False(File.Exists(Path.Combine(target, "bos.db")));
    }

    [Fact]
    public async Task Bare_backup_interface_and_cli_both_install_fresh_identity_recovery_passwords()
    {
        using var files = new OperatorFiles();
        var (source, bootstrap) = await InitializeOperatorWorkspaceAsync(files);
        var archive = Path.Combine(files.Root, "snapshot.bosbak");
        IBackupAdapter adapter = new SqliteBackupAdapter(source);
        await adapter.SnapshotAsync(archive);
        var direct = Path.Combine(files.Root, "direct");
        await adapter.RestoreAsync(archive, direct);
        var cli = Path.Combine(files.Root, "cli");
        Assert.Equal(0, await OperatorCli.RunAsync(["restore", "--input", archive, "--data-dir", cli]));
        var oldPassword = (await File.ReadAllTextAsync(bootstrap)).Trim();
        var passwords = new List<string>();
        foreach (var target in new[] { direct, cli })
        {
            var secretFile = Path.Combine(target, SqliteBackupAdapter.RecoverySecretName);
            Private(secretFile);
            Private(Path.Combine(target, "cursor.key"));
            var password = (await File.ReadAllTextAsync(secretFile)).Trim();
            passwords.Add(password);
            Assert.NotEqual(oldPassword, password);
            var factory = new SqliteUnitOfWorkFactory(Path.Combine(target, "bos.db"));
            var identity = new IdentityService(factory, new Bos.Contracts.SystemClock(), new UuidGenerator());
            var login = await identity.LoginAsync("Admin", password);
            Assert.Equal("Admin", Text(login.User, "userName"));
            await Assert.ThrowsAsync<BosException>(() => identity.LoginAsync("Admin", oldPassword));
            await using var read = await factory.BeginReadAsync();
            await using var command = read.Connection.CreateCommand(); command.Transaction = read.Transaction;
            command.CommandText = "SELECT COUNT(*) FROM Audit WHERE json_extract(json,'$.method')='identity.bootstrap'";
            Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        Assert.NotEqual(passwords[0], passwords[1]);
        var originalIdentity = new IdentityService(new SqliteUnitOfWorkFactory(Path.Combine(source, "bos.db")), new Bos.Contracts.SystemClock(), new UuidGenerator());
        Assert.Equal("Admin", Text((await originalIdentity.LoginAsync("Admin", oldPassword)).User, "userName"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_invalidates_identity_application_and_record_cursors_with_current_permissions(bool useCli)
    {
        using var files = new OperatorFiles();
        var (source, bootstrap) = await InitializeOperatorWorkspaceAsync(files);
        var factory = new SqliteUnitOfWorkFactory(Path.Combine(source, "bos.db"));
        var identity = new IdentityService(factory, new Bos.Contracts.SystemClock(), new UuidGenerator());
        var login = await identity.LoginAsync("Admin", (await File.ReadAllTextAsync(bootstrap)).Trim());
        var api = Compose(source, factory, identity);
        foreach (var name in new[] { "CursorNote", "CursorOther" })
        {
            await api.InvokeAsync("types.create", new JsonObject
            {
                ["definition"] = new JsonObject { ["name"] = name, ["fields"] = new JsonObject { ["title"] = new JsonObject { ["kind"] = "string", ["required"] = true } } }
            }, login.Caller, NewKey());
            await api.InvokeAsync("types.publish", new JsonObject { ["name"] = name, ["expectedRevision"] = 1 }, login.Caller, NewKey());
        }
        foreach (var title in new[] { "First", "Second", "Third" })
        {
            await api.InvokeAsync("users.create", new JsonObject
            {
                ["user"] = new JsonObject { ["userName"] = title, ["displayName"] = title, ["kind"] = "service" }
            }, login.Caller, NewKey());
            await api.InvokeAsync("records.create", new JsonObject
            {
                ["type"] = "CursorNote", ["data"] = new JsonObject { ["title"] = title }
            }, login.Caller, NewKey());
        }
        var grants = (await api.InvokeAsync("identity.getCurrent", new JsonObject(), login.Caller, null)).Result!["effectiveGrants"]!.DeepClone();
        var queries = new[]
        {
            (Method: "users.list", Args: new JsonObject { ["query"] = new JsonObject { ["limit"] = 1 } }, Error: "INVALID_REQUEST"),
            (Method: "types.list", Args: new JsonObject { ["query"] = new JsonObject { ["limit"] = 1 } }, Error: "INVALID_CURSOR"),
            (Method: "records.list", Args: new JsonObject { ["type"] = "CursorNote", ["query"] = new JsonObject { ["limit"] = 1 } }, Error: "INVALID_CURSOR")
        };
        var cursors = new List<string>();
        foreach (var query in queries)
        {
            var page = (await api.InvokeAsync(query.Method, query.Args, login.Caller, null)).Result!;
            var cursor = Text(page, "nextCursor");
            cursors.Add(cursor);
            var resume = query.Args.DeepClone().AsObject(); resume["query"]!["cursor"] = cursor;
            Assert.Single((await api.InvokeAsync(query.Method, resume, login.Caller, null)).Result!["items"]!.AsArray());
        }
        JsonObject originalKey;
        await using (var read = await factory.BeginReadAsync()) originalKey = (await read.Documents.GetAsync("runtimeMetadata", "cursorKey"))!;
        var originalFileKey = await File.ReadAllBytesAsync(Path.Combine(source, "cursor.key"));
        var archive = Path.Combine(files.Root, "snapshot.bosbak");
        IBackupAdapter adapter = new SqliteBackupAdapter(source);
        await adapter.SnapshotAsync(archive);
        var target = Path.Combine(files.Root, "restored");
        if (useCli) Assert.Equal(0, await OperatorCli.RunAsync(["restore", "--input", archive, "--data-dir", target]));
        else await adapter.RestoreAsync(archive, target);
        var restoredFactory = new SqliteUnitOfWorkFactory(Path.Combine(target, "bos.db"));
        var restoredIdentity = new IdentityService(restoredFactory, new Bos.Contracts.SystemClock(), new UuidGenerator());
        var recoveryPassword = (await File.ReadAllTextAsync(Path.Combine(target, SqliteBackupAdapter.RecoverySecretName))).Trim();
        var recovered = await restoredIdentity.LoginAsync("Admin", recoveryPassword);
        Assert.Equal(login.Caller.PrincipalId, recovered.Caller.PrincipalId);
        Assert.Equal("UNAUTHENTICATED", (await Assert.ThrowsAsync<BosException>(() => restoredIdentity.LoginAsync("Admin", recoveryPassword))).Code);
        var restoredApi = Compose(target, restoredFactory, restoredIdentity);
        Assert.True(JsonNode.DeepEquals(grants, (await restoredApi.InvokeAsync("identity.getCurrent", new JsonObject(), recovered.Caller, null)).Result!["effectiveGrants"]));
        for (var i = 0; i < queries.Length; i++)
        {
            var query = queries[i];
            // Fresh pages and continuations succeed: cursor rejection must not come from
            // restored session/token invalidation or a loss of the principal's permissions.
            var page = (await restoredApi.InvokeAsync(query.Method, query.Args, recovered.Caller, null)).Result!;
            var resume = query.Args.DeepClone().AsObject(); resume["query"]!["cursor"] = Text(page, "nextCursor");
            Assert.Single((await restoredApi.InvokeAsync(query.Method, resume, recovered.Caller, null)).Result!["items"]!.AsArray());
            resume["query"]!["cursor"] = cursors[i];
            Assert.Equal(query.Error, (await Assert.ThrowsAsync<BosException>(() => restoredApi.InvokeAsync(query.Method, resume, recovered.Caller, null))).Code);
        }
        await using (var read = await restoredFactory.BeginReadAsync())
        {
            var key = (await read.Documents.GetAsync("runtimeMetadata", "cursorKey"))!;
            Assert.Equal("cursorKey", Text(key, "id"));
            Assert.Matches("^[A-Za-z0-9_-]{43}$", Text(key, "value"));
            Assert.Equal(32, Convert.FromBase64String(Text(key, "value").Replace('-', '+').Replace('_', '/').PadRight(44, '=')).Length);
            Assert.NotEqual(Text(originalKey, "value"), Text(key, "value"));
        }
        await using (var read = await factory.BeginReadAsync())
            Assert.True(JsonNode.DeepEquals(originalKey, await read.Documents.GetAsync("runtimeMetadata", "cursorKey")));
        Assert.NotEqual(Convert.ToHexString(originalFileKey), Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(target, "cursor.key"))));

        static ApplicationApi Compose(string directory, IUnitOfWorkFactory factory, IdentityService identity)
        {
            var registry = new OperationRegistry(); MaterialRequestOperations.Register(registry);
            var clock = new Bos.Contracts.SystemClock(); var ids = new UuidGenerator(); var authorization = new Authorization();
            var runner = new FlowRunner(factory, authorization, new FlowInterpreter(registry), registry, clock, ids);
            var api = new ApplicationApi(factory, authorization, identity, new SqliteQueryEngine(File.ReadAllBytes(Path.Combine(directory, "cursor.key"))), registry, new FlowCompiler(registry), runner, clock, ids);
            runner.Dispatcher = api;
            return api;
        }
    }

    [Fact]
    public async Task Failed_identity_recovery_prevents_adapter_and_cli_from_publishing_or_reporting_success()
    {
        using var files = new OperatorFiles();
        var (source, _) = await InitializeOperatorWorkspaceAsync(files);
        var factory = new SqliteUnitOfWorkFactory(Path.Combine(source, "bos.db"));
        await using (var write = await factory.BeginWriteAsync())
        {
            var admin = Assert.Single(await write.Documents.ListAsync("users"));
            admin["revision"] = int.MaxValue; // A valid, exhausted metadata counter cannot be reset through Identity.
            await write.Documents.PutAsync("users", Text(admin, "id"), admin);
            await write.CommitAsync();
        }
        var archive = Path.Combine(files.Root, "snapshot.bosbak");
        IBackupAdapter adapter = new SqliteBackupAdapter(source);
        await adapter.SnapshotAsync(archive);
        var direct = Path.Combine(files.Root, "direct");
        var error = await Assert.ThrowsAsync<BosException>(() => adapter.RestoreAsync(archive, direct));
        Assert.Equal("CAPACITY_EXCEEDED", error.Code);
        var cli = Path.Combine(files.Root, "cli");
        Assert.Equal(1, await OperatorCli.RunAsync(["restore", "--input", archive, "--data-dir", cli]));
        foreach (var target in new[] { direct, cli })
        {
            Assert.False(File.Exists(Path.Combine(target, "bos.db")));
            Assert.False(File.Exists(Path.Combine(target, SqliteBackupAdapter.RecoverySecretName)));
            Assert.False(File.Exists(Path.Combine(target, "bos.json")));
            Assert.Equal("workspace.lock", Path.GetFileName(Assert.Single(Directory.EnumerateFileSystemEntries(target))));
        }
    }

    [Fact]
    public async Task Backup_refuses_a_newer_source_schema_without_modifying_it_or_publishing_an_archive()
    {
        using var files = new OperatorFiles();
        var (source, _) = await InitializeOperatorWorkspaceAsync(files);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var database = Path.Combine(source, "bos.db");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = database, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=3";
            await command.ExecuteNonQueryAsync();
        }
        var before = await File.ReadAllBytesAsync(database);
        var archive = Path.Combine(files.Root, "future.bosbak");
        Assert.Equal("DEPENDENCY_UNAVAILABLE", (await Assert.ThrowsAsync<BosException>(() => new SqliteBackupAdapter(source).SnapshotAsync(archive))).Code);
        var after = await File.ReadAllBytesAsync(database);
        Assert.Equal(before, after);
        Assert.False(File.Exists(archive));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(files.Root), x => Path.GetFileName(x).StartsWith(".bos-backup-", StringComparison.Ordinal) || x.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("entryCount")]
    [InlineData("expandedEntry")]
    [InlineData("manifestExpansion")]
    public async Task Restore_rejects_dishonest_or_oversized_zip_metadata_before_publication(string attack)
    {
        using var files = new OperatorFiles();
        var (source, _) = await InitializeOperatorWorkspaceAsync(files);
        var archive = Path.Combine(files.Root, "snapshot.bosbak");
        await new SqliteBackupAdapter(source).SnapshotAsync(archive);
        if (attack == "manifestExpansion")
        {
            using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
            zip.GetEntry("manifest.json")!.Delete();
            await using var output = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
            await output.WriteAsync(Encoding.UTF8.GetBytes(new string(' ', 2 * 1024 * 1024)));
        }
        var bytes = await File.ReadAllBytesAsync(archive);
        var end = bytes.Length - 22; // The writer above produces no ZIP comment.
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end, 4)));
        if (attack == "entryCount")
        {
            var count = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 10, 2)) - 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 8, 2), count);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 10, 2), count);
        }
        else
        {
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16, 4));
            var wanted = attack == "expandedEntry" ? "bos.db" : "manifest.json";
            var found = false;
            while (offset < end)
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28, 2));
                var name = Encoding.UTF8.GetString(bytes, offset + 46, nameLength);
                if (name == wanted)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24, 4), attack == "expandedEntry" ? 3u * 1024 * 1024 * 1024 + 1 : 1);
                    found = true; break;
                }
                offset += 46 + nameLength + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 30, 2)) + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 32, 2));
            }
            Assert.True(found);
        }
        await File.WriteAllBytesAsync(archive, bytes);
        var target = Path.Combine(files.Root, "target");
        Assert.Equal("INVALID_BACKUP", (await Assert.ThrowsAsync<BosException>(() => new SqliteBackupAdapter(source).RestoreAsync(archive, target))).Code);
        Assert.False(File.Exists(Path.Combine(target, "bos.db")));
        Assert.False(File.Exists(Path.Combine(target, SqliteBackupAdapter.RecoverySecretName)));
    }

    [Fact]
    public async Task Backup_and_restore_reject_linked_ancestors_and_protected_source_keys()
    {
        if (!OperatingSystem.IsLinux()) return; // Creating links on Windows depends on the test account's OS privilege.
        using var files = new OperatorFiles();
        var (source, _) = await InitializeOperatorWorkspaceAsync(files);
        var adapter = new SqliteBackupAdapter(source);
        var archive = Path.Combine(files.Root, "snapshot.bosbak");
        await adapter.SnapshotAsync(archive);
        var alias = Path.Combine(files.Root, "alias"); Directory.CreateSymbolicLink(alias, source);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<BosException>(() => adapter.SnapshotAsync(Path.Combine(alias, "inside.bosbak")))).Code);
        Assert.False(File.Exists(Path.Combine(source, "inside.bosbak")));
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<BosException>(() => adapter.RestoreAsync(archive, Path.Combine(alias, "escaped")))).Code);
        Assert.False(Directory.Exists(Path.Combine(source, "escaped")));
        var outside = Path.Combine(files.Root, "outside-key"); await File.WriteAllTextAsync(outside, "external key");
        File.CreateSymbolicLink(Path.Combine(source, "keys", "linked.xml"), outside);
        var rejected = Path.Combine(files.Root, "linked.bosbak");
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<BosException>(() => adapter.SnapshotAsync(rejected))).Code);
        Assert.False(File.Exists(rejected));
        Assert.Equal("external key", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task Reset_admin_generates_new_protected_password_and_rejects_credential_options()
    {
        using var files = new OperatorFiles();
        var directory = Path.Combine(files.Root, "workspace");
        var first = Path.Combine(files.Root, "bootstrap.secret");
        var next = Path.Combine(files.Root, "reset.secret");
        Assert.Equal(0, await OperatorCli.RunAsync(["init", "--data-dir", directory, "--bootstrap-file", first]));
        Assert.Equal(2, await OperatorCli.RunAsync(["reset-admin", "--data-dir", directory, "--password", "not-accepted"]));
        Assert.Equal(0, await OperatorCli.RunAsync(["reset-admin", "--data-dir", directory, "--secret-file", next]));
        Private(next);
        var identity = new IdentityService(new SqliteUnitOfWorkFactory(Path.Combine(directory, "bos.db")), new Bos.Contracts.SystemClock(), new UuidGenerator());
        Assert.Equal("Admin", Text((await identity.LoginAsync("Admin", (await File.ReadAllTextAsync(next)).Trim())).User, "userName"));
        var oldPassword = (await File.ReadAllTextAsync(first)).Trim();
        await Assert.ThrowsAsync<BosException>(() => identity.LoginAsync("Admin", oldPassword));
        Assert.Equal(1, await OperatorCli.RunAsync(["reset-admin", "--data-dir", directory, "--secret-file", next]));
    }

    private static async Task PublishReferenceTypes(TestWorkspace workspace)
    {
        await workspace.PublishTypeAsync(ExampleJson("MaterialRequest.json"));
        await workspace.PublishTypeAsync(ExampleJson("MaterialRequestLine.json"));
    }
    private static JsonObject Input(string quantity, string unitPrice) => new()
    {
        ["title"] = "Exact arithmetic", ["currency"] = "LKR",
        ["lines"] = new JsonArray(new JsonObject { ["item"] = "Item", ["quantity"] = quantity, ["unitPrice"] = unitPrice })
    };
    private static JsonObject RevisionInput(JsonNode record) => new() { ["id"] = Text(record, "id"), ["expectedRevision"] = Number(record, "revision") };
    private static void Private(string path)
    {
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        else
        {
            var acl = new FileInfo(path).GetAccessControl();
            var owner = acl.GetOwner(typeof(SecurityIdentifier));
            Assert.Equal(WindowsIdentity.GetCurrent().User, owner);
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow) Assert.Equal(owner, rule.IdentityReference);
        }
    }
    private static async Task<(string Directory, string Bootstrap)> InitializeOperatorWorkspaceAsync(OperatorFiles files)
    {
        var directory = Path.Combine(files.Root, "source");
        var bootstrap = Path.Combine(files.Root, "bootstrap.secret");
        Assert.Equal(0, await OperatorCli.RunAsync(["init", "--data-dir", directory, "--bootstrap-file", bootstrap]));
        return (directory, bootstrap);
    }
    private sealed class OperatorFiles : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "bos-operator-" + Guid.NewGuid().ToString("N"));
        internal OperatorFiles() => Directory.CreateDirectory(Root);
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
    private sealed class BoundedPageDispatcher(bool hasNext) : IDispatcher
    {
        internal const string Id = "00000000-0000-4000-8000-000000000001";
        internal bool Wrote { get; private set; }
        public Task<JsonNode?> CallAsync(string name, int? version, JsonObject input, InvocationContext context, IUnitOfWork uow, CancellationToken ct = default)
        {
            if (name == "records.get") return Task.FromResult<JsonNode?>(new JsonObject { ["id"] = Id, ["revision"] = 1, ["data"] = new JsonObject { ["status"] = "Draft" } });
            if (name == "records.list")
            {
                Assert.Equal(100, input["query"]!["limit"]!.GetValue<int>());
                return Task.FromResult<JsonNode?>(new JsonObject { ["items"] = new JsonArray(), ["nextCursor"] = hasNext ? "more" : null });
            }
            Wrote = true;
            throw new InvalidOperationException("Finalization must reject this page before writing.");
        }
    }
}
