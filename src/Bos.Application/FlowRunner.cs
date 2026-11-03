using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;

namespace Bos.Application;

/// <summary>Owns admission, the durable reservation, and the single execution transaction.</summary>
public sealed class FlowRunner(
    IUnitOfWorkFactory factory, IAuthorization authorization, IFlowInterpreter interpreter,
    IOperationRegistry operations, IClock clock, IIdGenerator ids) : IFlowRunner
{
    private sealed class Admission
    {
        public readonly SemaphoreSlim Writers = new(1, 1);
        public readonly SemaphoreSlim Readers = new(4, 4);
    }
    private sealed record Flow(JsonObject Published, JsonObject Ast, JsonObject Input, JsonObject Output,
        string Mode, IReadOnlyList<Grant> Capabilities, Dictionary<string, int[]> Types);

    private static readonly ConditionalWeakTable<IUnitOfWorkFactory, Admission> Admissions = new();
    public IDispatcher Dispatcher { get; set; } = null!;
    public string WorkspaceTimezone { get; set; } = "Asia/Colombo";

    public async Task<ApiResult> RunAsync(JsonObject args, Caller caller, string key, CancellationToken ct = default)
    {
        args = (JsonObject)args.DeepClone();
        JsonArgs.Only(args, "name", "version", "input");
        var name = JsonArgs.Name(JsonArgs.String(args, "name"));
        var version = JsonArgs.Int(args, "version");
        if (version <= 0) throw new BosException("INVALID_REQUEST", "A positive explicit flow version is required.", 400, "version");
        var input = JsonArgs.Object(args, "input");
        ReceiptProtocol.ValidateKey(key);
        if (Dispatcher is null) throw new InvalidOperationException("Set FlowRunner.Dispatcher before accepting runs.");
        var scope = ReceiptProtocol.Scope("flows.run", args);
        var receiptKey = ReceiptProtocol.Key(caller.PrincipalId, scope, key);
        var inputHash = ReceiptProtocol.Hash("flows.run", args); // before schema defaults
        Flow flow;
        JsonObject? replay;
        await using (var admissionRead = await factory.BeginReadAsync(ct))
        {
            var context = Context(caller);
            await authorization.ReloadAsync(admissionRead, context, ct);
            authorization.Require(context, "flows.execute", name);
            replay = await ExistingAsync(admissionRead, receiptKey, inputHash, ct);
            // Replays do not execute or require the original write capabilities again.
            flow = replay is null ? await ResolveAsync(admissionRead, context, name, version, input, ct) : null!;
        }
        if (replay is not null) return await ReplayAsync(replay, caller, ct);

        var admission = Admissions.GetValue(factory, _ => new Admission());
        var gate = flow.Mode == "command" ? admission.Writers : admission.Readers;
        if (!await gate.WaitAsync(0, ct))
        {
            // A duplicate of the admitted run is a 202 replay, rather than overload.
            await using var read = await factory.BeginReadAsync(ct);
            var context = Context(caller);
            await authorization.ReloadAsync(read, context, ct);
            authorization.Require(context, "flows.execute", name);
            replay = await ExistingAsync(read, receiptKey, inputHash, ct);
            if (replay is null) throw new BosException("CAPACITY_EXCEEDED", "Flow concurrency limit reached.", 429, retryable: true);
        }
        else
        {
            try
            {
                CheckStorage();
                var runId = ids.NewId();
                JsonObject? reservation = null;
                var commitAttempted = false;
                try
                {
                    await using var write = await factory.BeginWriteAsync(ct);
                    var context = Context(caller, runId);
                    await authorization.ReloadAsync(write, context, ct);
                    authorization.Require(context, "flows.execute", name);
                    replay = await ExistingAsync(write, receiptKey, inputHash, ct);
                    if (replay is null)
                    {
                        flow = await ResolveAsync(write, context, name, version, input, ct);
                        CheckStorage();
                        reservation = NewRun(runId, name, version, flow.Published["hash"]!.GetValue<string>(), caller, inputHash);
                        await write.Documents.PutAsync("runs", runId, WithKey(reservation, receiptKey), ct);
                        await write.Documents.PutAsync("runSteps", runId, new JsonObject { ["runId"] = runId, ["items"] = new JsonArray() }, ct);
                        await write.Documents.PutAsync("receipts", receiptKey, new JsonObject
                        {
                            ["principalId"] = caller.PrincipalId, ["scope"] = scope, ["key"] = key,
                            ["inputHash"] = inputHash, ["result"] = reservation.DeepClone(), ["status"] = 202
                        }, ct);
                        commitAttempted = true;
                        await write.CommitAsync(ct);
                    }
                }
                catch when (commitAttempted)
                {
                    // Disposal closes the uncertain transaction before opening a new connection.
                    replay = await ResolveOutcomeAsync(receiptKey);
                    if (replay is null) throw UnknownOutcome();
                    if (replay["runId"]?.GetValue<string>() != runId || replay["status"]?.GetValue<string>() != "running")
                        return await ReplayAsync(replay, caller, CancellationToken.None);
                    reservation = replay;
                }
                if (reservation is null) return await ReplayAsync(replay!, caller, ct);
                return await ExecuteAsync(flow, reservation, receiptKey, caller, input, ct);
            }
            finally { gate.Release(); }
        }
        return await ReplayAsync(replay!, caller, ct);
    }

    private async Task<ApiResult> ExecuteAsync(Flow admitted, JsonObject reservation, string receiptKey,
        Caller caller, JsonObject input, CancellationToken ct)
    {
        var runId = reservation["runId"]!.GetValue<string>();
        var name = reservation["flowName"]!.GetValue<string>();
        var version = reservation["flowVersion"]!.GetValue<int>();
        var context = Context(caller, runId);
        JsonObject? terminal = null;
        Exception? failure = null;
        var commitAttempted = false;
        try
        {
            await using (var uow = admitted.Mode == "command"
                ? await factory.BeginWriteAsync(ct) : await factory.BeginReadAsync(ct))
            {
                context.StartedAt = clock.UtcNow;
                // Budget.Check uses the runtime wall clock; IClock captures the stable business instant.
                context.Budget = new Budget { Deadline = DateTimeOffset.UtcNow.AddSeconds(2) };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var executionCt = deadline.Token;
                await authorization.ReloadAsync(uow, context, executionCt);
                authorization.Require(context, "flows.execute", name);
                var flow = await ResolveAsync(uow, context, name, version, input, executionCt);
                if (flow.Published["hash"]!.GetValue<string>() != reservation["hash"]!.GetValue<string>() || flow.Mode != admitted.Mode)
                    throw new BosException("DEPENDENCY_UNAVAILABLE", "The pinned flow has changed.", 409);
                context.ReadOnly = flow.Mode == "query";
                context.Ceilings.Add(flow.Capabilities);
                foreach (var (type, versions) in flow.Types) context.TypeVersions.Add(type, versions);
                var normalized = Schema.Normalize(flow.Input, input);
                var result = await interpreter.ExecuteAsync((JsonObject)flow.Ast.DeepClone(), normalized, context, uow, Dispatcher, executionCt);
                executionCt.ThrowIfCancellationRequested();
                context.Budget.Check(executionCt);
                if (context.Poisoned) throw new BosException("INTERNAL_ERROR", "Execution transaction was aborted.", 500);
                if (context.ReadOnly && (context.ChangedRecordIds.Count != 0 || context.CreatedRecordIds.Count != 0))
                    throw new BosException("MODE_VIOLATION", "A query flow cannot mutate records.");
                if (result is not JsonObject output) throw new BosException("OUTPUT_INVALID", "Flow output must be an object.");
                try { result = Schema.Normalize(flow.Output, output, false); }
                catch (BosException) { throw new BosException("OUTPUT_INVALID", "Flow output does not match its schema."); }
                if (Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(result)) > 64 * 1024)
                    throw new BosException("EXECUTION_LIMIT", "Flow output exceeds 64 KiB.");
                foreach (var id in context.ChangedRecordIds.Concat(context.CreatedRecordIds).Distinct(StringComparer.Ordinal))
                {
                    var record = await uow.Records.GetAsync(id, executionCt);
                    if (record?["type"] is JsonValue type) context.ReadTypes.Add(type.GetValue<string>());
                }
                terminal = Terminal(reservation, context, "succeeded", result, null);
                if (!context.ReadOnly)
                {
                    await CompleteAsync(uow, terminal, receiptKey, context.Steps, executionCt, context.Logs);
                    context.Budget.Check(executionCt);
                    commitAttempted = true;
                    await uow.CommitAsync(executionCt);
                }
            } // A query snapshot must be closed before persisting its result.
            if (context.ReadOnly)
            {
                await using var write = await factory.BeginWriteAsync(ct);
                await CompleteAsync(write, terminal!, receiptKey, context.Steps, ct, context.Logs);
                commitAttempted = true;
                await write.CommitAsync(ct);
            }
        }
        catch (Exception error) { failure = error; }

        if (failure is not null)
        {
            if (commitAttempted)
            {
                var durable = await ResolveOutcomeAsync(receiptKey);
                if (durable is null) throw UnknownOutcome();
                if (durable["status"]?.GetValue<string>() != "running")
                    return await ReplayAsync(durable, caller, CancellationToken.None);
            }
            terminal = Terminal(reservation, context, "failed", null, SafeError(failure, context));
            // Client cancellation must not prevent the accepted run from obtaining a terminal receipt.
            try
            {
                await using var write = await factory.BeginWriteAsync(CancellationToken.None);
                await CompleteAsync(write, terminal, receiptKey, context.Steps, CancellationToken.None, context.Logs);
                await write.CommitAsync(CancellationToken.None);
            }
            catch
            {
                var durable = await ResolveOutcomeAsync(receiptKey);
                if (durable is null) throw UnknownOutcome();
                return await ReplayAsync(durable, caller, CancellationToken.None);
            }
        }
        return await ReplayAsync(terminal!, caller, CancellationToken.None);
    }

    public async Task<JsonObject> VisibleReceiptAsync(JsonObject receipt, InvocationContext context, CancellationToken ct = default)
    {
        await using var read = await factory.BeginReadAsync(ct);
        var current = Context(context.Caller, context.RunId);
        await authorization.ReloadAsync(read, current, ct);
        if (receipt["principalId"]?.GetValue<string>() != current.PrincipalId && !authorization.Can(current, "runs.readAll", "*"))
            throw new BosException("NOT_FOUND", "Run not found.", 404);
        var visible = new JsonObject();
        foreach (var field in new[] { "runId", "flowName", "flowVersion", "hash", "principalId", "status", "startedAt", "completedAt", "inputHash", "readTypes", "changedRecordIds", "error" })
            if (receipt.TryGetPropertyValue(field, out var value)) visible[field] = value?.DeepClone();
        var allowed = authorization.Can(current, "flows.execute", receipt["flowName"]!.GetValue<string>());
        if (receipt["readTypes"] is not JsonArray types) allowed = false;
        else foreach (var type in types) allowed &= type is not null && authorization.Can(current, "record.read", type.GetValue<string>());
        visible["resultRedacted"] = !allowed;
        if (allowed && receipt.TryGetPropertyValue("result", out var result)) visible["result"] = result?.DeepClone();
        if (receipt["steps"] is JsonArray steps)
            visible["steps"] = StepMetadata(steps.OfType<JsonObject>(),
                includeLogs: allowed && receipt["status"]?.GetValue<string>() is "succeeded" or "failed" or "interrupted");
        return visible;
    }

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        // The host holds the exclusive workspace lock and invokes this before accepting requests.
        const int batchSize = 200;
        var after = "";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var write = await factory.BeginWriteAsync(ct);

            // Bound the scanned key range as well as the returned rows. Filtering running runs
            // over the entire remaining history could otherwise block cancellation inside SQLite.
            var through = after;
            await using (var keys = write.Connection.CreateCommand())
            {
                keys.Transaction = write.Transaction;
                keys.CommandText = """
                    SELECT key FROM Documents
                    WHERE collection=$collection AND key COLLATE BINARY > $after
                    ORDER BY key COLLATE BINARY LIMIT $limit
                    """;
                Parameter(keys, "$collection", "runs");
                Parameter(keys, "$after", after);
                Parameter(keys, "$limit", batchSize);
                await using var reader = await keys.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                while (await reader.ReadAsync(ct))
                {
                    ct.ThrowIfCancellationRequested();
                    through = reader.GetString(0);
                }
            }
            if (through == after) return;

            var runs = new List<JsonObject>(batchSize);
            await using (var running = write.Connection.CreateCommand())
            {
                running.Transaction = write.Transaction;
                running.CommandText = """
                    SELECT json FROM Documents
                    WHERE collection=$collection AND key COLLATE BINARY > $after AND key COLLATE BINARY <= $through
                        AND json_extract(json,'$.status')=$status
                    ORDER BY key COLLATE BINARY LIMIT $limit
                    """;
                Parameter(running, "$collection", "runs");
                Parameter(running, "$after", after);
                Parameter(running, "$through", through);
                Parameter(running, "$status", "running");
                Parameter(running, "$limit", batchSize);
                await using var reader = await running.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                while (await reader.ReadAsync(ct))
                {
                    ct.ThrowIfCancellationRequested();
                    runs.Add(CanonicalJson.Parse(reader.GetString(0))!.AsObject());
                }
            } // Close readers before mutating the same connection/transaction.

            foreach (var run in runs)
            {
                ct.ThrowIfCancellationRequested();
                var runId = run["runId"]!.GetValue<string>();
                var context = Context(new Caller(run["principalId"]!.GetValue<string>()), runId);
                var terminal = Terminal(run, context, "interrupted", null,
                    new JsonObject { ["code"] = "RUN_INTERRUPTED", ["message"] = "Execution was interrupted; it will not be restarted.",
                        ["path"] = "", ["details"] = new JsonArray(), ["traceId"] = runId, ["retryable"] = false });
                var storedSteps = await write.Documents.GetAsync("runSteps", runId, ct);
                await CompleteAsync(write, terminal, run["receiptKey"]!.GetValue<string>(),
                    storedSteps?["items"] is JsonArray steps ? steps.OfType<JsonObject>() : [], ct);
            }
            await write.CommitAsync(ct);
            after = through;
        }
    }

    private static void Parameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task<Flow> ResolveAsync(IUnitOfWork uow, InvocationContext context, string name, int version, JsonObject input, CancellationToken ct)
    {
        var control = await uow.Documents.GetAsync("flowControl", name, ct);
        var published = await uow.Documents.GetAsync("flowVersions", $"{name}:{version.ToString(CultureInfo.InvariantCulture)}", ct);
        if (published is null) throw new BosException("NOT_FOUND", "Published flow not found.", 404);
        if (control is null) throw new BosException("DEPENDENCY_UNAVAILABLE", "Flow control is unavailable.", 409);
        if (control["disabled"] is not JsonValue disabled || !disabled.TryGetValue<bool>(out var isDisabled)) throw Dependency();
        if (isDisabled) throw new BosException("FLOW_DISABLED", "Flow is disabled.", 409);
        if (published["ast"] is not JsonObject ast || ast["astFormat"]?.GetValue<string>() != "bos.ast/1.0" || ast["format"]?.GetValue<string>() != "bos.flow/1.0" ||
            published["format"]?.GetValue<string>() != "bos.flow/1.0" || published["name"]?.GetValue<string>() != name ||
            published["version"]?.GetValue<int>() != version || ast["name"]?.GetValue<string>() != name)
            throw Dependency();
        var astHash = CanonicalJson.Hash(ast);
        if (astHash != published["astHash"]?.GetValue<string>() || astHash != published["hash"]?.GetValue<string>()) throw Dependency();
        var mode = ast["mode"]?.GetValue<string>();
        if (mode is not ("command" or "query") || ast["transaction"]?.GetValue<string>() != (mode == "command" ? "atomic" : "readOnly")) throw Dependency();
        if (published["inputSchema"] is not JsonObject inputSchema || published["outputSchema"] is not JsonObject outputSchema ||
            published["dependencies"] is not JsonObject dependencies || published["capabilities"] is not JsonArray capabilities ||
            !JsonNode.DeepEquals(ast["inputSchema"], inputSchema) || !JsonNode.DeepEquals(ast["outputSchema"], outputSchema) ||
            !JsonNode.DeepEquals(ast["dependencies"], dependencies) || !JsonNode.DeepEquals(ast["capabilities"], capabilities)) throw Dependency();
        var grants = new List<Grant>();
        foreach (var capability in capabilities)
        {
            if (capability is not JsonValue text || !text.TryGetValue<string>(out var value)) throw Dependency();
            var separator = value.IndexOf(':');
            if (separator < 1 || separator == value.Length - 1 || value.Contains('*') || value.IndexOf(':', separator + 1) >= 0) throw Dependency();
            var grant = new Grant(value[..separator], value[(separator + 1)..]);
            if (grant.Action is not ("record.read" or "record.create" or "record.update" or "record.delete" or "operations.execute")) throw Dependency();
            authorization.Require(context, grant.Action, grant.Resource);
            grants.Add(grant);
        }
        var types = new Dictionary<string, int[]>(StringComparer.Ordinal);
        if (dependencies["types"] is not JsonObject pinnedTypes || dependencies["operations"] is not JsonObject pinnedOperations) throw Dependency();
        foreach (var (type, versions) in pinnedTypes)
        {
            if (versions is not JsonArray array || array.Count == 0) throw Dependency();
            var versionsList = new List<int>();
            foreach (var node in array)
            {
                if (node is not JsonValue value || !value.TryGetValue<int>(out var pinned) || pinned <= 0) throw Dependency();
                var definition = await uow.Documents.GetAsync("typeVersions", $"{type}:{pinned.ToString(CultureInfo.InvariantCulture)}", ct);
                if (definition is null || definition["name"]?.GetValue<string>() != type || definition["version"]?.GetValue<int>() != pinned ||
                    definition["definition"] is not JsonObject typeDefinition || definition["hash"]?.GetValue<string>() != CanonicalJson.Hash(typeDefinition))
                    throw Dependency();
                versionsList.Add(pinned);
            }
            types.Add(type, versionsList.Distinct().ToArray());
        }
        foreach (var (operation, node) in pinnedOperations)
        {
            if (node is not JsonValue value || !value.TryGetValue<int>(out var pinned) || pinned <= 0) throw Dependency();
            OperationDescriptor descriptor;
            try { descriptor = operations.Resolve(operation, pinned).Descriptor; }
            catch (BosException) { throw Dependency(); }
            if (descriptor.Name != operation || descriptor.ContractVersion != pinned || !descriptor.FlowCallable ||
                (mode == "query" && descriptor.Mode != "read")) throw Dependency();
            foreach (var grant in descriptor.RequiredGrants.Concat(descriptor.Capabilities))
            {
                if (grant.Resource is "*" or "{type}" or "" && operation.StartsWith("records.", StringComparison.Ordinal)) continue; // dynamic type argument
                if (!grants.Contains(grant)) throw Dependency();
                authorization.Require(context, grant.Action, grant.Resource);
            }
        }
        _ = Schema.Normalize(inputSchema, input); // structural admission; business checks stay in execution
        return new Flow(published, ast, inputSchema, outputSchema, mode, grants, types);
    }

    private InvocationContext Context(Caller caller, string? runId = null) => new()
    {
        Caller = caller, CommandId = runId ?? ids.NewId(), RunId = runId, StartedAt = clock.UtcNow,
        WorkspaceTimezone = WorkspaceTimezone
    };

    private JsonObject NewRun(string runId, string name, int version, string hash, Caller caller, string inputHash) => new()
    {
        ["runId"] = runId, ["flowName"] = name, ["flowVersion"] = version, ["hash"] = hash,
        ["principalId"] = caller.PrincipalId, ["status"] = "running", ["startedAt"] = Instant(clock.UtcNow),
        ["inputHash"] = inputHash, ["readTypes"] = new JsonArray(), ["changedRecordIds"] = new JsonArray(), ["resultRedacted"] = false
    };

    private JsonObject Terminal(JsonObject reservation, InvocationContext context, string status, JsonNode? result, JsonObject? error)
    {
        var terminal = (JsonObject)reservation.DeepClone();
        terminal.Remove("receiptKey");
        terminal["status"] = status;
        if (status != "interrupted") terminal["startedAt"] = Instant(context.StartedAt);
        terminal["completedAt"] = Instant(clock.UtcNow);
        terminal["readTypes"] = Strings(context.ReadTypes);
        terminal["changedRecordIds"] = status == "succeeded" ? Strings(context.ChangedRecordIds.Concat(context.CreatedRecordIds)) : new JsonArray();
        terminal.Remove("result");
        terminal.Remove("error");
        if (status == "succeeded") terminal["result"] = result?.DeepClone();
        if (error is not null)
        {
            var safe = (JsonObject)error.DeepClone();
            safe["flowVersion"] = reservation["flowVersion"]?.DeepClone();
            terminal["error"] = safe;
        }
        return terminal;
    }

    private static async Task<JsonObject?> ExistingAsync(IUnitOfWork uow, string receiptKey, string hash, CancellationToken ct)
    {
        var saved = await uow.Documents.GetAsync("receipts", receiptKey, ct);
        if (saved is null) return null;
        if (saved["inputHash"]?.GetValue<string>() != hash)
            throw new BosException("IDEMPOTENCY_CONFLICT", "This key is already bound to different arguments.", 409);
        return saved["result"] is JsonObject result ? (JsonObject)result.DeepClone() : throw UnknownOutcome();
    }

    private async Task<JsonObject?> ResolveOutcomeAsync(string receiptKey)
    {
        try
        {
            await using var fresh = await factory.BeginReadAsync(CancellationToken.None);
            var saved = await fresh.Documents.GetAsync("receipts", receiptKey, CancellationToken.None);
            return saved?["result"] is JsonObject run ? (JsonObject)run.DeepClone() : null;
        }
        catch { throw UnknownOutcome(); }
    }

    private async Task<ApiResult> ReplayAsync(JsonObject receipt, Caller caller, CancellationToken ct)
    {
        var visible = await VisibleReceiptAsync(receipt, Context(caller), ct);
        var status = receipt["status"]?.GetValue<string>();
        // ApiResult carries the interrupted run ID alongside its diagnostic rather than losing it in an exception.
        return new ApiResult(visible, status == "running" ? 202 : status == "interrupted" ? 409 : 200);
    }

    private static async Task CompleteAsync(IUnitOfWork uow, JsonObject terminal, string receiptKey, IEnumerable<JsonObject> steps,
        CancellationToken ct, IEnumerable<JsonObject>? logs = null)
    {
        var saved = await uow.Documents.GetAsync("receipts", receiptKey, ct) ?? throw UnknownOutcome();
        var runId = terminal["runId"]!.GetValue<string>();
        // Only the durable reservation's owner may complete it; a terminal outcome is never overwritten.
        if (saved["result"] is not JsonObject reserved || reserved["runId"]?.GetValue<string>() != runId || reserved["status"]?.GetValue<string>() != "running")
            throw UnknownOutcome();
        var items = StepMetadata(steps);
        // Validate before writing any terminal state. Failed executions retain only valid,
        // bounded explicit diagnostics; bad logging must not prevent failure persistence.
        AttachLogs(items, logs ?? [], strict: terminal["status"]?.GetValue<string>() == "succeeded");
        saved["result"] = terminal.DeepClone();
        saved["status"] = terminal["status"]?.GetValue<string>() == "interrupted" ? 409 : 200;
        await uow.Documents.PutAsync("runs", runId, WithKey(terminal, receiptKey), ct);
        await uow.Documents.PutAsync("runSteps", runId, new JsonObject { ["runId"] = runId, ["items"] = items }, ct);
        await uow.Documents.PutAsync("receipts", receiptKey, saved, ct);
    }

    private static JsonArray StepMetadata(IEnumerable<JsonObject> steps, bool includeLogs = false)
    {
        var source = steps.Take(500).ToArray();
        var items = new JsonArray();
        var bytes = 0;
        foreach (var step in source)
        {
            var item = new JsonObject { ["ordinal"] = items.Count + 1 };
            foreach (var field in new[] { "stepId", "iterationPath", "startedAt" })
                if (step[field] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length <= 512) item[field] = text;
            if (step["durationMs"] is JsonValue duration && duration.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                item["durationMs"] = duration.DeepClone();
            // Interpreter step dictionaries are internal; normalize their status/code aliases at this boundary.
            if ((step["outcome"] ?? step["status"]) is JsonValue outcome && outcome.TryGetValue<string>(out var status) &&
                status is "running" or "succeeded" or "failed" or "interrupted") item["outcome"] = status;
            if ((step["errorCode"] ?? step["code"]) is JsonValue error && error.TryGetValue<string>(out var code) &&
                code.Length <= 64 && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) item["errorCode"] = code;
            bytes += Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(item)) + 1;
            if (bytes > 256 * 1024 - 1024) break;
            items.Add(item);
        }
        if (includeLogs) AttachLogs(items, source, strict: false, stored: true);
        return items;
    }

    private static void AttachLogs(JsonArray items, IEnumerable<JsonObject> entries, bool strict, bool stored = false)
    {
        var targets = items.OfType<JsonObject>().Where(s => s["stepId"] is not null)
            .GroupBy(s => (s["stepId"]!.GetValue<string>(), s["iterationPath"]?.GetValue<string>() ?? ""))
            .ToDictionary(group => group.Key, group => group.Count() == 1 ? group.First() : null);
        var logBytes = 0;
        var documentBytes = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(items));
        foreach (var entry in entries.Take(501))
        {
            if (stored && entry["log"] is null) continue;
            try
            {
                if (entry["stepId"] is not JsonValue id || !id.TryGetValue<string>(out var stepId) ||
                    entry["iterationPath"] is not JsonValue path || !path.TryGetValue<string>(out var iterationPath) ||
                    !targets.TryGetValue((stepId, iterationPath), out var target) || target is null || target.ContainsKey("log"))
                    throw InvalidLog();
                var payload = LogPayload(stored ? entry["log"] as JsonObject ?? throw InvalidLog() : entry);
                // Include correlation overhead, matching the interpreter's cumulative log budget.
                var envelope = (JsonObject)payload.DeepClone();
                envelope["stepId"] = stepId;
                envelope["iterationPath"] = iterationPath;
                var bytes = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(envelope));
                var added = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(payload)) + 7; // ,"log":
                if (bytes > 16 * 1024 - logBytes || added > 256 * 1024 - 1024 - documentBytes)
                    throw new BosException("EXECUTION_LIMIT", "Run diagnostics exceed their storage budget.");
                target["log"] = payload;
                logBytes += bytes;
                documentBytes += added;
            }
            catch (BosException) when (!strict) { /* Discard invalid/excess payloads, preserving step metadata and terminal failure. */ }
        }
    }

    private static JsonObject LogPayload(JsonObject entry)
    {
        if (entry["level"] is not JsonValue level || !level.TryGetValue<string>(out var severity) || severity is not ("info" or "warn") ||
            entry["message"] is not JsonValue message || !message.TryGetValue<string>(out var text) || entry["fields"] is not JsonObject fields)
            throw InvalidLog();
        foreach (var (name, value) in fields)
        {
            JsonArgs.Name(name);
            if (value is not null && value is not JsonValue) throw InvalidLog();
        }
        // Only explicitly logged scalar fields cross this boundary. Never copy the caller,
        // operation input/output, exception details or other context properties into a log.
        return (JsonObject)CanonicalJson.Normalize(new JsonObject
        {
            ["level"] = severity, ["message"] = text, ["fields"] = fields.DeepClone()
        })!;
    }

    private static BosException InvalidLog() => new("LOG_INVALID", "A log must contain scalar fields and identify one unique step iteration.");

    private static JsonObject SafeError(Exception error, InvocationContext context)
    {
        var code = error is BosException bos ? bos.Code : error is OperationCanceledException
            ? (DateTimeOffset.UtcNow >= context.Budget.Deadline ? "DEADLINE_EXCEEDED" : "RUN_CANCELLED") : "INTERNAL_ERROR";
        // Exception messages/details can contain record data, SQL, paths or trusted-operation secrets.
        if (code.Length > 64 || code.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))) code = "INTERNAL_ERROR";
        return new JsonObject
        {
            ["code"] = code, ["message"] = "Flow execution failed.", ["path"] = context.StepId ?? "",
            ["stepId"] = context.StepId, ["iterationPath"] = context.IterationPath,
            ["details"] = new JsonArray(), ["traceId"] = context.CommandId, ["retryable"] = error is BosException { Retryable: true }
        };
    }

    private void CheckStorage()
    {
        var path = factory.DatabasePath;
        if (string.IsNullOrEmpty(path) || path == ":memory:") return;
        try
        {
            var full = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives().Where(d => full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
            var size = (File.Exists(full) ? new FileInfo(full).Length : 0) + (File.Exists(full + "-wal") ? new FileInfo(full + "-wal").Length : 0);
            if (size >= 2L * 1024 * 1024 * 1024 || drive is null || drive.AvailableFreeSpace < 512L * 1024 * 1024)
                throw new BosException("CAPACITY_EXCEEDED", "Storage headroom is insufficient for a new run.", 503);
        }
        catch (IOException) { throw new BosException("STORAGE_BUSY", "Storage admission is unavailable.", 503, retryable: true); }
        catch (UnauthorizedAccessException) { throw new BosException("STORAGE_BUSY", "Storage admission is unavailable.", 503, retryable: true); }
    }

    private static JsonObject WithKey(JsonObject run, string key) { var copy = (JsonObject)run.DeepClone(); copy["receiptKey"] = key; return copy; }
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    private static string Instant(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static BosException Dependency() => new("DEPENDENCY_UNAVAILABLE", "A pinned flow definition or dependency is unavailable or invalid.", 409);
    private static BosException UnknownOutcome() => new("STORAGE_BUSY", "Run outcome is unresolved. Replay the same key or poll the run; do not submit a new key.", 503);
}
