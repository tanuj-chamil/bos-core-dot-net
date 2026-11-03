using System.Data.Common;
using System.Text.Json.Nodes;
using Bos.Application;
using Bos.Contracts;
using Bos.Core;
using Bos.Sqlite;
using Xunit;

namespace Bos.Conformance;

public sealed class RunTests
{
    private const string Key = "run-test-key-000001";
    private static readonly Caller Owner = new("runner-owner");
    private static JsonObject Args(JsonObject? input = null) => new() { ["name"] = "TestFlow", ["version"] = 1, ["input"] = input ?? new JsonObject() };

    [Fact]
    public void Receipt_identity_is_canonical_and_keeps_preconditions_and_missing_distinct()
    {
        var a = JsonNode.Parse("""{"name":"TestFlow","version":1,"input":{"b":"e\u0301","a":"1"}}""")!.AsObject();
        var b = JsonNode.Parse("""{"input":{"a":"1","b":"é"},"version":1,"name":"TestFlow"}""")!.AsObject();
        Assert.Equal(ReceiptProtocol.Hash("flows.run", a), ReceiptProtocol.Hash("flows.run", b));
        b["version"] = 2;
        Assert.NotEqual(ReceiptProtocol.Hash("flows.run", a), ReceiptProtocol.Hash("flows.run", b));
        Assert.Equal(ReceiptProtocol.Scope("flows.run", a), ReceiptProtocol.Scope("flows.run", b));
        b["version"] = 1;
        b["input"]!["revision"] = 2;
        Assert.NotEqual(ReceiptProtocol.Hash("flows.run", a), ReceiptProtocol.Hash("flows.run", b));
        Assert.NotEqual(ReceiptProtocol.Hash("x", new JsonObject()), ReceiptProtocol.Hash("x", new JsonObject { ["optional"] = null }));
        Assert.NotEqual(ReceiptProtocol.Key("a", "b", Key), ReceiptProtocol.Key("b", "a", Key));
        Assert.NotEqual(ReceiptProtocol.Scope("records.get", new JsonObject { ["type"] = "A", ["id"] = "1" }),
            ReceiptProtocol.Scope("records.get", new JsonObject { ["type"] = "A", ["id"] = "2" }));
    }

