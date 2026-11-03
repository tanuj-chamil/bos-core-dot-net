using System.Text.Json.Nodes;
using Bos.Application;
using Bos.Contracts;
using Bos.Core;
using Bos.Flows;
using Bos.Identity;
using Bos.Sqlite;

namespace Bos.CrashProbe;

// This executable is a test fixture, has no Host reference and is not publishable.
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (args.Length != 6 || args[0] is not ("reserved" or "staged" or "committed")) return 2;
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ct = lifetime.Token;
            var mode = args[0];
            var storage = new SqliteUnitOfWorkFactory(args[1]);
            await storage.InitializeAsync(ct);
            IUnitOfWorkFactory factory = mode == "reserved" ? new ReservationCheckpoint(storage) : storage;
            var clock = new SystemClock();
            var ids = new UuidGenerator();
            var authorization = new Authorization(clock);
            var identity = new IdentityService(factory, clock, ids);
            var registry = new OperationRegistry();
            registry.Register(new MutateOperation(mode));
            var runner = new FlowRunner(factory, authorization, new FlowInterpreter(registry), registry, clock, ids);
            var api = new ApplicationApi(factory, authorization, identity,
                new SqliteQueryEngine(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                registry, new FlowCompiler(registry), runner, clock, ids);
            runner.Dispatcher = api;

            // The parent already performed real bootstrap/login. This trusted CLI invocation
            // restores that identity from storage; Authorization still reloads its activity,
            // security stamp and actual role grants at every application transaction boundary.
            Caller caller;
            await using (var read = await factory.BeginReadAsync(ct))
            {
                var user = await read.Documents.GetAsync("users", args[2], ct)
                    ?? throw new InvalidOperationException("The seeded identity is missing.");
                caller = new Caller(user["id"]!.GetValue<string>(), SecurityStamp: user["securityStamp"]!.GetValue<string>());
            }
            var input = new JsonObject { ["first"] = args[4], ["second"] = args[5] };
            var result = await api.InvokeAsync("flows.run", new JsonObject
            {
                ["name"] = "CrashFlow", ["version"] = 1, ["input"] = input
            }, caller, args[3], ct);
            if (mode != "committed" || result.Status != 200 || result.Result?["status"]?.GetValue<string>() != "succeeded")
                throw new InvalidOperationException("The probe did not reach its expected checkpoint.");
            // Application return is after business state and the terminal receipt commit.
            await Signal("committed", new JsonObject { ["runId"] = result.Result["runId"]!.DeepClone() });
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 3;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task Signal(string checkpoint, JsonObject detail)
    {
        detail["checkpoint"] = checkpoint;
        await Console.Out.WriteLineAsync("BOS_CRASH_CHECKPOINT " + detail.ToJsonString());
        await Console.Out.FlushAsync();
    }

    private sealed class ReservationCheckpoint(IUnitOfWorkFactory inner) : IUnitOfWorkFactory
    {
        private int writes;
        public string DatabasePath => inner.DatabasePath;
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public Task<IUnitOfWork> BeginReadAsync(CancellationToken ct = default) => inner.BeginReadAsync(ct);
        public async Task<IUnitOfWork> BeginWriteAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref writes) == 2)
            {
                // FlowRunner's first writer has committed and disposed the reservation.
                // Stop before opening its execution transaction (and before the 2s budget).
                JsonObject run;
                await using (var read = await inner.BeginReadAsync(ct))
                {
                    var runs = await read.Documents.ListAsync("runs", ct: ct);
                    if (runs.Count != 1 || runs[0]["status"]?.GetValue<string>() != "running")
                        throw new InvalidOperationException("A durable running reservation is required.");
                    run = runs[0];
                }
                await Signal("reserved", new JsonObject { ["runId"] = run["runId"]!.DeepClone() });
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return await inner.BeginWriteAsync(ct);
        }
    }

    private sealed class MutateOperation(string mode) : IOperation
    {
        public OperationDescriptor Descriptor { get; } = new("CrashMutate", 1, "Test-only crash boundary operation",
            JsonNode.Parse("""{"fields":{"first":{"kind":"string","required":true},"second":{"kind":"string","required":true}}}""")!.AsObject(),
            JsonNode.Parse("""{"fields":{"done":{"kind":"boolean","required":true}}}""")!.AsObject(),
            "write", true, [new Grant("operations.execute", "CrashMutate")],
            [new Grant("record.read", "CrashRecord"), new Grant("record.update", "CrashRecord")]);

        public async Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct)
        {
            foreach (var field in new[] { "first", "second" })
            {
                var id = input[field]!.GetValue<string>();
                var record = (await dispatcher.CallAsync("records.get", 1,
                    new JsonObject { ["type"] = "CrashRecord", ["id"] = id }, context, uow, ct))!;
                var updated = (await dispatcher.CallAsync("records.update", 1, new JsonObject
                {
                    ["type"] = "CrashRecord", ["id"] = id, ["expectedRevision"] = record["revision"]!.DeepClone(),
                    ["patch"] = new JsonObject
                    {
                        ["label"] = "after-" + field, ["counter"] = "1",
                        ["related"] = field == "first" ? input["second"]!.DeepClone() : null
                    }
                }, context, uow, ct))!;
                if (updated["revision"]!.GetValue<int>() != 2) throw new InvalidOperationException("Expected a real staged revision two.");
            }
            if (mode == "staged")
            {
                async Task<long> Count(string sql)
                {
                    await using var command = uow.Connection.CreateCommand();
                    command.Transaction = uow.Transaction;
                    command.CommandText = sql;
                    return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
                }
                var history = await Count("SELECT count(*) FROM RecordHistory WHERE revision=2");
                var indexed = await Count("SELECT count(*) FROM IndexValues WHERE field='counter' AND value='\"1\"'");
                var unique = await Count("SELECT count(*) FROM UniqueValues WHERE value IN ('\"after-first\"','\"after-second\"')");
                if (history != 2 || indexed != 2 || unique != 2) throw new InvalidOperationException("Staged history/index/unique rows are missing.");
                await Signal("staged", new JsonObject
                {
                    ["runId"] = context.RunId, ["history"] = history, ["indexed"] = indexed, ["unique"] = unique
                });
                // Honor the actual production execution cancellation. The parent kills
                // immediately on the flushed marker and verifies the run remained running.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return new JsonObject { ["done"] = true };
        }
    }
}
