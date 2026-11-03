using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bos.Application;
using Bos.Contracts;
using Bos.Core;
using Bos.Flows;
using Bos.Identity;
using Bos.Sqlite;
using Xunit;

namespace Bos.Conformance;

/// <summary>Independent integration composition: real authorization, Identity and SQLite.</summary>
internal sealed class TestWorkspace : IAsyncDisposable
{
    internal const string BootstrapPassword = "Conformance-Admin-2026!";
    internal static readonly string[] AdminActions =
    [
        "types.read", "types.manage", "record.read", "record.create", "record.update", "record.delete",
        "operations.execute", "flows.read", "flows.manage", "flows.execute", "runs.readAll",
        "identity.manage", "audit.read", "system.manage"
    ];

    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "bos-conformance-" + Guid.NewGuid().ToString("N"));
    internal IUnitOfWorkFactory Factory { get; private set; } = null!;
    internal ApplicationApi Api { get; private set; } = null!;
    internal IdentityService Identity { get; private set; } = null!;
    internal Caller Admin { get; private set; } = null!;
    internal OperationRegistry Registry { get; } = new();
    internal List<JsonObject> StagedPrices { get; } = [];
    internal bool FinalizationEntered { get; private set; }

    internal static async Task<TestWorkspace> CreateAsync(
        Action<IOperationRegistry>? register = null, bool failFinalization = false)
    {
        var workspace = new TestWorkspace();
        try
        {
            Directory.CreateDirectory(workspace.DirectoryPath);
            workspace.Factory = new SqliteUnitOfWorkFactory(Path.Combine(workspace.DirectoryPath, "bos.db"));
            await workspace.Factory.InitializeAsync();
            var clock = new SystemClock();
            var ids = new OrderedIds();
            var authorization = new Authorization();
            workspace.Identity = new IdentityService(workspace.Factory, clock, ids);
            var query = new SqliteQueryEngine(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            // Decorate at trusted startup registration, never via a production fault endpoint.
            MaterialRequestOperations.Register(new RegistrationAdapter(workspace.Registry, operation =>
            {
                if (operation.Descriptor.Name == "RepriceRequestLine")
                    return new ObservedOperation(operation, async (context, uow, input, dispatcher, ct) =>
                    {
                        var result = await operation.ExecuteAsync(context, uow, input, dispatcher, ct);
                        workspace.StagedPrices.Add((JsonObject)result!.DeepClone());
                        return result;
                    });
                if (failFinalization && operation.Descriptor.Name == "FinalizeRequest")
                    return new ObservedOperation(operation, (_, _, _, _, _) =>
                    {
                        workspace.FinalizationEntered = true;
                        throw new BosException("TEST_FINALIZATION_FAILURE", "Controlled test-only failure after pricing.");
                    });
                return operation;
            }));
            register?.Invoke(workspace.Registry);
            var runner = new FlowRunner(workspace.Factory, authorization, new FlowInterpreter(workspace.Registry),
                workspace.Registry, clock, ids);
            workspace.Api = new ApplicationApi(workspace.Factory, authorization, workspace.Identity, query,
                workspace.Registry, new FlowCompiler(workspace.Registry), runner, clock, ids);
            runner.Dispatcher = workspace.Api;
            workspace.Admin = await BootstrapAsync(workspace.Factory, workspace.Identity);
            return workspace;
        }
        catch
        {
            await workspace.DisposeAsync();
            throw;
        }
    }

    internal static async Task<Caller> BootstrapAsync(IUnitOfWorkFactory factory, IIdentityService identity)
    {
        await using var uow = await factory.BeginWriteAsync();
        var user = await identity.BootstrapAsync(uow, BootstrapPassword);
        var id = Text(user, "id");
        var stored = await uow.Documents.GetAsync("users", id);
        Assert.NotNull(stored);
        var stamp = Text(stored, "securityStamp");
        await uow.CommitAsync();
        return new Caller(id, SecurityStamp: stamp);
    }

    internal async Task<JsonObject> CallAsync(string method, JsonObject args, Caller? caller = null, string? key = null)
    {
        var result = await Api.InvokeAsync(method, args, caller ?? Admin, key ?? NewKey());
        Assert.Equal(200, result.Status);
        return Assert.IsType<JsonObject>(result.Result);
    }

    internal async Task<JsonObject> OperationAsync(string name, JsonObject input, Caller? caller = null, string? key = null) =>
        await CallAsync("operations.call", new JsonObject
        {
            ["name"] = name, ["contractVersion"] = 1, ["input"] = input
        }, caller, key);

    internal async Task<JsonObject> PublishTypeAsync(JsonObject definition)
    {
        var draft = await CallAsync("types.create", new JsonObject { ["definition"] = definition.DeepClone() });
        return await CallAsync("types.publish", new JsonObject
        {
            ["name"] = Text(definition, "name"), ["expectedRevision"] = Number(draft, "draftRevision")
        });
    }

    internal async Task PublishFlowAsync(string name, string source)
    {
        var draft = await CallAsync("flows.create", new JsonObject
        {
            ["definition"] = new JsonObject { ["source"] = source }
        });
        await CallAsync("flows.publish", new JsonObject
        {
            ["name"] = name, ["expectedRevision"] = Number(draft, "draftRevision")
        });
    }

    internal async Task InstallReferenceAsync()
    {
        await PublishTypeAsync(ExampleJson("MaterialRequest.json"));
        await PublishTypeAsync(ExampleJson("MaterialRequestLine.json"));
        await PublishFlowAsync("SubmitMaterialRequest", ExampleText("SubmitMaterialRequest.yaml"));
    }

    internal async Task<JsonObject> RecordAsync(string type, string id) =>
        await CallAsync("records.get", new JsonObject { ["type"] = type, ["id"] = id });

    internal async Task<JsonObject> CreateRecordAsync(string type, JsonObject data) =>
        await CallAsync("records.create", new JsonObject { ["type"] = type, ["data"] = data });

    internal async Task<JsonObject> SubmitAsync(JsonObject request, string? revision = null, Caller? caller = null) =>
        await CallAsync("flows.run", new JsonObject
        {
            ["name"] = "SubmitMaterialRequest", ["version"] = 1,
            ["input"] = new JsonObject
            {
                ["requestId"] = Text(request, "id"),
                ["revision"] = revision ?? Number(request, "revision").ToString(CultureInfo.InvariantCulture)
            }
        }, caller);

    internal static async Task<(Caller Caller, string Token)> CreateServiceAsync(
        IApplicationApi api, IIdentityService identity, Caller admin, string name, JsonArray grants, JsonArray? scopes = null)
    {
        async Task<JsonObject> Invoke(string method, JsonObject args) =>
            Assert.IsType<JsonObject>((await api.InvokeAsync(method, args, admin, NewKey())).Result);
        var user = await Invoke("users.create", new JsonObject
        {
            ["user"] = new JsonObject { ["userName"] = name, ["displayName"] = name, ["kind"] = "service" }
        });
        var id = Text(user, "id");
        user = await Invoke("users.update", new JsonObject
        {
            ["id"] = id, ["expectedRevision"] = Number(user, "revision"),
            ["changes"] = new JsonObject { ["active"] = true }
        });
        var role = await Invoke("roles.create", new JsonObject
        {
            ["role"] = new JsonObject { ["name"] = name + "Role", ["grants"] = grants.DeepClone() }
        });
        await Invoke("roles.assign", new JsonObject
        {
            ["userId"] = id, ["roleId"] = Text(role, "id"), ["expectedUserRevision"] = Number(user, "revision")
        });
        var issue = await Invoke("tokens.create", new JsonObject
        {
            ["principalId"] = id, ["scopes"] = (scopes ?? grants).DeepClone()
        });
        var token = Text(issue, "token");
        var caller = await identity.AuthenticateTokenAsync(token);
        Assert.NotNull(caller);
        Assert.Equal(id, caller.PrincipalId);
        return (caller, token);
    }

    // Fresh SQLite connection, not an application cache or a repository mock. Snapshot every
    // persisted business row, including history and all derived search/reference/unique state.
    internal async Task<string> BusinessSnapshotAsync()
    {
        await using var uow = await Factory.BeginReadAsync();
        var tables = new List<string>();
        await using (var command = Command(uow, "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                if (new[] { "Records", "RecordHistory", "IndexValues", "UniqueValues", "RecordReferences" }
                    .Contains(name, StringComparer.OrdinalIgnoreCase)) tables.Add(name);
            }
        Assert.Equal(5, tables.Count);
        var result = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var rows = new List<string>();
            await using var command = Command(uow, $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\"");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(JsonSerializer.Serialize(values));
            }
            result[table] = rows.Order(StringComparer.Ordinal).ToArray();
        }
        return JsonSerializer.Serialize(result);
    }

    internal async Task<string> RecordAuditSnapshotAsync(params string[] ids)
    {
        // The public audit catalogue filters before paging and applies authorization itself.
        var entries = new List<string>();
        foreach (var id in ids)
        {
            var page = await CallAsync("audit.list", new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["where"] = new JsonArray(new JsonObject { ["field"] = "objectId", ["op"] = "eq", ["value"] = id }),
                    ["limit"] = 200
                }
            });
            Assert.Null(page["nextCursor"]);
            entries.Add(page["items"]!.ToJsonString());
        }
        return string.Join("\n", entries);
    }

    private static DbCommand Command(IUnitOfWork uow, string sql)
    {
        var command = uow.Connection.CreateCommand();
        command.Transaction = uow.Transaction;
        command.CommandText = sql;
        return command;
    }

    internal static JsonObject ExampleJson(string file) => Parse(ExampleText(file));
    internal static string ExampleText(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "docs", "examples", file);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("Cannot locate normative example " + file);
    }

    internal static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
    internal static string NewKey() => "conformance-" + Guid.NewGuid().ToString("N");
    internal static string Text(JsonNode node, string key) => node[key]!.GetValue<string>();
    internal static int Number(JsonNode node, string key) => node[key]!.GetValue<int>();
    internal static JsonArray AllGrants() => new(AdminActions.Select(action => (JsonNode)new JsonObject
    {
        ["action"] = action, ["resource"] = "*"
    }).ToArray());

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed class OrderedIds : IIdGenerator
    {
        private long next;
        public string NewId() => $"00000000-0000-4000-8000-{Interlocked.Increment(ref next):x12}";
    }

    private sealed class RegistrationAdapter(IOperationRegistry registry, Func<IOperation, IOperation> decorate) : IOperationRegistry
    {
        public IReadOnlyList<OperationDescriptor> Descriptors => registry.Descriptors;
        public void Register(IOperation operation) => registry.Register(decorate(operation));
        public IOperation Resolve(string name, int? version = null) => registry.Resolve(name, version);
    }

    private sealed class ObservedOperation(IOperation operation,
        Func<InvocationContext, IUnitOfWork, JsonObject, IDispatcher, CancellationToken, Task<JsonNode?>> execute) : IOperation
    {
        public OperationDescriptor Descriptor => operation.Descriptor;
        public Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input,
            IDispatcher dispatcher, CancellationToken ct) => execute(context, uow, input, dispatcher, ct);
    }
}