    [Fact]
    public void Secret_command_utilities_exclude_passwords_and_never_replay_token_plaintext()
    {
        var args = new JsonObject { ["id"] = "user", ["expectedRevision"] = 1, ["newPassword"] = "secret-one" };
        var hash = ReceiptProtocol.Hash("users.setPassword", args);
        args["newPassword"] = "secret-two";
        Assert.Equal(hash, ReceiptProtocol.Hash("users.setPassword", args));
        args["expectedRevision"] = 2;
        Assert.NotEqual(hash, ReceiptProtocol.Hash("users.setPassword", args));
        var issue = new JsonObject { ["token"] = "NEVER-PERSIST", ["metadata"] = new JsonObject { ["id"] = "token-id" } };
        var stored = ReceiptProtocol.StoredResult("tokens.create", issue)!;
        Assert.DoesNotContain("NEVER-PERSIST", stored.ToJsonString());
        Assert.Null(stored["token"]);
        Assert.Equal("SECRET_ALREADY_DISCLOSED", ReceiptProtocol.ReplayResult("tokens.create", stored)!["replayCode"]!.GetValue<string>());
        stored["metadata"]!["id"] = "changed";
        Assert.Equal("token-id", issue["metadata"]!["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("123456789012345é")]
    [InlineData("123456789012345\n")]
    public void Invalid_keys_fail_before_reservation(string key) => Assert.Throws<BosException>(() => ReceiptProtocol.ValidateKey(key));

    [Fact]
    public async Task Malformed_unauthorized_disabled_and_corrupt_requests_do_not_reserve()
    {
        var h = new Harness();
        var malformed = Args(); malformed["version"] = null;
        await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(malformed, Owner, Key));
        malformed = Args(); malformed["extra"] = true;
        await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(malformed, Owner, Key));
        h.Auth.Grants = [];
        await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(Args(), Owner, Key));
        h.Auth.Grants = Harness.Grants;
        h.Factory.Seed("flowControl", "TestFlow", new JsonObject { ["name"] = "TestFlow", ["revision"] = 2, ["disabled"] = true });
        var disabled = await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(Args(), Owner, Key));
        Assert.Equal("FLOW_DISABLED", disabled.Code);
        h.Factory.Seed("flowControl", "TestFlow", new JsonObject { ["name"] = "TestFlow", ["revision"] = 3, ["disabled"] = false });
        var published = h.Factory.Get("flowVersions", "TestFlow:1")!;
        published["ast"]!["steps"] = new JsonArray(); // do not update its recorded hash
        h.Factory.Seed("flowVersions", "TestFlow:1", published);
        var corrupt = await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(Args(), Owner, Key));
        Assert.Equal("DEPENDENCY_UNAVAILABLE", corrupt.Code);
        Assert.Empty(h.Factory.Collection("runs"));
        Assert.Empty(h.Factory.Collection("receipts"));
        Assert.Equal(0, h.Interpreter.Calls);
    }

    [Fact]
    public async Task Schema_validation_happens_before_reservation_and_hash_is_before_defaults()
    {
        var h = new Harness();
        h.Publish(inputSchema: JsonNode.Parse("""{"fields":{"label":{"kind":"string","required":true,"default":"default-label"}}}""")!.AsObject());
        await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(Args(new JsonObject { ["unknown"] = "x" }), Owner, Key));
        Assert.Empty(h.Factory.Collection("runs"));
        h.Interpreter.Body = (_, input, _, _, _) =>
        {
            Assert.Equal("default-label", input["label"]!.GetValue<string>());
            return Task.FromResult<JsonNode?>(new JsonObject());
        };
        var receipt = (JsonObject)(await h.Runner.RunAsync(Args(), Owner, Key)).Result!;
        Assert.Equal(ReceiptProtocol.Hash("flows.run", Args()), receipt["inputHash"]!.GetValue<string>());
        var conflict = await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(Args(new JsonObject { ["label"] = "default-label" }), Owner, Key));
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Code);
        Assert.Equal(1, h.Interpreter.Calls);
    }

    [Fact]
    public async Task Success_is_atomic_with_business_audit_steps_and_receipt_and_replays_once()
    {
        var h = new Harness();
        h.Interpreter.Body = async (_, _, context, uow, ct) =>
        {
            Assert.Equal("running", h.Factory.Collection("runs").Single()["status"]!.GetValue<string>());
            Assert.True(uow.IsWrite);
            Assert.Single(context.Ceilings);
            Assert.Equal(new[] { 1 }, context.TypeVersions["Note"]);
            await uow.Documents.PutAsync("business", "one", new JsonObject { ["value"] = "committed" }, ct);
            await uow.Documents.AppendAuditAsync(new JsonObject { ["runId"] = context.RunId }, ct);
            context.ReadTypes.Add("Note");
            context.Steps.Add(new JsonObject { ["stepId"] = "output", ["ordinal"] = 1, ["outcome"] = "succeeded", ["input"] = "secret-snapshot" });
            return new JsonObject { ["value"] = "done" };
        };
        var response = await h.Runner.RunAsync(Args(), Owner, Key);
        var receipt = response.Result!.AsObject();
        Assert.Equal(200, response.Status);
        Assert.Equal("succeeded", receipt["status"]!.GetValue<string>());
        Assert.Equal("committed", h.Factory.Get("business", "one")!["value"]!.GetValue<string>());
        Assert.Single(h.Factory.Collection("audit"));
        Assert.Equal(2, h.Factory.Commits);
        Assert.DoesNotContain("secret-snapshot", h.Factory.Collection("runSteps").Single().ToJsonString());
        Assert.False(receipt.ContainsKey("receiptKey"));
        Assert.False(receipt.ContainsKey("input"));
        var replay = await h.Runner.RunAsync(Args(), Owner, Key);
        Assert.True(JsonNode.DeepEquals(receipt, replay.Result));
        Assert.Equal(1, h.Interpreter.Calls);
    }

    [Fact]
    public async Task Failed_execution_rolls_back_business_and_audit_and_persists_only_safe_metadata()
    {
        var h = new Harness();
        h.Interpreter.Body = async (_, _, context, uow, ct) =>
        {
            await uow.Documents.PutAsync("business", "one", new JsonObject { ["value"] = "rollback" }, ct);
            await uow.Documents.AppendAuditAsync(new JsonObject { ["value"] = "rollback" }, ct);
            context.StepId = "finalize";
            context.IterationPath = "lines[1]/finalize";
            throw new BosException("BUSINESS_FAILURE", "SQL /secret/path token=NEVER-DISCLOSE");
        };
        var response = await h.Runner.RunAsync(Args(), Owner, Key);
        Assert.Equal(200, response.Status);
        Assert.Equal("failed", response.Result!["status"]!.GetValue<string>());
        Assert.Equal("BUSINESS_FAILURE", response.Result["error"]!["code"]!.GetValue<string>());
        Assert.Equal("finalize", response.Result["error"]!["stepId"]!.GetValue<string>());
        Assert.Equal(1, response.Result["error"]!["flowVersion"]!.GetValue<int>());
        Assert.DoesNotContain("NEVER-DISCLOSE", response.Result.ToJsonString());
        Assert.Null(h.Factory.Get("business", "one"));
        Assert.Empty(h.Factory.Collection("audit"));
        Assert.Equal("failed", h.Factory.Collection("receipts").Single()["result"]!["status"]!.GetValue<string>());
        await h.Runner.RunAsync(Args(), Owner, Key);
        Assert.Equal(1, h.Interpreter.Calls);
    }

    [Fact]
    public async Task Failed_terminal_persistence_leaves_running_until_recovery_and_never_executes_again()
    {
        var h = new Harness();
        h.Factory.FailBeforeCommit.Add(2); // reservation succeeds; failed receipt cannot commit
        h.Interpreter.Body = (_, _, _, _, _) => throw new InvalidOperationException("private failure");
        var response = await h.Runner.RunAsync(Args(), Owner, Key);
        Assert.Equal(202, response.Status);
        Assert.Equal("running", response.Result!["status"]!.GetValue<string>());
        await using var database = new RecoveryDatabase();
        await database.Factory.InitializeAsync();
        var run = h.Factory.Collection("runs").Single();
        var runId = run["runId"]!.GetValue<string>();
        await using (var seed = await database.Factory.BeginWriteAsync())
        {
            foreach (var (collection, documentKey) in new[]
            {
                ("runs", runId), ("runSteps", runId), ("receipts", run["receiptKey"]!.GetValue<string>()),
                ("flowVersions", "TestFlow:1"), ("flowControl", "TestFlow"), ("typeVersions", "Note:1")
            }) await seed.Documents.PutAsync(collection, documentKey, h.Factory.Get(collection, documentKey)!);
            await seed.CommitAsync();
        }
        var restarted = new FlowRunner(database.Factory, h.Auth, h.Interpreter, new EmptyRegistry(), new SystemClock(), new UuidGenerator()) { Dispatcher = new NoDispatcher() };
        await restarted.RecoverAsync();
        response = await restarted.RunAsync(Args(), Owner, Key);
        Assert.Equal(409, response.Status);
        Assert.Equal("interrupted", response.Result!["status"]!.GetValue<string>());
        Assert.Equal("RUN_INTERRUPTED", response.Result["error"]!["code"]!.GetValue<string>());
        Assert.Equal(1, h.Interpreter.Calls);
        await restarted.RecoverAsync();
        Assert.Equal(1, h.Interpreter.Calls);
    }

    [Fact]
    public async Task Recovery_pages_past_10000_historical_runs_and_multiple_running_batches()
    {
        await using var database = new RecoveryDatabase();
        await database.Factory.InitializeAsync();
        await using (var seed = await database.Factory.BeginWriteAsync())
        {
            for (var i = 0; i < 10001; i++)
                await seed.Documents.PutAsync("runs", $"history-{i:D5}", new JsonObject
                {
                    ["runId"] = $"history-{i:D5}", ["status"] = i % 2 == 0 ? "succeeded" : "failed",
                    ["result"] = "preserved-history"
                });
            for (var i = 0; i < 401; i++) await SeedRunningAsync(seed, $"recovery-{i:D5}");
            await seed.CommitAsync();
        }
        var interpreter = new TestInterpreter();
        var runner = new FlowRunner(database.Factory, new TestAuthorization(), interpreter, new EmptyRegistry(), new SystemClock(), new UuidGenerator());
        await runner.RecoverAsync();
        await runner.RecoverAsync();
        Assert.Equal(0, interpreter.Calls);
        await using var read = await database.Factory.BeginReadAsync();
        using var counts = read.Connection.CreateCommand();
        counts.Transaction = read.Transaction;
        counts.CommandText = """
            SELECT
                SUM(json_extract(json,'$.status')='running'),
                SUM(json_extract(json,'$.status')='interrupted'),
                SUM(json_extract(json,'$.result')='preserved-history')
            FROM Documents WHERE collection='runs'
            """;
        await using (var reader = await counts.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0, reader.GetInt32(0));
            Assert.Equal(401, reader.GetInt32(1));
            Assert.Equal(10001, reader.GetInt32(2));
        }
        foreach (var id in new[] { "recovery-00000", "recovery-00200", "recovery-00400" })
        {
            var run = (await read.Documents.GetAsync("runs", id))!;
            var receipt = (await read.Documents.GetAsync("receipts", run["receiptKey"]!.GetValue<string>()))!;
            Assert.Equal("interrupted", run["status"]!.GetValue<string>());
            Assert.Equal("RUN_INTERRUPTED", run["error"]!["code"]!.GetValue<string>());
            Assert.Equal(409, receipt["status"]!.GetValue<int>());
            run.Remove("receiptKey");
            Assert.True(JsonNode.DeepEquals(run, receipt["result"]));
            Assert.Single((await read.Documents.GetAsync("runSteps", id))!["items"]!.AsArray());
        }
    }

    [Fact]
    public async Task Recovery_rolls_back_the_entire_batch_when_a_receipt_update_fails()
    {
        await using var database = new RecoveryDatabase();
        await database.Factory.InitializeAsync();
        await using (var seed = await database.Factory.BeginWriteAsync())
        {
            for (var i = 0; i < 201; i++) await SeedRunningAsync(seed, $"recovery-{i:D5}");
            using var trigger = seed.Connection.CreateCommand();
            trigger.Transaction = seed.Transaction;
            trigger.CommandText = """
                CREATE TRIGGER fail_recovery BEFORE UPDATE ON Documents
                WHEN NEW.collection='receipts' AND json_extract(NEW.json,'$.result.runId')='recovery-00100'
                BEGIN SELECT RAISE(ABORT,'injected receipt failure'); END
                """;
            await trigger.ExecuteNonQueryAsync();
            await seed.CommitAsync();
        }
        var interpreter = new TestInterpreter();
        var runner = new FlowRunner(database.Factory, new TestAuthorization(), interpreter, new EmptyRegistry(), new SystemClock(), new UuidGenerator());
        await Assert.ThrowsAsync<BosException>(() => runner.RecoverAsync());
        await using (var read = await database.Factory.BeginReadAsync())
        {
            foreach (var id in new[] { "recovery-00000", "recovery-00100", "recovery-00200" })
            {
                var run = (await read.Documents.GetAsync("runs", id))!;
                Assert.Equal("running", run["status"]!.GetValue<string>());
                Assert.Equal("running", (await read.Documents.GetAsync("receipts", run["receiptKey"]!.GetValue<string>()))!["result"]!["status"]!.GetValue<string>());
                Assert.Equal("running", (await read.Documents.GetAsync("runSteps", id))!["items"]![0]!["status"]!.GetValue<string>());
            }
        }
        await using (var write = await database.Factory.BeginWriteAsync())
        {
            using var drop = write.Connection.CreateCommand();
            drop.Transaction = write.Transaction;
            drop.CommandText = "DROP TRIGGER fail_recovery";
            await drop.ExecuteNonQueryAsync();
            await write.CommitAsync();
        }
        await runner.RecoverAsync();
        Assert.Equal(0, interpreter.Calls);
    }

    [Fact]
    public async Task Runner_applies_the_configured_workspace_timezone_to_execution_context()
    {
        var h = new Harness();
        Assert.Equal("Asia/Colombo", h.Runner.WorkspaceTimezone);
        h.Runner.WorkspaceTimezone = "Pacific/Auckland";
        h.Interpreter.Body = (_, _, context, _, _) =>
        {
            Assert.Equal("Pacific/Auckland", context.WorkspaceTimezone);
            return Task.FromResult<JsonNode?>(new JsonObject());
        };
        Assert.Equal("succeeded", (await h.Runner.RunAsync(Args(), Owner, Key)).Result!["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("command", false)]
    [InlineData("command", true)]
    [InlineData("query", false)]
    [InlineData("query", true)]
    public async Task GetRunSteps_returns_explicit_terminal_logs_and_redacts_after_type_grant_loss(string mode, bool fail)
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.PublishTypeAsync(new JsonObject
        {
            ["name"] = "Note", ["fields"] = new JsonObject { ["text"] = new JsonObject { ["kind"] = "string" } }
        });
        var source = """
            format: bos.flow/1.0
            name: DiagnosticFlow
            mode: MODE
            transaction: TRANSACTION
            inputSchema:
              fields:
                privateText: {kind: string, required: true}
            outputSchema: {fields: {}}
            dependencies:
              types: {Note: [1]}
              operations: {records.count: 1}
            capabilities: [record.read:Note]
            steps:
              - id: countNotes
                call: records.count
                args:
                  type: {literal: Note}
                  filter: {object: {}}
              - id: messages
                forEach:
                  array: [{literal: first}, {literal: second}]
                as: item
                maxItems: 2
                steps:
                  - id: diagnostic
                    log: Processed item
                    level: info
                    fields:
                      label: {path: item}
                      amount: {integer: "2"}
                      ok: {literal: true}
                      asOf: {fn: parseDate, args: [{literal: "2026-11-03"}]}
                      absent: {literal: null}
              - id: finish
                require: {literal: CONTINUE}
                code: TEST_STOP
                message: PRIVATE-EXCEPTION-MESSAGE
              - id: output
                return: {object: {}}
            """.Replace("MODE", mode, StringComparison.Ordinal)
            .Replace("TRANSACTION", mode == "command" ? "atomic" : "readOnly", StringComparison.Ordinal)
            .Replace("CONTINUE", fail ? "false" : "true", StringComparison.Ordinal);
        await workspace.PublishFlowAsync("DiagnosticFlow", source);
        var grants = new JsonArray(
            new JsonObject { ["action"] = "flows.execute", ["resource"] = "DiagnosticFlow" },
            new JsonObject { ["action"] = "record.read", ["resource"] = "Note" });
        var (caller, token) = await TestWorkspace.CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "LogReader", grants);
        var receipt = await workspace.CallAsync("flows.run", new JsonObject
        {
            ["name"] = "DiagnosticFlow", ["version"] = 1, ["input"] = new JsonObject { ["privateText"] = "PRIVATE-INPUT-PAYLOAD" }
        }, caller);
        Assert.Equal(fail ? "failed" : "succeeded", receipt["status"]!.GetValue<string>());
        var runId = receipt["runId"]!.GetValue<string>();
        var args = new JsonObject { ["runId"] = runId };
        var page = await workspace.CallAsync("flows.getRunSteps", args, caller);
        var logged = page["items"]!.AsArray().OfType<JsonObject>().Where(step => step["log"] is not null).ToArray();
        Assert.Equal(2, logged.Length);
        for (var i = 0; i < logged.Length; i++)
        {
            Assert.Equal("diagnostic", logged[i]["stepId"]!.GetValue<string>());
            Assert.Equal($"messages[{i}]", logged[i]["iterationPath"]!.GetValue<string>());
            Assert.Equal("succeeded", logged[i]["outcome"]!.GetValue<string>());
            Assert.NotNull(logged[i]["startedAt"]);
            Assert.NotNull(logged[i]["durationMs"]);
            Assert.Equal("Processed item", logged[i]["log"]!["message"]!.GetValue<string>());
            Assert.Equal(i == 0 ? "first" : "second", logged[i]["log"]!["fields"]!["label"]!.GetValue<string>());
            Assert.Equal("2", logged[i]["log"]!["fields"]!["amount"]!.GetValue<string>());
            Assert.True(logged[i]["log"]!["fields"]!["ok"]!.GetValue<bool>());
            Assert.Equal("2026-11-03", logged[i]["log"]!["fields"]!["asOf"]!.GetValue<string>());
            Assert.True(logged[i]["log"]!["fields"]!.AsObject().ContainsKey("absent"));
            Assert.Null(logged[i]["log"]!["fields"]!["absent"]);
        }
        if (fail)
        {
            var failed = page["items"]!.AsArray().OfType<JsonObject>().Single(step => step["stepId"]!.GetValue<string>() == "finish");
            Assert.Equal("failed", failed["outcome"]!.GetValue<string>());
            Assert.Equal("TEST_STOP", failed["errorCode"]!.GetValue<string>());
            Assert.Equal("TEST_STOP", receipt["error"]!["code"]!.GetValue<string>());
        }
        await using (var read = await workspace.Factory.BeginReadAsync())
        {
            var stored = (await read.Documents.GetAsync("runSteps", runId))!.ToJsonString();
            Assert.DoesNotContain("PRIVATE-", stored);
            Assert.DoesNotContain(token, stored);
            Assert.DoesNotContain("PRIVATE-EXCEPTION-MESSAGE", receipt.ToJsonString());
        }
        logged[0]["log"]!["fields"]!["label"] = "client-side-change";
        var unchanged = await workspace.CallAsync("flows.getRunSteps", args, caller);
        Assert.Equal("first", unchanged["items"]!.AsArray().OfType<JsonObject>().First(step => step["log"] is not null)["log"]!["fields"]!["label"]!.GetValue<string>());
        var roles = await workspace.CallAsync("roles.list", new JsonObject
        {
            ["query"] = new JsonObject { ["where"] = new JsonArray(new JsonObject { ["field"] = "name", ["op"] = "eq", ["value"] = "LogReaderRole" }) }
        });
        var role = roles["items"]!.AsArray().Single()!;
        await workspace.CallAsync("roles.update", new JsonObject
        {
            ["id"] = role["id"]!.DeepClone(), ["expectedRevision"] = role["revision"]!.DeepClone(),
            ["changes"] = new JsonObject { ["grants"] = new JsonArray(grants[0]!.DeepClone()) }
        });
        var redacted = await workspace.CallAsync("flows.getRunSteps", args, caller);
        Assert.True(redacted["resultRedacted"]!.GetValue<bool>());
        Assert.Empty(redacted["items"]!.AsArray());
        Assert.DoesNotContain("Processed item", redacted.ToJsonString());
    }

    [Fact]
    public async Task Invalid_or_oversize_logs_cannot_commit_business_success_and_keep_only_bounded_valid_diagnostics()
    {
        var h = new Harness();
        h.Interpreter.Body = async (_, _, context, uow, ct) =>
        {
            await uow.Documents.PutAsync("business", "one", new JsonObject { ["value"] = "must-rollback" }, ct);
            context.Steps.Add(new JsonObject { ["stepId"] = "first", ["iterationPath"] = "", ["status"] = "succeeded" });
            context.Steps.Add(new JsonObject { ["stepId"] = "large", ["iterationPath"] = "", ["status"] = "succeeded" });
            context.Logs.Add(new JsonObject
            {
                ["stepId"] = "first", ["iterationPath"] = "", ["level"] = "warn", ["message"] = "Retained explicit diagnostic",
                ["fields"] = new JsonObject { ["count"] = 2, ["ratio"] = 1.25, ["ok"] = true, ["when"] = "2026-11-03T00:00:00Z", ["padding"] = new string('p', 8000) },
                ["input"] = new JsonObject { ["secret"] = "PRIVATE-DUMP" }, ["credential"] = "PRIVATE-CREDENTIAL"
            });
            context.Logs.Add(new JsonObject
            {
                ["stepId"] = "large", ["iterationPath"] = "", ["level"] = "info", ["message"] = new string('界', 3000), ["fields"] = new JsonObject()
            });
            // Each entry fits individually; only aggregate UTF-8 accounting detects the overflow.
            foreach (var log in context.Logs)
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(log)) < 16 * 1024);
            var provisional = h.Factory.Collection("runSteps").Single();
            Assert.Empty(provisional["items"]!.AsArray());
            Assert.DoesNotContain("Retained explicit diagnostic", provisional.ToJsonString());
            return new JsonObject();
        };
        var response = await h.Runner.RunAsync(Args(), Owner, Key);
        Assert.Equal("failed", response.Result!["status"]!.GetValue<string>());
        Assert.Equal("EXECUTION_LIMIT", response.Result["error"]!["code"]!.GetValue<string>());
        Assert.Null(h.Factory.Get("business", "one"));
        var stored = h.Factory.Collection("runSteps").Single();
        var items = stored["items"]!.AsArray();
        Assert.Equal("Retained explicit diagnostic", items[0]!["log"]!["message"]!.GetValue<string>());
        Assert.Equal(2, items[0]!["log"]!["fields"]!["count"]!.GetValue<int>());
        Assert.False(items[1]!.AsObject().ContainsKey("log"));
        Assert.DoesNotContain("PRIVATE-", stored.ToJsonString());
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(items[0]!["log"]!.ToJsonString()), 1, 16 * 1024);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Log_fields_cannot_persist_whole_objects_or_arrays(bool array)
    {
        var h = new Harness();
        h.Interpreter.Body = (_, _, context, _, _) =>
        {
            context.Steps.Add(new JsonObject { ["stepId"] = "diagnostic", ["iterationPath"] = "", ["status"] = "succeeded" });
            context.Logs.Add(new JsonObject
            {
                ["stepId"] = "diagnostic", ["iterationPath"] = "", ["level"] = "info", ["message"] = "PRIVATE-BAD-LOG",
                ["fields"] = new JsonObject { ["dump"] = array ? new JsonArray("PRIVATE-ARRAY") : new JsonObject { ["secret"] = "PRIVATE-OBJECT" } }
            });
            return Task.FromResult<JsonNode?>(new JsonObject());
        };
        var receipt = (await h.Runner.RunAsync(Args(), Owner, Key)).Result!;
        Assert.Equal("failed", receipt["status"]!.GetValue<string>());
        Assert.Equal("LOG_INVALID", receipt["error"]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("PRIVATE-", h.Factory.Collection("runSteps").Single().ToJsonString());
    }

    [Fact]
    public async Task Visible_receipt_steps_strip_log_payloads_when_redacted()
    {
        var h = new Harness();
        h.Interpreter.Body = (_, _, context, _, _) =>
        {
            context.ReadTypes.Add("Note");
            context.Steps.Add(new JsonObject { ["stepId"] = "diagnostic", ["iterationPath"] = "loop[1]/nested[2]", ["status"] = "succeeded" });
            context.Logs.Add(new JsonObject
            {
                ["stepId"] = "diagnostic", ["iterationPath"] = "loop[1]/nested[2]", ["level"] = "info",
                ["message"] = "Explicit diagnostic payload", ["fields"] = new JsonObject { ["label"] = "private-label" }
            });
            return Task.FromResult<JsonNode?>(new JsonObject());
        };
        var receipt = (await h.Runner.RunAsync(Args(), Owner, Key)).Result!.AsObject();
        receipt["steps"] = h.Factory.Collection("runSteps").Single()["items"]!.DeepClone();
        var allowed = await h.Runner.VisibleReceiptAsync(receipt, new InvocationContext { Caller = Owner });
        Assert.Equal("Explicit diagnostic payload", allowed["steps"]![0]!["log"]!["message"]!.GetValue<string>());
        h.Auth.Grants = [new("flows.execute", "TestFlow")];
        var redacted = await h.Runner.VisibleReceiptAsync(receipt, new InvocationContext { Caller = Owner });
        Assert.True(redacted["resultRedacted"]!.GetValue<bool>());
        Assert.Equal("loop[1]/nested[2]", redacted["steps"]![0]!["iterationPath"]!.GetValue<string>());
        Assert.Equal("succeeded", redacted["steps"]![0]!["outcome"]!.GetValue<string>());
        Assert.False(redacted["steps"]![0]!.AsObject().ContainsKey("log"));
        Assert.DoesNotContain("private-label", redacted.ToJsonString());
    }

    private static async Task SeedRunningAsync(IUnitOfWork uow, string id)
    {
        var receiptKey = CanonicalJson.Hash(JsonValue.Create(id));
        var run = new JsonObject
        {
            ["runId"] = id, ["flowName"] = "TestFlow", ["flowVersion"] = 1, ["hash"] = "flow-hash",
            ["principalId"] = Owner.PrincipalId, ["status"] = "running", ["startedAt"] = "2026-01-01T00:00:00Z",
            ["inputHash"] = "input-hash", ["readTypes"] = new JsonArray(), ["changedRecordIds"] = new JsonArray(), ["resultRedacted"] = false
        };
        await uow.Documents.PutAsync("receipts", receiptKey, new JsonObject
        {
            ["principalId"] = Owner.PrincipalId, ["scope"] = "recovery-test", ["key"] = id,
            ["inputHash"] = "input-hash", ["result"] = run.DeepClone(), ["status"] = 202
        });
        run["receiptKey"] = receiptKey;
        await uow.Documents.PutAsync("runs", id, run);
        await uow.Documents.PutAsync("runSteps", id, new JsonObject
        {
            ["runId"] = id, ["items"] = new JsonArray(new JsonObject { ["stepId"] = "output", ["status"] = "running" })
        });
    }

    private sealed class RecoveryDatabase : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "bos-recovery-tests-" + Guid.NewGuid().ToString("N"));
        public SqliteUnitOfWorkFactory Factory { get; }
        public RecoveryDatabase()
        {
            Directory.CreateDirectory(directory);
            Factory = new SqliteUnitOfWorkFactory(Path.Combine(directory, "workspace.db"));
        }
        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Commit_then_response_loss_is_resolved_from_a_fresh_connection(int commit)
    {
        var h = new Harness();
        h.Factory.ThrowAfterCommit.Add(commit);
        var response = await h.Runner.RunAsync(Args(), Owner, Key);
        Assert.Equal("succeeded", response.Result!["status"]!.GetValue<string>());
        Assert.Equal(1, h.Interpreter.Calls);
        Assert.Equal(2, h.Factory.Commits);
        Assert.True(h.Factory.Reads > 2);
    }

    [Fact]
    public async Task Poisoned_transaction_and_invalid_output_cannot_commit_success()
    {
        var h = new Harness();
        h.Interpreter.Body = async (_, _, context, uow, ct) =>
        {
            await uow.Documents.PutAsync("business", "one", new JsonObject(), ct);
            context.Poisoned = true;
            return new JsonObject();
        };
        var poisoned = await h.Runner.RunAsync(Args(), Owner, Key);
        Assert.Equal("failed", poisoned.Result!["status"]!.GetValue<string>());
        Assert.Null(h.Factory.Get("business", "one"));
        h.Interpreter.Body = (_, _, _, _, _) => Task.FromResult<JsonNode?>(new JsonObject { ["undeclared"] = true });
        var invalid = await h.Runner.RunAsync(Args(), Owner, Key + "new");
        Assert.Equal("OUTPUT_INVALID", invalid.Result!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cancellation_still_persists_a_terminal_failure_using_an_uncancelled_token()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Interpreter.Body = (_, _, _, _, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<JsonNode?>(new JsonObject());
        };
        var response = await h.Runner.RunAsync(Args(), Owner, Key, cts.Token);
        Assert.Equal("failed", response.Result!["status"]!.GetValue<string>());
        Assert.Equal("RUN_CANCELLED", response.Result["error"]!["code"]!.GetValue<string>());
        Assert.Equal(2, h.Factory.Commits);
    }

    [Fact]
    public async Task Receipt_visibility_uses_current_owner_flow_and_all_touched_type_grants()
    {
        var h = new Harness();
        h.Interpreter.Body = (_, _, context, _, _) =>
        {
            context.ReadTypes.Add("Note");
            return Task.FromResult<JsonNode?>(new JsonObject { ["value"] = "private-output" });
        };
        var receipt = (await h.Runner.RunAsync(Args(), Owner, Key)).Result!.AsObject();
        h.Auth.Grants = [new("flows.execute", "TestFlow")];
        var redacted = (await h.Runner.RunAsync(Args(), Owner, Key)).Result!.AsObject();
        Assert.True(redacted["resultRedacted"]!.GetValue<bool>());
        Assert.False(redacted.ContainsKey("result"));
        Assert.Equal("succeeded", redacted["status"]!.GetValue<string>());
        h.Auth.Grants = [];
        var ownerMetadata = await h.Runner.VisibleReceiptAsync(receipt, new InvocationContext { Caller = Owner });
        Assert.True(ownerMetadata["resultRedacted"]!.GetValue<bool>());
        var denied = await Assert.ThrowsAsync<BosException>(() => h.Runner.VisibleReceiptAsync(receipt,
            new InvocationContext { Caller = new Caller("other") }));
        Assert.Equal("NOT_FOUND", denied.Code);
        h.Auth.Grants = [new("runs.readAll", "*")];
        var adminMetadata = await h.Runner.VisibleReceiptAsync(receipt, new InvocationContext { Caller = new Caller("other") });
        Assert.True(adminMetadata["resultRedacted"]!.GetValue<bool>());
        await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(Args(), Owner, Key));
    }

    [Fact]
    public async Task Write_concurrency_rejects_new_work_without_reserving_but_running_duplicate_returns_202()
    {
        var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Interpreter.Body = async (_, _, _, _, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return new JsonObject();
        };
        var first = h.Runner.RunAsync(Args(), Owner, Key);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var duplicate = await h.Runner.RunAsync(Args(), Owner, Key);
            Assert.Equal(202, duplicate.Status);
            var overload = await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(Args(), Owner, Key + "other"));
            Assert.Equal(429, overload.Status);
            Assert.Single(h.Factory.Collection("runs"));
        }
        finally { release.SetResult(); }
        Assert.Equal("succeeded", (await first).Result!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Query_runs_use_snapshots_close_before_receipts_and_admit_only_four_readers()
    {
        var h = new Harness(); h.Publish("query");
        var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Interpreter.Body = async (_, _, context, uow, ct) =>
        {
            Assert.True(context.ReadOnly);
            Assert.False(uow.IsWrite);
            entered.Release();
            await release.Task.WaitAsync(ct);
            return new JsonObject();
        };
        var tasks = Enumerable.Range(0, 4).Select(i => h.Runner.RunAsync(Args(), Owner, Key + i)).ToArray();
        for (var i = 0; i < 4; i++) Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        try
        {
            var overload = await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(Args(), Owner, Key + "fifth"));
            Assert.Equal(429, overload.Status);
            Assert.Equal(4, h.Factory.Collection("runs").Count);
        }
        finally { release.SetResult(); }
        foreach (var result in await Task.WhenAll(tasks)) Assert.Equal("succeeded", result.Result!["status"]!.GetValue<string>());
        Assert.Equal(4, h.Interpreter.Calls);
        Assert.Equal(0, h.Factory.ActiveReads);
        Assert.False(h.Factory.SameAsyncFlowReadDuringWrite);
    }

    [Fact]
    public async Task Execution_rechecks_live_control_after_reservation()
    {
        var h = new Harness();
        h.Factory.AfterCommit = commit =>
        {
            if (commit == 1) h.Factory.Seed("flowControl", "TestFlow", new JsonObject { ["name"] = "TestFlow", ["revision"] = 2, ["disabled"] = true });
        };
        var result = await h.Runner.RunAsync(Args(), Owner, Key);
        Assert.Equal("failed", result.Result!["status"]!.GetValue<string>());
        Assert.Equal("FLOW_DISABLED", result.Result["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, h.Interpreter.Calls);
    }

    [Fact]
    public async Task Execution_rechecks_grants_and_cannot_keep_admission_authority()
    {
        var h = new Harness();
        h.Factory.AfterCommit = commit => { if (commit == 1) h.Auth.Grants = [new("flows.execute", "TestFlow")]; };
        var result = await h.Runner.RunAsync(Args(), Owner, Key);
        Assert.Equal("failed", result.Result!["status"]!.GetValue<string>());
        Assert.Equal("FORBIDDEN", result.Result["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, h.Interpreter.Calls);
    }

    [Fact]
    public async Task Captured_business_clock_does_not_replace_the_runtime_execution_deadline()
    {
        var h = new Harness();
        var clock = new FixedClock(DateTimeOffset.Parse("2020-01-01T00:00:00Z"));
        var runner = new FlowRunner(h.Factory, h.Auth, h.Interpreter, new EmptyRegistry(), clock, new UuidGenerator()) { Dispatcher = new NoDispatcher() };
        h.Interpreter.Body = (_, _, context, _, ct) =>
        {
            Assert.Equal(clock.UtcNow, context.StartedAt);
            Assert.InRange(context.Budget.Deadline - DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            context.Budget.Check(ct);
            return Task.FromResult<JsonNode?>(new JsonObject());
        };
        var response = await runner.RunAsync(Args(), Owner, Key);
        Assert.Equal("succeeded", response.Result!["status"]!.GetValue<string>());
        Assert.Equal(clock.UtcNow, DateTimeOffset.Parse(response.Result["startedAt"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Commit_failure_before_durability_rolls_back_work_before_persisting_failed_receipt()
    {
        var h = new Harness(); h.Factory.FailBeforeCommit.Add(2);
        h.Interpreter.Body = async (_, _, _, uow, ct) =>
        {
            await uow.Documents.PutAsync("business", "one", new JsonObject { ["value"] = "rollback" }, ct);
            return new JsonObject();
        };
        var result = await h.Runner.RunAsync(Args(), Owner, Key);
        Assert.Equal("failed", result.Result!["status"]!.GetValue<string>());
        Assert.Null(h.Factory.Get("business", "one"));
        Assert.Equal(2, h.Factory.Commits); // reservation and terminal failure, never business success
    }

    [Fact]
    public async Task Pinned_dependency_failure_and_type_hash_corruption_reject_before_reservation()
    {
        var h = new Harness();
        var type = h.Factory.Get("typeVersions", "Note:1")!;
        type["definition"]!["description"] = "unhashed change";
        h.Factory.Seed("typeVersions", "Note:1", type);
        var mismatch = await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(Args(), Owner, Key));
        Assert.Equal("DEPENDENCY_UNAVAILABLE", mismatch.Code);
        Assert.Empty(h.Factory.Collection("runs"));
        h.Publish();
        var flow = h.Factory.Get("flowVersions", "TestFlow:1")!;
        flow["ast"]!["dependencies"]!["operations"]!["Unavailable"] = 99;
        flow["dependencies"] = flow["ast"]!["dependencies"]!.DeepClone();
        var hash = CanonicalJson.Hash(flow["ast"]);
        flow["hash"] = hash;
        flow["astHash"] = hash;
        h.Factory.Seed("flowVersions", "TestFlow:1", flow);
        var unavailable = await Assert.ThrowsAsync<BosException>(() => h.Runner.RunAsync(Args(), Owner, Key));
        Assert.Equal("DEPENDENCY_UNAVAILABLE", unavailable.Code);
        Assert.Empty(h.Factory.Collection("runs"));
        Assert.Empty(h.Factory.Collection("receipts"));
    }

    [Fact]
    public async Task Step_storage_is_bounded_and_carries_no_step_payloads()
    {
        var h = new Harness();
        h.Interpreter.Body = (_, _, context, _, _) =>
        {
            for (var i = 0; i < 600; i++) context.Steps.Add(new JsonObject
            {
                ["stepId"] = "step" + i, ["iterationPath"] = "loop[" + i + "]", ["status"] = "succeeded",
                ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["durationMs"] = 1,
                ["input"] = "PRIVATE-INPUT", ["result"] = "PRIVATE-STEP-RESULT"
            });
            return Task.FromResult<JsonNode?>(new JsonObject());
        };
        await h.Runner.RunAsync(Args(), Owner, Key);
        var stored = h.Factory.Collection("runSteps").Single();
        Assert.Equal(500, stored["items"]!.AsArray().Count);
        Assert.Equal(1, stored["items"]![0]!["ordinal"]!.GetValue<int>());
        Assert.Equal("succeeded", stored["items"]![0]!["outcome"]!.GetValue<string>());
        Assert.DoesNotContain("PRIVATE-", stored.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_fresh_connections_observe_atomic_receipts_and_business_rollback(bool fail)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bos-run-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new SqliteUnitOfWorkFactory(Path.Combine(directory, "workspace.db"));
            await factory.InitializeAsync();
            var fixture = new Harness();
            await using (var seed = await factory.BeginWriteAsync())
            {
                await seed.Documents.PutAsync("flowVersions", "TestFlow:1", fixture.Factory.Get("flowVersions", "TestFlow:1")!);
                await seed.Documents.PutAsync("flowControl", "TestFlow", fixture.Factory.Get("flowControl", "TestFlow")!);
                await seed.Documents.PutAsync("typeVersions", "Note:1", fixture.Factory.Get("typeVersions", "Note:1")!);
                await seed.CommitAsync();
            }
            var interpreter = new TestInterpreter();
            var runner = new FlowRunner(factory, fixture.Auth, interpreter, new EmptyRegistry(), new SystemClock(), new UuidGenerator()) { Dispatcher = new NoDispatcher() };
            interpreter.Body = async (_, _, context, uow, ct) =>
            {
                // A separate connection sees the reservation but never the uncommitted business write.
                await uow.Documents.PutAsync("business", "one", new JsonObject { ["value"] = "business-write" }, ct);
                await uow.Documents.AppendAuditAsync(new JsonObject { ["actor"] = Owner.PrincipalId, ["runId"] = context.RunId }, ct);
                await using var outside = await factory.BeginReadAsync(ct);
                Assert.Equal("running", (await outside.Documents.ListAsync("runs", ct: ct)).Single()["status"]!.GetValue<string>());
                Assert.Null(await outside.Documents.GetAsync("business", "one", ct));
                if (fail) throw new BosException("BUSINESS_FAILURE", "private diagnostic");
                return new JsonObject { ["value"] = "done" };
            };
            var response = await runner.RunAsync(Args(), Owner, Key);
            var runId = response.Result!["runId"]!.GetValue<string>();
            Assert.Equal(fail ? "failed" : "succeeded", response.Result["status"]!.GetValue<string>());
            await using (var fresh = await factory.BeginReadAsync())
            {
                Assert.Equal(fail, await fresh.Documents.GetAsync("business", "one") is null);
                Assert.Equal(fail ? "failed" : "succeeded", (await fresh.Documents.GetAsync("runs", runId))!["status"]!.GetValue<string>());
                Assert.True(JsonNode.DeepEquals(response.Result, (await fresh.Documents.ListAsync("receipts")).Single()["result"]));
                using var audit = fresh.Connection.CreateCommand();
                audit.Transaction = fresh.Transaction;
                audit.CommandText = "SELECT COUNT(*) FROM Audit";
                Assert.Equal(fail ? 0L : 1L, Convert.ToInt64(await audit.ExecuteScalarAsync()));
            }
            await runner.RecoverAsync(); // SQLite's bounded repository must also support ordinary startup recovery.
            Assert.Equal(1, interpreter.Calls);
            var replay = await runner.RunAsync(Args(), Owner, Key);
            Assert.Equal(runId, replay.Result!["runId"]!.GetValue<string>());
            Assert.Equal(1, interpreter.Calls);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private sealed class Harness
    {
        public static readonly Grant[] Grants = [new("flows.execute", "TestFlow"), new("record.read", "Note")];
        public MemoryFactory Factory { get; } = new();
        public TestAuthorization Auth { get; } = new() { Grants = Grants };
        public TestInterpreter Interpreter { get; } = new();
        public FlowRunner Runner { get; }
        public Harness()
        {
            Publish();
            Runner = new FlowRunner(Factory, Auth, Interpreter, new EmptyRegistry(), new SystemClock(), new UuidGenerator()) { Dispatcher = new NoDispatcher() };
        }
        public void Publish(string mode = "command", JsonObject? inputSchema = null)
        {
            var empty = new JsonObject { ["fields"] = new JsonObject() };
            var ast = new JsonObject
            {
                ["format"] = "bos.flow/1.0", ["astFormat"] = "bos.ast/1.0", ["name"] = "TestFlow", ["mode"] = mode, ["transaction"] = mode == "command" ? "atomic" : "readOnly",
                ["inputSchema"] = inputSchema?.DeepClone() ?? empty.DeepClone(),
                ["outputSchema"] = JsonNode.Parse("""{"fields":{"value":{"kind":"string"}}}"""),
                ["dependencies"] = new JsonObject { ["types"] = new JsonObject { ["Note"] = new JsonArray(1) }, ["operations"] = new JsonObject() },
                ["capabilities"] = new JsonArray("record.read:Note"),
                ["steps"] = new JsonArray(new JsonObject { ["id"] = "output", ["return"] = new JsonObject { ["object"] = new JsonObject() } })
            };
            Factory.Seed("flowVersions", "TestFlow:1", new JsonObject
            {
                ["name"] = "TestFlow", ["version"] = 1, ["format"] = "bos.flow/1.0", ["hash"] = CanonicalJson.Hash(ast), ["astHash"] = CanonicalJson.Hash(ast),
                ["ast"] = ast, ["inputSchema"] = ast["inputSchema"]!.DeepClone(), ["outputSchema"] = ast["outputSchema"]!.DeepClone(),
                ["dependencies"] = ast["dependencies"]!.DeepClone(), ["capabilities"] = ast["capabilities"]!.DeepClone()
            });
            Factory.Seed("flowControl", "TestFlow", new JsonObject { ["name"] = "TestFlow", ["revision"] = 1, ["disabled"] = false });
            var definition = new JsonObject { ["name"] = "Note", ["fields"] = new JsonObject() };
            Factory.Seed("typeVersions", "Note:1", new JsonObject { ["name"] = "Note", ["version"] = 1, ["definition"] = definition, ["hash"] = CanonicalJson.Hash(definition) });
        }
    }

    private sealed class TestAuthorization : IAuthorization
    {
        public IReadOnlyList<Grant> Grants { get; set; } = [];
        public Task ReloadAsync(IUnitOfWork uow, InvocationContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); context.Grants = Grants.ToArray(); return Task.CompletedTask;
        }
        public bool Can(InvocationContext context, string action, string resource) =>
            context.Grants.Any(g => g.Action == action && (g.Resource == "*" || g.Resource == resource)) &&
            context.Ceilings.All(ceiling => ceiling.Contains(new Grant(action, resource)));
        public void Require(InvocationContext context, string action, string resource)
        {
            if (!Can(context, action, resource)) throw new BosException("FORBIDDEN", "Permission denied.", 403);
        }
    }
    private sealed class TestInterpreter : IFlowInterpreter
    {
        private int calls;
        public int Calls => calls;
        public Func<JsonObject, JsonObject, InvocationContext, IUnitOfWork, CancellationToken, Task<JsonNode?>> Body { get; set; } =
            (_, _, _, _, _) => Task.FromResult<JsonNode?>(new JsonObject());
        public Task<JsonNode?> ExecuteAsync(JsonObject ast, JsonObject input, InvocationContext context, IUnitOfWork uow, IDispatcher dispatcher, CancellationToken ct = default)
        {
            Interlocked.Increment(ref calls); return Body(ast, input, context, uow, ct);
        }
    }
    private sealed class EmptyRegistry : IOperationRegistry
    {
        public IReadOnlyList<OperationDescriptor> Descriptors => [];
        public void Register(IOperation operation) => throw new NotSupportedException();
        public IOperation Resolve(string name, int? version = null) => throw new BosException("DEPENDENCY_UNAVAILABLE", "Operation unavailable.");
    }
    private sealed class FixedClock(DateTimeOffset instant) : IClock { public DateTimeOffset UtcNow => instant; }
    private sealed class NoDispatcher : IDispatcher
    {
        public Task<JsonNode?> CallAsync(string name, int? version, JsonObject input, InvocationContext context, IUnitOfWork uow, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Transactional test double: snapshots clone committed state, only Commit publishes writes.</summary>
    private sealed class MemoryFactory : IUnitOfWorkFactory
    {
        private Dictionary<string, Dictionary<string, JsonObject>> data = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim writer = new(1, 1);
        private readonly object sync = new();
        private readonly AsyncLocal<HashSet<MemoryUow>?> localReads = new();
        private int attempts;
        public int Commits { get; private set; }
        public int Reads { get; private set; }
        public int ActiveReads { get; private set; }
        public bool SameAsyncFlowReadDuringWrite { get; private set; }
        public HashSet<int> FailBeforeCommit { get; } = [];
        public HashSet<int> ThrowAfterCommit { get; } = [];
        public Action<int>? AfterCommit { get; set; }
        public string DatabasePath => ":memory:";
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Seed(string collection, string key, JsonObject value)
        {
            lock (sync)
            {
                if (!data.TryGetValue(collection, out var items)) data[collection] = items = new(StringComparer.Ordinal);
                items[key] = (JsonObject)value.DeepClone();
            }
        }
        public JsonObject? Get(string collection, string key)
        {
            lock (sync) return data.TryGetValue(collection, out var items) && items.TryGetValue(key, out var value) ? (JsonObject)value.DeepClone() : null;
        }
        public IReadOnlyList<JsonObject> Collection(string collection)
        {
            lock (sync) return data.TryGetValue(collection, out var items) ? items.Values.Select(v => (JsonObject)v.DeepClone()).ToArray() : [];
        }
        public Task<IUnitOfWork> BeginReadAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (sync)
            {
                var uow = new MemoryUow(this, false, Clone(data));
                localReads.Value ??= [];
                localReads.Value.Add(uow);
                ActiveReads++; Reads++;
                return Task.FromResult<IUnitOfWork>(uow);
            }
        }
        public async Task<IUnitOfWork> BeginWriteAsync(CancellationToken ct = default)
        {
            await writer.WaitAsync(ct);
            lock (sync)
            {
                SameAsyncFlowReadDuringWrite |= localReads.Value?.Any(r => !r.Disposed) == true;
                return new MemoryUow(this, true, Clone(data));
            }
        }
        private static Dictionary<string, Dictionary<string, JsonObject>> Clone(Dictionary<string, Dictionary<string, JsonObject>> source) =>
            source.ToDictionary(c => c.Key, c => c.Value.ToDictionary(v => v.Key, v => (JsonObject)v.Value.DeepClone(), StringComparer.Ordinal), StringComparer.Ordinal);
        private sealed class MemoryUow(MemoryFactory owner, bool write, Dictionary<string, Dictionary<string, JsonObject>> snapshot) : IUnitOfWork, IDocumentRepository, IRecordRepository
        {
            public bool Disposed { get; private set; }
            public DbConnection Connection => throw new NotSupportedException();
            public DbTransaction Transaction => throw new NotSupportedException();
            public bool IsWrite => write;
            public IDocumentRepository Documents => this;
            public IRecordRepository Records => this;
            public Task CommitAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                lock (owner.sync)
                {
                    var attempt = ++owner.attempts;
                    if (owner.FailBeforeCommit.Contains(attempt)) throw new IOException("Injected commit failure");
                    if (write) owner.data = Clone(snapshot);
                    owner.Commits++;
                    owner.AfterCommit?.Invoke(attempt);
                    if (owner.ThrowAfterCommit.Contains(attempt)) throw new IOException("Injected response loss after commit");
                    return Task.CompletedTask;
                }
            }
            public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
            public ValueTask DisposeAsync()
            {
                if (!Disposed)
                {
                    Disposed = true;
                    if (write) owner.writer.Release();
                    else { lock (owner.sync) owner.ActiveReads--; }
                }
                return ValueTask.CompletedTask;
            }
            public Task<JsonObject?> GetAsync(string collection, string key, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(snapshot.TryGetValue(collection, out var items) && items.TryGetValue(key, out var value) ? (JsonObject)value.DeepClone() : null);
            }
            public Task PutAsync(string collection, string key, JsonObject value, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (!write) throw new InvalidOperationException("Write attempted in a read snapshot.");
                if (!snapshot.TryGetValue(collection, out var items)) snapshot[collection] = items = new(StringComparer.Ordinal);
                items[key] = (JsonObject)value.DeepClone();
                return Task.CompletedTask;
            }
            public Task DeleteAsync(string collection, string key, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<IReadOnlyList<JsonObject>> ListAsync(string collection, int limit = 1000, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<JsonObject>>(snapshot.TryGetValue(collection, out var items) ? items.Values.Select(v => (JsonObject)v.DeepClone()).ToArray() : []);
            public Task AppendAuditAsync(JsonObject entry, CancellationToken ct = default) => PutAsync("audit", Guid.NewGuid().ToString(), entry, ct);
            public Task<JsonObject?> GetAsync(string id, CancellationToken ct = default) => GetAsync("records", id, ct);
            public Task SaveAsync(JsonObject record, JsonObject definition, InvocationContext context, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<bool> HasIncomingReferencesAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        }
    }
}
