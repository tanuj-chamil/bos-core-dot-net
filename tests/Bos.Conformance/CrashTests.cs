using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bos.Application;
using Bos.Contracts;
using Bos.Core;
using Bos.Flows;
using Bos.Identity;
using Bos.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static Bos.Conformance.TestWorkspace;

namespace Bos.Conformance;

public sealed class CrashTests(ITestOutputHelper output)
{
    [Theory, InlineData("reserved"), InlineData("staged"), Trait("Gate", "A14"), Trait("Gate", "A15")]
    public async Task Killing_a_real_child_before_commit_recovers_interrupted_without_business_changes_or_reexecution(string checkpoint)
    {
        await using var workspace = await CrashWorkspace.CreateAsync();
        var before = await Snapshot(workspace.Application.Factory);
        var marker = await KillAtCheckpoint(workspace, checkpoint);
        var runId = Text(marker, "runId");
        if (checkpoint == "staged")
            foreach (var count in new[] { "history", "indexed", "unique" }) Assert.Equal(2, marker[count]!.GetValue<int>());

        // Every subsequent inspection uses a new factory after the actual process exited.
        var restarted = new Application(workspace.DatabasePath);
        await restarted.Factory.InitializeAsync();
        Assert.Equal(before, await Snapshot(restarted.Factory));
        var reservation = await StoredRun(restarted.Factory, runId);
        Assert.Equal("running", Text(reservation, "status")); // rules out a deadline-failure false positive
        await AssertStoredReceipt(restarted.Factory, workspace.Key, runId, "running", 202);
        using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await restarted.Runner.RecoverAsync(recoveryDeadline.Token);
        var interrupted = await restarted.Api.InvokeAsync("flows.getRun", new JsonObject { ["runId"] = runId }, workspace.Caller, null);
        Assert.Equal("interrupted", Text(interrupted.Result!, "status"));
        Assert.Equal("RUN_INTERRUPTED", Text(interrupted.Result!["error"]!, "code"));
        await AssertStoredReceipt(restarted.Factory, workspace.Key, runId, "interrupted", 409);
        var replay = await restarted.Api.InvokeAsync("flows.run", workspace.RunArgs, workspace.Caller, workspace.Key);
        Assert.Equal(409, replay.Status);
        Assert.Equal(runId, Text(replay.Result!, "runId"));
        Assert.Equal("RUN_INTERRUPTED", Text(replay.Result!["error"]!, "code"));
        await restarted.Runner.RecoverAsync(recoveryDeadline.Token); // repeat recovery is inert
        Assert.Equal(0, restarted.Reexecution.Calls);
        Assert.Equal(before, await Snapshot(restarted.Factory));
        await using var read = await restarted.Factory.BeginReadAsync();
        Assert.Single(await read.Documents.ListAsync("runs"));
        Assert.Empty((await read.Documents.GetAsync("runSteps", runId))!["items"]!.AsArray());
    }

    [Fact, Trait("Gate", "A14"), Trait("Gate", "A13")]
    public async Task Killing_a_real_child_after_commit_preserves_both_updates_history_indexes_and_exactly_once_success_receipt()
    {
        await using var workspace = await CrashWorkspace.CreateAsync();
        var marker = await KillAtCheckpoint(workspace, "committed");
        var runId = Text(marker, "runId");
        var restarted = new Application(workspace.DatabasePath);
        await restarted.Factory.InitializeAsync();
        await AssertStoredReceipt(restarted.Factory, workspace.Key, runId, "succeeded", 200);
        foreach (var (id, field) in new[] { (workspace.FirstId, "first"), (workspace.SecondId, "second") })
        {
            var result = await restarted.Api.InvokeAsync("records.get", new JsonObject { ["type"] = "CrashRecord", ["id"] = id }, workspace.Caller, null);
            Assert.Equal(2, Number(result.Result!, "revision"));
            Assert.Equal("after-" + field, Text(result.Result!["data"]!, "label"));
            Assert.Equal("1", Text(result.Result["data"]!, "counter"));
        }
        await AssertCommittedRows(restarted.Factory, workspace, runId);
        var committed = await Snapshot(restarted.Factory);
        using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await restarted.Runner.RecoverAsync(recoveryDeadline.Token);
        var fetched = await restarted.Api.InvokeAsync("flows.getRun", new JsonObject { ["runId"] = runId }, workspace.Caller, null);
        Assert.Equal("succeeded", Text(fetched.Result!, "status"));
        Assert.True(fetched.Result!["result"]!["done"]!.GetValue<bool>());
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var replay = await restarted.Api.InvokeAsync("flows.run", workspace.RunArgs, workspace.Caller, workspace.Key);
            Assert.Equal(200, replay.Status);
            Assert.Equal(CanonicalJson.Serialize(fetched.Result), CanonicalJson.Serialize(replay.Result));
        }
        Assert.Equal(0, restarted.Reexecution.Calls);
        Assert.Equal(committed, await Snapshot(restarted.Factory));
        await AssertCommittedRows(restarted.Factory, workspace, runId);
        await using var read = await restarted.Factory.BeginReadAsync();
        Assert.Single(await read.Documents.ListAsync("runs"));
        var steps = (await read.Documents.GetAsync("runSteps", runId))!["items"]!.AsArray();
        Assert.Equal(new[] { "mutate", "output" }, steps.Select(step => Text(step!, "stepId")));
        Assert.All(steps, step => Assert.Equal("succeeded", Text(step!, "outcome")));
    }

    private async Task<JsonObject> KillAtCheckpoint(CrashWorkspace workspace, string checkpoint)
    {
        var root = SolutionRoot();
        var assembly = typeof(CrashTests).Assembly;
        var configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var framework = new FrameworkName(assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        var probe = Path.Combine(root, "tests", "Bos.CrashProbe", "bin", configuration,
            "net" + framework.Version.ToString(2), "Bos.CrashProbe.dll");
        Assert.True(File.Exists(probe), "The test project reference must build the child probe: " + probe);
        Assert.True(File.Exists(Path.ChangeExtension(probe, ".runtimeconfig.json")));
        Assert.True(File.Exists(Path.ChangeExtension(probe, ".deps.json")));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = root, CreateNoWindow = true
        };
        foreach (var argument in new[] { probe, checkpoint, workspace.DatabasePath, workspace.Caller.PrincipalId, workspace.Key, workspace.FirstId, workspace.SecondId })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); // drain independently; never block a child pipe
        var elapsed = Stopwatch.StartNew();
        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (line is null || !line.StartsWith("BOS_CRASH_CHECKPOINT ", StringComparison.Ordinal))
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail($"Probe exited without {checkpoint}: {line}; {await stderr.WaitAsync(TimeSpan.FromSeconds(5))}");
            }
            // No database calls, marker parsing or assertions between the signal and kill:
            // the staged operation is still subject to the actual two-second execution budget.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var marker = Parse(line!["BOS_CRASH_CHECKPOINT ".Length..]);
            Assert.Equal(checkpoint, Text(marker, "checkpoint"));
            Assert.NotEqual(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(TimeSpan.FromSeconds(5)));
            output.WriteLine($"{DateTimeOffset.UtcNow:O} checkpoint={checkpoint} pid={process.Id} exit={process.ExitCode} elapsedMs={elapsed.ElapsedMilliseconds} runId={Text(marker, "runId")}");
            return marker;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static string SolutionRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Bos.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate the solution root for the crash probe.");
    }

    private static async Task<JsonObject> StoredRun(IUnitOfWorkFactory factory, string runId)
    {
        await using var read = await factory.BeginReadAsync();
        return (await read.Documents.GetAsync("runs", runId))!;
    }

    private static async Task AssertStoredReceipt(IUnitOfWorkFactory factory, string key, string runId, string status, int responseStatus)
    {
        await using var read = await factory.BeginReadAsync();
        var receipt = Assert.Single(await read.Documents.ListAsync("receipts"), row => row["key"]?.GetValue<string>() == key);
        Assert.Equal(runId, Text(receipt["result"]!, "runId"));
        Assert.Equal(status, Text(receipt["result"]!, "status"));
        Assert.Equal(responseStatus, Number(receipt, "status"));
    }

    private static async Task<string> Snapshot(IUnitOfWorkFactory factory)
    {
        await using var read = await factory.BeginReadAsync();
        var snapshot = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var table in new[] { "Records", "RecordHistory", "IndexValues", "UniqueValues", "RecordReferences", "Audit" })
        {
            await using var command = read.Connection.CreateCommand();
            command.Transaction = read.Transaction;
            command.CommandText = table == "Audit" ? "SELECT * FROM Audit WHERE json_extract(json,'$.type')='CrashRecord'" : $"SELECT * FROM {table}";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(JsonSerializer.Serialize(values));
            }
            snapshot[table] = rows.Order(StringComparer.Ordinal).ToArray();
        }
        return JsonSerializer.Serialize(snapshot);
    }

    private static async Task AssertCommittedRows(IUnitOfWorkFactory factory, CrashWorkspace workspace, string runId)
    {
        await using var read = await factory.BeginReadAsync();
        async Task<long> Count(string sql, params (string Key, object Value)[] parameters)
        {
            await using var command = read.Connection.CreateCommand(); command.Transaction = read.Transaction; command.CommandText = sql;
            foreach (var (key, value) in parameters)
            {
                var parameter = command.CreateParameter(); parameter.ParameterName = key; parameter.Value = value; command.Parameters.Add(parameter);
            }
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        Assert.Equal(4, await Count("SELECT count(*) FROM RecordHistory"));
        Assert.Equal(2, await Count("SELECT count(*) FROM RecordHistory WHERE revision=2 AND runId=$run", ("$run", runId)));
        Assert.Equal(2, await Count("SELECT count(*) FROM IndexValues WHERE field='counter' AND value='\"1\"'"));
        Assert.Equal(2, await Count("SELECT count(*) FROM UniqueValues WHERE value IN ('\"after-first\"','\"after-second\"')"));
        Assert.Equal(1, await Count("SELECT count(*) FROM RecordReferences WHERE sourceId=$first AND targetId=$second", ("$first", workspace.FirstId), ("$second", workspace.SecondId)));
        Assert.Equal(2, await Count("SELECT count(*) FROM Audit WHERE json_extract(json,'$.runId')=$run AND json_extract(json,'$.method')='records.update'", ("$run", runId)));
    }

    private sealed class CrashWorkspace : IAsyncDisposable
    {
        private string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "bos-crash-" + Guid.NewGuid().ToString("N"));
        internal string DatabasePath => Path.Combine(DirectoryPath, "bos.db");
        internal Application Application { get; private set; } = null!;
        internal Caller Caller { get; private set; } = null!;
        internal string FirstId { get; private set; } = "";
        internal string SecondId { get; private set; } = "";
        internal string Key { get; } = NewKey();
        internal JsonObject RunArgs => new()
        {
            ["name"] = "CrashFlow", ["version"] = 1, ["input"] = new JsonObject { ["first"] = FirstId, ["second"] = SecondId }
        };
        internal static async Task<CrashWorkspace> CreateAsync()
        {
            var workspace = new CrashWorkspace();
            Directory.CreateDirectory(workspace.DirectoryPath);
            try
            {
                var application = workspace.Application = new Application(workspace.DatabasePath);
                await application.Factory.InitializeAsync();
                await BootstrapAsync(application.Factory, application.Identity);
                workspace.Caller = (await application.Identity.LoginAsync("Admin", BootstrapPassword)).Caller;
                async Task<JsonObject> Call(string method, JsonObject args) =>
                    Assert.IsType<JsonObject>((await application.Api.InvokeAsync(method, args, workspace.Caller, NewKey())).Result);
                var draft = await Call("types.create", new JsonObject { ["definition"] = Parse(TypeDefinition) });
                await Call("types.publish", new JsonObject { ["name"] = "CrashRecord", ["expectedRevision"] = Number(draft, "draftRevision") });
                var first = await Call("records.create", Parse("""{"type":"CrashRecord","data":{"label":"before-first","counter":"0","related":null}}"""));
                workspace.FirstId = Text(first, "id");
                var second = await Call("records.create", new JsonObject
                {
                    ["type"] = "CrashRecord", ["data"] = new JsonObject { ["label"] = "before-second", ["counter"] = "0", ["related"] = workspace.FirstId }
                });
                workspace.SecondId = Text(second, "id");
                draft = await Call("flows.create", new JsonObject { ["definition"] = new JsonObject { ["source"] = FlowSource } });
                await Call("flows.publish", new JsonObject { ["name"] = "CrashFlow", ["expectedRevision"] = Number(draft, "draftRevision") });
                Assert.Equal(0, application.Reexecution.Calls);
                return workspace;
            }
            catch { await workspace.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Application
    {
        internal IUnitOfWorkFactory Factory { get; }
        internal ApplicationApi Api { get; }
        internal IdentityService Identity { get; }
        internal FlowRunner Runner { get; }
        internal ReplayMustNotExecute Reexecution { get; } = new();
        internal Application(string databasePath)
        {
            Factory = new SqliteUnitOfWorkFactory(databasePath);
            var clock = new SystemClock(); var ids = new UuidGenerator(); var authorization = new Authorization(clock);
            Identity = new IdentityService(Factory, clock, ids);
            var registry = new OperationRegistry(); registry.Register(Reexecution);
            Runner = new FlowRunner(Factory, authorization, new FlowInterpreter(registry), registry, clock, ids);
            Api = new ApplicationApi(Factory, authorization, Identity, new SqliteQueryEngine(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                registry, new FlowCompiler(registry), Runner, clock, ids);
            Runner.Dispatcher = Api;
        }
    }

    // Same published contract as the child, deliberately no business implementation:
    // any accidental automatic recovery/replay execution becomes an observable failure.
    private sealed class ReplayMustNotExecute : IOperation
    {
        internal int Calls { get; private set; }
        public OperationDescriptor Descriptor { get; } = new("CrashMutate", 1, "Test-only crash boundary operation",
            Parse("""{"fields":{"first":{"kind":"string","required":true},"second":{"kind":"string","required":true}}}"""),
            Parse("""{"fields":{"done":{"kind":"boolean","required":true}}}"""), "write", true,
            [new Grant("operations.execute", "CrashMutate")], [new Grant("record.read", "CrashRecord"), new Grant("record.update", "CrashRecord")]);
        public Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Recovery or replay tried to execute the business operation again.");
        }
    }

    private const string TypeDefinition = """
        {"name":"CrashRecord","description":"Process crash durability fixture","accessMode":"direct","allowedOperations":[],
         "fields":{"label":{"kind":"string","required":true,"indexed":true,"unique":true},
          "counter":{"kind":"integer","required":true,"indexed":true},
          "related":{"kind":"reference","targetType":"CrashRecord","nullable":true,"indexed":true}}}
        """;

    private const string FlowSource = """
        format: bos.flow/1.0
        name: CrashFlow
        mode: command
        transaction: atomic
        inputSchema:
          fields:
            first: {kind: string, required: true}
            second: {kind: string, required: true}
        outputSchema: {fields: {done: {kind: boolean, required: true}}}
        dependencies:
          types: {CrashRecord: [1]}
          operations: {CrashMutate: 1}
        capabilities:
          - record.read:CrashRecord
          - record.update:CrashRecord
          - operations.execute:CrashMutate
        steps:
          - id: mutate
            call: CrashMutate
            args:
              first: {path: input.first}
              second: {path: input.second}
            as: changed
          - id: output
            return: {path: changed}
        """;
}
