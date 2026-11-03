using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;

namespace Bos.Application;

/// <summary>The only business entry point. Nested calls reuse the root transaction and authorization snapshot.</summary>
public sealed class ApplicationApi : IApplicationApi, IDispatcher, IAttachmentApi
{
    private readonly IUnitOfWorkFactory factory;
    private readonly IAuthorization authorization;
    private readonly IIdentityService identity;
    private readonly IQueryEngine queries;
    private readonly IOperationRegistry registry;
    private readonly IFlowCompiler compiler;
    private readonly IFlowRunner runner;
    private readonly IClock clock;
    private readonly IIdGenerator ids;
    private readonly RecordService records;
    private readonly AttachmentService attachments;
    public string WorkspaceTimezone { get; set; } = "Asia/Colombo";
    public IReadOnlyList<MethodDescriptor> Methods => MethodCatalogue.All;

    public ApplicationApi(IUnitOfWorkFactory factory, IAuthorization authorization, IIdentityService identity,
        IQueryEngine queries, IOperationRegistry registry, IFlowCompiler compiler, IFlowRunner runner, IClock clock, IIdGenerator ids)
    {
        this.factory = factory; this.authorization = authorization; this.identity = identity; this.queries = queries;
        this.registry = registry; this.compiler = compiler; this.runner = runner; this.clock = clock; this.ids = ids;
        records = new(authorization, queries, clock, ids);
        attachments = new(factory, authorization, clock, ids);
    }

    public Task<JsonObject> UploadAttachmentAsync(string fileName, string contentType, byte[] content, Caller caller, string key, CancellationToken ct = default) =>
        attachments.UploadAsync(fileName, contentType, content, caller, key, WorkspaceTimezone, ct);

    public Task<AttachmentDownload> DownloadAttachmentAsync(string id, Caller caller, CancellationToken ct = default) =>
        attachments.DownloadAsync(id, caller, WorkspaceTimezone, ct);

    public async Task<ApiResult> InvokeAsync(string method, JsonObject args, Caller caller, string? key, CancellationToken ct = default)
    {
        args = (JsonObject)args.DeepClone();
        var descriptor = Methods.FirstOrDefault(x => x.Name == method);
        if (descriptor is null || descriptor.Transport != "http")
        {
            await using var read = await factory.BeginReadAsync(ct);
            var current = Context(caller);
            await authorization.ReloadAsync(read, current, ct);
            throw new BosException(MethodCatalogue.ReservedMethods.Contains(method) ? "FEATURE_NOT_AVAILABLE" : "NOT_FOUND",
                MethodCatalogue.ReservedMethods.Contains(method) ? "This method is reserved for a later release." : "Method not found.",
                MethodCatalogue.ReservedMethods.Contains(method) ? 422 : 404);
        }
        ValidateShape(descriptor.InputSchema, args);
        if (method == "flows.run")
        {
            ReceiptProtocol.ValidateKey(key!);
            return await runner.RunAsync(args, caller, key!, ct);
        }
        var mutating = descriptor.Mutating;
        if (method == "operations.call") mutating = registry.Resolve(JsonArgs.String(args, "name"), Version(args, "contractVersion")).Descriptor.Mode == "write";
        if (mutating) { ReceiptProtocol.ValidateKey(key!); CheckStorage(); }
        var context = Context(caller);
        await using var uow = mutating ? await factory.BeginWriteAsync(ct) : await factory.BeginReadAsync(ct);
        context.StartedAt = clock.UtcNow;
        context.Budget = new Budget { Deadline = DateTimeOffset.UtcNow.AddSeconds(2) };
        context.ReadOnly = !mutating;
        await authorization.ReloadAsync(uow, context, ct);
        AuthorizeMethod(method, args, context);
        var scope = ReceiptProtocol.Scope(method, args);
        if (method == "flows.create")
            scope = ReceiptProtocol.Scope(method, new JsonObject { ["name"] = compiler.Compile(Source(JsonArgs.Object(args, "definition"))).Ast["name"]!.DeepClone() });
        var receiptKey = mutating ? ReceiptProtocol.Key(caller.PrincipalId, scope, key!) : null;
        var hash = mutating ? ReceiptProtocol.Hash(method, args) : null;
        if (receiptKey is not null && await uow.Documents.GetAsync("receipts", receiptKey, ct) is { } prior)
        {
            if (prior["inputHash"]?.GetValue<string>() != hash) throw new BosException("IDEMPOTENCY_CONFLICT", "The command key was already used for different arguments.", 409);
            if (prior["readTypes"] is JsonArray types)
                foreach (var type in types) authorization.Require(context, "record.read", type!.GetValue<string>());
            return new(ReceiptProtocol.ReplayResult(method, prior["result"]));
        }
        try
        {
            var result = await ExecuteAsync(method, args, context, uow, ct);
            context.Budget.Check(ct);
            if (context.Poisoned) throw new BosException("INTERNAL_ERROR", "The transaction was aborted.", 500);
            // The flow result's 64-KiB limit is enforced by the runner. Full direct records
            // and bounded pages can be larger; check their transport bound before commit.
            ResponseBudget.Check(result);
            if (mutating)
            {
                await uow.Documents.PutAsync("receipts", receiptKey!, new JsonObject
                {
                    ["principalId"] = caller.PrincipalId, ["scope"] = scope, ["key"] = key,
                    ["inputHash"] = hash, ["result"] = ReceiptProtocol.StoredResult(method, result), ["status"] = 200,
                    ["readTypes"] = Strings(context.ReadTypes), ["commandId"] = context.CommandId
                }, ct);
                try { await uow.CommitAsync(ct); }
                catch
                {
                    // Release the uncertain connection before consulting committed state.
                    await uow.DisposeAsync();
                    await using var fresh = await factory.BeginReadAsync(CancellationToken.None);
                    var durable = await fresh.Documents.GetAsync("receipts", receiptKey!, CancellationToken.None);
                    if (durable?["inputHash"]?.GetValue<string>() != hash)
                        throw new BosException("OUTCOME_UNKNOWN", "The command outcome could not be confirmed. Replay the same command key.", 503, retryable: false);
                    var reauthorized = Context(caller);
                    await authorization.ReloadAsync(fresh, reauthorized, CancellationToken.None);
                    AuthorizeMethod(method, args, reauthorized);
                    if (durable!["readTypes"] is JsonArray readTypes)
                        foreach (var type in readTypes) authorization.Require(reauthorized, "record.read", type!.GetValue<string>());
                    return new(ReceiptProtocol.ReplayResult(method, durable["result"]));
                }
            }
            return new(result);
        }
        catch { context.Poisoned = true; throw; }
    }

    public async Task<JsonNode?> CallAsync(string name, int? version, JsonObject input, InvocationContext context, IUnitOfWork uow, CancellationToken ct = default)
    {
        var oldOwner = context.GuardOwner;
        var ceilingAdded = false;
        var entered = false;
        try
        {
            context.Budget.Call();
            context.Budget.Check(ct);
            var operation = registry.Resolve(name, version);
            var descriptor = operation.Descriptor;
            if (context.RunId is not null && !descriptor.FlowCallable) throw new BosException("FORBIDDEN", "Operation is not available to flows.", 403);
            entered = true;
            if (++context.OperationDepth > 8) throw new BosException("EXECUTION_LIMIT", "Operation composition depth exceeded.");
            if (descriptor.Mode == "write" && (context.ReadOnly || !uow.IsWrite)) throw new BosException("MODE_VIOLATION", "A read-only invocation cannot write.");
            if (name.StartsWith("records.", StringComparison.Ordinal))
                return await records.InvokeAsync(name, input, context, uow, ct);
            authorization.Require(context, "operations.execute", descriptor.Name);
            foreach (var grant in descriptor.RequiredGrants.Concat(descriptor.Capabilities)) authorization.Require(context, grant.Action, grant.Resource);
            context.Ceilings.Add(descriptor.Capabilities);
            ceilingAdded = true;
            context.GuardOwner = descriptor.Name;
            var bound = OperationBinding.Bind(descriptor.InputSchema, input, descriptor.MetadataPaths, "input");
            var result = await operation.ExecuteAsync(context, uow, bound, this, ct);
            if (result is not JsonObject obj) throw new BosException("OUTPUT_INVALID", "Operation output must match its object schema.");
            try { return OperationBinding.Bind(descriptor.OutputSchema, obj, descriptor.MetadataPaths, "output"); }
            catch (BosException) { throw new BosException("OUTPUT_INVALID", "Operation output does not match its schema."); }
        }
        catch { context.Poisoned = true; throw; }
        finally
        {
            context.GuardOwner = oldOwner;
            if (ceilingAdded) context.Ceilings.RemoveAt(context.Ceilings.Count - 1);
            if (entered) context.OperationDepth--;
        }
    }

    private async Task<JsonNode?> ExecuteAsync(string method, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        if (method.StartsWith("records.", StringComparison.Ordinal)) return await CallAsync(method, 1, args, context, uow, ct);
        if (method.StartsWith("attachments.", StringComparison.Ordinal)) return await attachments.InvokeAsync(method, args, context, uow, ct);
        if (method.StartsWith("types.", StringComparison.Ordinal)) return await TypesAsync(method, args, context, uow, ct);
        if (method.StartsWith("flows.", StringComparison.Ordinal)) return await FlowsAsync(method, args, context, uow, ct);
        if (method.StartsWith("operations.", StringComparison.Ordinal)) return await OperationsAsync(method, args, context, uow, ct);
        if (method.StartsWith("users.", StringComparison.Ordinal) || method.StartsWith("roles.", StringComparison.Ordinal) || method.StartsWith("tokens.", StringComparison.Ordinal) || method is "identity.getCurrent" or "authorization.can")
            return await identity.InvokeAsync(method, args, context, uow, ct);
        if (method == "audit.list")
            return await queries.CatalogueAsync(uow, context, "audit", Query(args),
                ["actor", "method", "commandId", "runId", "type", "objectId"], ["sequence"], _ => true, ct);
        if (method == "system.getCapabilities")
        {
            return new JsonObject
            {
                ["productVersion"] = "1.0.0-preview.1", ["specificationVersion"] = "1.0.0", ["releaseStatus"] = "reviewBuild",
                ["flowFormats"] = new JsonArray("bos.flow/1.0"), ["astFormats"] = new JsonArray("bos.ast/1.0"),
                ["methods"] = new JsonArray(Methods.Where(m => m.Transport == "http" && Discoverable(m, context)).Select(m => (JsonNode)new JsonObject
                {
                    ["name"] = m.Name, ["contractVersion"] = 1, ["transport"] = m.Transport, ["mode"] = m.Mutating ? "write" : "read",
                    ["readOnly"] = !m.Mutating, ["mutating"] = m.Mutating, ["grant"] = m.Grant, ["flowCallable"] = m.FlowCallable,
                    ["inputSchema"] = m.InputSchema.DeepClone(), ["outputSchema"] = m.OutputSchema.DeepClone()
                }).ToArray()),
                ["helpers"] = new JsonArray(Methods.Where(m => m.Transport == "expression").Select(m => (JsonNode)new JsonObject
                {
                    ["name"] = m.Name, ["contractVersion"] = 1, ["inputSchema"] = m.InputSchema.DeepClone(), ["outputSchema"] = m.OutputSchema.DeepClone()
                }).ToArray()),
                ["limits"] = new JsonObject
                {
                    ["requestBytes"] = 262144, ["yamlBytes"] = 65536, ["astBytes"] = 262144, ["staticSteps"] = 500,
                    ["schemaFields"] = 200, ["schemaDepth"] = 8, ["arrayItems"] = 200, ["recordScalars"] = 2000,
                    ["expressionDepth"] = 16, ["evaluatedNodes"] = 10000, ["loopItems"] = 100, ["sdkCalls"] = 200,
                    ["mutations"] = 250, ["returnedRecords"] = 1000, ["queryPage"] = 200, ["queryFilters"] = 10,
                    ["queryInItems"] = 50, ["queryVmInstructions"] = 1000000, ["executionMilliseconds"] = 2000,
                    ["queryMilliseconds"] = 1000, ["outputBytes"] = 65536, ["diagnosticBytes"] = 16384,
                    ["attachmentBytes"] = AttachmentService.MaximumBytes, ["recordAttachments"] = 100
                }
            };
        }
        if (method == "system.getHealth")
        {
            long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
            if (factory is not IStorageDiagnostics metrics) throw new BosException("DEPENDENCY_UNAVAILABLE", "Storage diagnostics are unavailable.", 503);
            return new JsonObject
            {
                ["status"] = "ready", ["checks"] = new JsonArray(new JsonObject { ["name"] = "storage", ["status"] = "ready" }),
                ["metrics"] = new JsonObject { ["activeReaders"] = metrics.ActiveReaders, ["activeWriters"] = metrics.ActiveWriters, ["databaseBytes"] = Size(factory.DatabasePath), ["walBytes"] = Size(factory.DatabasePath + "-wal") }
            };
        }
        throw new BosException("NOT_FOUND", "Method not found.", 404);
    }

    private async Task<JsonNode?> TypesAsync(string method, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        if (method == "types.validate")
        {
            var definition = JsonArgs.Object(args, "definition");
            authorization.Require(context, "types.manage", JsonArgs.Name(JsonArgs.String(definition, "name")));
            return Report(Schema.ValidateDefinition(definition));
        }
        if (method == "types.list")
            return await queries.CatalogueAsync(uow, context, "typeActive", Query(args), ["name", "status"], ["name"],
                row => CanReadType(context, row["name"]!.GetValue<string>()), ct);
        var name = method == "types.create" ? JsonArgs.Name(JsonArgs.String(JsonArgs.Object(args, "definition"), "name")) : JsonArgs.Name(JsonArgs.String(args, "name"));
        if (method is "types.create" or "types.update")
        {
            var definition = JsonArgs.Object(args, "definition");
            if (JsonArgs.String(definition, "name") != name) throw new BosException("INVALID_SCHEMA", "Definition name must match the requested name.");
            EnsureValid(Schema.ValidateDefinition(definition));
            await ValidateTypeDependenciesAsync(definition, uow, ct);
            var prior = await uow.Documents.GetAsync("typeDrafts", name, ct);
            if (method == "types.create" && prior is not null) throw new BosException("ALREADY_EXISTS", "Type name already exists.", 409);
            if (method == "types.update") { RequireFound(prior); Revision(prior!, "draftRevision", args); }
            if (prior is not null && CanonicalJson.Hash(prior["definition"]) == CanonicalJson.Hash(definition)) throw NoChange();
            var draft = new JsonObject
            {
                ["name"] = name, ["draftRevision"] = prior is null ? 1 : Next(prior, "draftRevision"), ["definition"] = CanonicalJson.Normalize(definition),
                ["updatedAt"] = Instant(context.StartedAt), ["updatedBy"] = context.PrincipalId
            };
            await uow.Documents.PutAsync("typeDrafts", name, draft, ct);
            await Audit(uow, context, method, name, null, prior?["draftRevision"], draft["draftRevision"], ct);
            return draft;
        }
        if (method == "types.getDraft") return RequireFound(await uow.Documents.GetAsync("typeDrafts", name, ct));
        if (method == "types.listVersions")
        {
            if (!CanReadType(context, name)) throw new BosException("NOT_FOUND", "Type not found.", 404);
            return await VersionsAsync(uow, context, "typeVersions", name, Query(args), ct);
        }
        var active = await uow.Documents.GetAsync("typeActive", name, ct);
        if (method == "types.get")
        {
            if (!CanReadType(context, name)) throw new BosException("NOT_FOUND", "Type not found.", 404);
            var version = Version(args, "version") ?? (active is null ? 0 : JsonArgs.Int(active, "version"));
            return RequireFound(await uow.Documents.GetAsync("typeVersions", name + ":" + version, ct));
        }
        if (method == "types.publish")
        {
            var draft = RequireFound(await uow.Documents.GetAsync("typeDrafts", name, ct));
            Revision(draft, "draftRevision", args);
            var definition = draft["definition"]!.AsObject();
            EnsureValid(Schema.ValidateDefinition(definition));
            await ValidateTypeDependenciesAsync(definition, uow, ct);
            var hash = CanonicalJson.Hash(definition);
            if (active is not null)
            {
                if (active["hash"]?.GetValue<string>() == hash) throw NoChange();
                var old = RequireFound(await uow.Documents.GetAsync("typeVersions", name + ":" + JsonArgs.Int(active, "version"), ct));
                Schema.CheckEvolution(old["definition"]!.AsObject(), definition);
            }
            var version = active is null ? 1 : Next(active, "version");
            var published = new JsonObject
            {
                ["name"] = name, ["version"] = version, ["hash"] = hash, ["definition"] = definition.DeepClone(),
                ["publishedAt"] = Instant(context.StartedAt), ["publishedBy"] = context.PrincipalId
            };
            await uow.Documents.PutAsync("typeVersions", name + ":" + version, published, ct);
            await uow.Documents.PutAsync("typeActive", name, new JsonObject { ["name"] = name, ["version"] = version, ["activeVersion"] = version, ["hash"] = hash, ["status"] = "published" }, ct);
            draft["draftRevision"] = Next(draft, "draftRevision"); draft["publishedHash"] = hash;
            await uow.Documents.PutAsync("typeDrafts", name, draft, ct);
            await Audit(uow, context, method, name, version, null, draft["draftRevision"], ct);
            return published;
        }
        throw new BosException("NOT_FOUND", "Method not found.", 404);
    }

    private async Task<JsonNode?> FlowsAsync(string method, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        if (method is "flows.create" or "flows.update" or "flows.validate")
        {
            var definition = JsonArgs.Object(args, "definition"); JsonArgs.Only(definition, "source");
            var source = Source(definition);
            CompiledFlow compiled;
            try { compiled = compiler.Compile(source); }
            catch (BosException e) when (method == "flows.validate")
            {
                return new JsonObject { ["valid"] = false, ["errors"] = Errors(e.Details.Count == 0 ? [new(e.Path, e.Code, e.Message)] : e.Details), ["warnings"] = new JsonArray(), ["dependencies"] = new JsonObject() };
            }
            var name = JsonArgs.Name(JsonArgs.String(compiled.Ast, "name"));
            authorization.Require(context, "flows.manage", name);
            if (method == "flows.update" && JsonArgs.String(args, "name") != name) throw new BosException("INVALID_REQUEST", "Flow name must match the definition.", 400);
            if (method == "flows.validate")
            {
                try { await ResolveFlowDependenciesAsync(compiled.Ast, uow, ct); }
                catch (BosException e) { return new JsonObject { ["valid"] = false, ["errors"] = Errors([new(e.Path, e.Code, e.Message)]), ["warnings"] = new JsonArray(), ["dependencies"] = compiled.Ast["dependencies"]!.DeepClone() }; }
                return new JsonObject { ["valid"] = true, ["errors"] = new JsonArray(), ["warnings"] = new JsonArray(), ["dependencies"] = compiled.Ast["dependencies"]!.DeepClone() };
            }
            var prior = await uow.Documents.GetAsync("flowDrafts", name, ct);
            if (method == "flows.create" && prior is not null) throw new BosException("ALREADY_EXISTS", "Flow name already exists.", 409);
            if (method == "flows.update") { RequireFound(prior); Revision(prior!, "draftRevision", args); }
            if (prior?["source"]?.GetValue<string>() == source) throw NoChange();
            var draft = new JsonObject { ["name"] = name, ["draftRevision"] = prior is null ? 1 : Next(prior, "draftRevision"), ["source"] = source, ["updatedAt"] = Instant(context.StartedAt), ["updatedBy"] = context.PrincipalId };
            await uow.Documents.PutAsync("flowDrafts", name, draft, ct);
            if (prior is null) await uow.Documents.PutAsync("flowControl", name, new JsonObject { ["name"] = name, ["revision"] = 1, ["disabled"] = false }, ct);
            await Audit(uow, context, method, name, null, prior?["draftRevision"], draft["draftRevision"], ct);
            return draft;
        }
        if (method == "flows.list")
            return await queries.CatalogueAsync(uow, context, "flowActive", Query(args), ["name", "disabled"], ["name"],
                row => CanReadFlow(context, row["name"]!.GetValue<string>()), ct);
        if (method is "flows.getRun" or "flows.getRunSteps")
        {
            var id = JsonArgs.Uuid(JsonArgs.String(args, "runId"));
            var receipt = RequireFound(await uow.Documents.GetAsync("runs", id, ct));
            var visible = await runner.VisibleReceiptAsync(receipt, context, ct);
            if (method == "flows.getRun") return visible;
            if (visible["resultRedacted"]!.GetValue<bool>()) return new JsonObject { ["items"] = new JsonArray(), ["nextCursor"] = null, ["resultRedacted"] = true };
            var steps = await uow.Documents.GetAsync("runSteps", id, ct);
            // Bounded metadata is at most 500 entries; use the same authenticated catalogue cursor path.
            return await PageStepsAsync(steps?["items"] as JsonArray ?? [], Query(args), id, context, uow, ct);
        }
        if (method == "flows.listRuns")
        {
            var query = Query(args);
            if (query["where"] is JsonArray filters)
                foreach (var filter in filters.OfType<JsonObject>())
                    if (filter["field"] is JsonValue field && field.TryGetValue<string>(out var fieldName) && fieldName == "principalId" && JsonArgs.String(filter, "value") != context.PrincipalId)
                        authorization.Require(context, "runs.readAll", "*");
            var page = await queries.CatalogueAsync(uow, context, "runs", query, ["flowName", "flowVersion", "status", "principalId"], ["-startedAt", "runId"],
                row => row["principalId"]?.GetValue<string>() == context.PrincipalId || authorization.Can(context, "runs.readAll", "*"), ct);
            foreach (var item in page["items"]!.AsArray().OfType<JsonObject>()) Keep(item, "runId", "flowName", "flowVersion", "principalId", "status", "startedAt", "completedAt");
            return page;
        }
        var nameArg = JsonArgs.Name(JsonArgs.String(args, "name"));
        if (method == "flows.getDraft") return RequireFound(await uow.Documents.GetAsync("flowDrafts", nameArg, ct));
        var control = RequireFound(await uow.Documents.GetAsync("flowControl", nameArg, ct));
        if (method is "flows.disable" or "flows.enable")
        {
            Revision(control, "revision", args);
            var disabled = method == "flows.disable";
            if (control["disabled"]!.GetValue<bool>() == disabled) throw NoChange();
            control["disabled"] = disabled; control["revision"] = Next(control, "revision");
            await uow.Documents.PutAsync("flowControl", nameArg, control, ct);
            if (await uow.Documents.GetAsync("flowActive", nameArg, ct) is { } projection)
            {
                projection["disabled"] = disabled; projection["controlRevision"] = control["revision"]!.DeepClone();
                await uow.Documents.PutAsync("flowActive", nameArg, projection, ct);
            }
            await Audit(uow, context, method, nameArg, null, null, control["revision"], ct);
            return new JsonObject { ["disabled"] = disabled, ["revision"] = control["revision"]!.DeepClone() };
        }
        if (method == "flows.listVersions")
        {
            if (!CanReadFlow(context, nameArg)) throw new BosException("NOT_FOUND", "Flow not found.", 404);
            return await VersionsAsync(uow, context, "flowVersions", nameArg, Query(args), ct);
        }
        var active = await uow.Documents.GetAsync("flowActive", nameArg, ct);
        if (method == "flows.get")
        {
            if (!CanReadFlow(context, nameArg)) throw new BosException("NOT_FOUND", "Flow not found.", 404);
            var version = Version(args, "version") ?? (active is null ? 0 : JsonArgs.Int(active, "version"));
            var published = RequireFound(await uow.Documents.GetAsync("flowVersions", nameArg + ":" + version, ct));
            published.Remove("ast");
            if (!authorization.Can(context, "flows.manage", nameArg)) published.Remove("source");
            published["disabled"] = control["disabled"]!.DeepClone(); published["controlRevision"] = control["revision"]!.DeepClone();
            return published;
        }
        if (method == "flows.publish")
        {
            var draft = RequireFound(await uow.Documents.GetAsync("flowDrafts", nameArg, ct)); Revision(draft, "draftRevision", args);
            var source = Source(draft); var compiled = compiler.Compile(source);
            await ResolveFlowDependenciesAsync(compiled.Ast, uow, ct);
            if (active?["hash"]?.GetValue<string>() == compiled.AstHash) throw NoChange();
            var version = active is null ? 1 : Next(active, "version");
            var published = new JsonObject
            {
                ["name"] = nameArg, ["version"] = version, ["hash"] = compiled.AstHash, ["sourceHash"] = compiled.SourceHash, ["astHash"] = compiled.AstHash,
                ["format"] = "bos.flow/1.0", ["source"] = source, ["ast"] = compiled.Ast.DeepClone(),
                ["inputSchema"] = compiled.Ast["inputSchema"]!.DeepClone(), ["outputSchema"] = compiled.Ast["outputSchema"]!.DeepClone(),
                ["dependencies"] = compiled.Ast["dependencies"]!.DeepClone(), ["capabilities"] = compiled.Ast["capabilities"]!.DeepClone(),
                ["publishedAt"] = Instant(context.StartedAt), ["publishedBy"] = context.PrincipalId,
                ["controlRevision"] = control["revision"]!.DeepClone(), ["disabled"] = control["disabled"]!.DeepClone()
            };
            await uow.Documents.PutAsync("flowVersions", nameArg + ":" + version, published, ct);
            await uow.Documents.PutAsync("flowActive", nameArg, new JsonObject
            {
                ["name"] = nameArg, ["version"] = version, ["activeVersion"] = version, ["hash"] = compiled.AstHash,
                ["disabled"] = control["disabled"]!.DeepClone(), ["controlRevision"] = control["revision"]!.DeepClone()
            }, ct);
            draft["draftRevision"] = Next(draft, "draftRevision");
            await uow.Documents.PutAsync("flowDrafts", nameArg, draft, ct);
            await Audit(uow, context, method, nameArg, version, null, draft["draftRevision"], ct);
            published.Remove("ast"); return published;
        }
        throw new BosException("NOT_FOUND", "Method not found.", 404);
    }

    private async Task<JsonNode?> OperationsAsync(string method, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct)
    {
        if (method == "operations.list")
            return await PageOperationsAsync(Query(args), context, uow, ct);
        var name = JsonArgs.String(args, "name");
        var operation = registry.Resolve(name, Version(args, "contractVersion"));
        if (!CanExecute(operation.Descriptor, context)) throw new BosException("FORBIDDEN", "Operation is unavailable to this principal.", 403);
        if (method == "operations.get") return Describe(operation.Descriptor);
        var input = JsonArgs.Object(args, "input");
        if (method == "operations.validate")
        {
            try { OperationBinding.Bind(operation.Descriptor.InputSchema, input, operation.Descriptor.MetadataPaths, "input"); return Report(new(true, [])); }
            catch (BosException e) { return Report(new(false, e.Details.Count == 0 ? [new(e.Path, e.Code, e.Message)] : e.Details)); }
        }
        return await CallAsync(name, operation.Descriptor.ContractVersion, input, context, uow, ct);
    }

    private void AuthorizeMethod(string method, JsonObject args, InvocationContext context)
    {
        if (method.StartsWith("types.", StringComparison.Ordinal))
        {
            if (method is "types.list" or "types.get" or "types.listVersions") return;
            var name = method is "types.create" or "types.validate" ? JsonArgs.Name(JsonArgs.String(JsonArgs.Object(args, "definition"), "name")) : JsonArgs.Name(JsonArgs.String(args, "name"));
            authorization.Require(context, "types.manage", name); return;
        }
        if (method.StartsWith("records.", StringComparison.Ordinal))
        {
            var type = JsonArgs.Name(JsonArgs.String(args, "type"));
            if (method is "records.get" or "records.getRevision" or "records.getHistory") { if (!authorization.Can(context, "record.read", type)) throw new BosException("NOT_FOUND", "Record not found.", 404); return; }
            authorization.Require(context, "record.read", type);
            if (method == "records.validate")
            {
                if (!authorization.Can(context, "record.create", type) && !authorization.Can(context, "record.update", type)) throw new BosException("FORBIDDEN", "Validation requires create or update permission.", 403);
            }
            else if (method is "records.create" or "records.update" or "records.delete") authorization.Require(context, "record." + method[8..], type);
            return;
        }
        if (method.StartsWith("attachments.", StringComparison.Ordinal))
        {
            var type = JsonArgs.Name(JsonArgs.String(args, "type"));
            if (!authorization.Can(context, "record.read", type)) throw new BosException("NOT_FOUND", "Record not found.", 404);
            if (method is "attachments.link" or "attachments.remove" && !authorization.Can(context, "record.update", type)) throw new BosException("NOT_FOUND", "Record not found.", 404);
            return;
        }
        if (method.StartsWith("flows.", StringComparison.Ordinal))
        {
            if (method is "flows.create" or "flows.validate")
            {
                if (!context.Grants.Any(g => g.Action == "flows.manage" && authorization.Can(context, g.Action, g.Resource)))
                    throw new BosException("FORBIDDEN", "Flow management permission is required.", 403);
                if (method == "flows.create")
                    authorization.Require(context, "flows.manage", JsonArgs.String(compiler.Compile(Source(JsonArgs.Object(args, "definition"))).Ast, "name"));
                return;
            }
            if (method is "flows.list" or "flows.get" or "flows.listVersions" or "flows.getRun" or "flows.getRunSteps" or "flows.listRuns") return;
            authorization.Require(context, "flows.manage", JsonArgs.Name(JsonArgs.String(args, "name"))); return;
        }
        if (method.StartsWith("operations.", StringComparison.Ordinal))
        {
            if (method == "operations.list") return;
            var descriptor = registry.Resolve(JsonArgs.String(args, "name"), Version(args, "contractVersion")).Descriptor;
            if (descriptor.Name.StartsWith("records.", StringComparison.Ordinal))
            {
                if (method == "operations.call") AuthorizeMethod(descriptor.Name, JsonArgs.Object(args, "input"), context);
                else if (!CanExecute(descriptor, context)) throw new BosException("FORBIDDEN", "Operation unavailable.", 403);
            }
            else
            {
                authorization.Require(context, "operations.execute", descriptor.Name);
                foreach (var grant in descriptor.RequiredGrants.Concat(descriptor.Capabilities)) authorization.Require(context, grant.Action, grant.Resource);
            }
            return;
        }
        if (method is "identity.getCurrent" or "authorization.can" or "system.getCapabilities") return;
        authorization.Require(context, method.StartsWith("audit.", StringComparison.Ordinal) ? "audit.read" : method.StartsWith("system.", StringComparison.Ordinal) ? "system.manage" : "identity.manage", "*");
    }
    private bool Discoverable(MethodDescriptor method, InvocationContext context)
    {
        if (method.Name is "identity.getCurrent" or "authorization.can" or "system.getCapabilities") return true;
        if (method.Name.StartsWith("operations.", StringComparison.Ordinal)) return registry.Descriptors.Any(d => CanExecute(d, context));
        if (method.Name.StartsWith("records.", StringComparison.Ordinal)) return CanExecute(registry.Resolve(method.Name, 1).Descriptor, context);
        if (method.Name.StartsWith("attachments.", StringComparison.Ordinal))
        {
            var action = method.Name is "attachments.link" or "attachments.remove" ? "record.update" : "record.read";
            return context.Grants.Select(g => g.Resource).Distinct(StringComparer.Ordinal)
                .Any(resource => authorization.Can(context, "record.read", resource) && authorization.Can(context, action, resource));
        }
        if (method.Name is "types.get" or "types.list" or "types.listVersions")
            return HasAny("types.read") || HasAny("record.read");
        if (method.Name is "flows.get" or "flows.list" or "flows.listVersions")
            return HasAny("flows.read") || HasAny("flows.execute");
        if (method.Grant.Length == 0) return true;
        return context.Grants.Any(g => g.Action == method.Grant && authorization.Can(context, g.Action, g.Resource)) || authorization.Can(context, method.Grant, "*");
        bool HasAny(string action) => context.Grants.Any(g => g.Action == action && authorization.Can(context, action, g.Resource));
    }
    private bool CanExecute(OperationDescriptor descriptor, InvocationContext context)
    {
        if (descriptor.Name.StartsWith("records.", StringComparison.Ordinal))
        {
            var action = descriptor.Name is "records.create" or "records.update" or "records.delete" ? "record." + descriptor.Name[8..] : "record.read";
            var resources = context.Grants.Select(g => g.Resource).Distinct(StringComparer.Ordinal);
            return resources.Any(resource => authorization.Can(context, "record.read", resource) && authorization.Can(context, action, resource) &&
                (descriptor.Name != "records.validate" || authorization.Can(context, "record.create", resource) || authorization.Can(context, "record.update", resource)));
        }
        return authorization.Can(context, "operations.execute", descriptor.Name) && descriptor.RequiredGrants.Concat(descriptor.Capabilities).All(g => authorization.Can(context, g.Action, g.Resource));
    }
    private bool CanReadType(InvocationContext c, string name) => authorization.Can(c, "types.read", name) || authorization.Can(c, "record.read", name);
    private bool CanReadFlow(InvocationContext c, string name) => authorization.Can(c, "flows.read", name) || authorization.Can(c, "flows.execute", name);

    private async Task ValidateTypeDependenciesAsync(JsonObject definition, IUnitOfWork uow, CancellationToken ct)
    {
        if (definition["allowedOperations"] is JsonArray allowed) foreach (var operation in allowed) registry.Resolve(operation!.GetValue<string>());
        async Task Fields(JsonObject fields)
        {
            foreach (var field in fields.Select(x => x.Value!.AsObject()))
            {
                if (field["managedBy"] is JsonArray managed) foreach (var operation in managed) registry.Resolve(operation!.GetValue<string>());
                var kind = field["kind"]?.GetValue<string>();
                if (kind is "reference" or "versionedReference")
                {
                    var target = JsonArgs.String(field, "targetType");
                    if (target != JsonArgs.String(definition, "name") && await uow.Documents.GetAsync("typeActive", target, ct) is null)
                        throw new BosException("DEPENDENCY_UNAVAILABLE", "Reference target type is not published.", 409);
                }
                if (kind == "polymorphicReference")
                    foreach (var target in field["targetTypes"]!.AsArray().Select(value => value!.GetValue<string>()))
                        if (target != JsonArgs.String(definition, "name") && await uow.Documents.GetAsync("typeActive", target, ct) is null)
                            throw new BosException("DEPENDENCY_UNAVAILABLE", "Reference target type is not published.", 409);
                if (field["fields"] is JsonObject nested) await Fields(nested);
                if (field["items"] is JsonObject item) await Fields(new JsonObject { ["item"] = item.DeepClone() });
                if (field["values"] is JsonObject values) await Fields(new JsonObject { ["value"] = values.DeepClone() });
                if (field["variants"] is JsonObject variants) await Fields(variants);
            }
        }
        await Fields(definition["fields"]!.AsObject());
    }
    private async Task ResolveFlowDependenciesAsync(JsonObject ast, IUnitOfWork uow, CancellationToken ct)
    {
        var dependencies = ast["dependencies"]!.AsObject();
        foreach (var (name, versions) in dependencies["types"]!.AsObject())
            foreach (var version in versions!.AsArray())
                if (await uow.Documents.GetAsync("typeVersions", name + ":" + version!.GetValue<int>(), ct) is null)
                    throw new BosException("DEPENDENCY_UNAVAILABLE", "A pinned type version is unavailable.", 409);
        foreach (var (name, version) in dependencies["operations"]!.AsObject()) registry.Resolve(name, version!.GetValue<int>());
    }
    private async Task<JsonObject> VersionsAsync(IUnitOfWork uow, InvocationContext context, string collection, string name, JsonObject query, CancellationToken ct)
    {
        // Parent name is a required fixed query input, not a caller-selectable catalogue filter.
        if (query["where"] is JsonArray where && where.Count != 0) throw new BosException("INVALID_REQUEST", "Version catalogues do not accept filters.", 400);
        context.CursorScope = collection + ":" + name;
        var page = await queries.CatalogueAsync(uow, context, collection, query, [], ["version"], row => row["name"]?.GetValue<string>() == name, ct);
        foreach (var row in page["items"]!.AsArray().OfType<JsonObject>()) Keep(row, "name", "version", "hash", "publishedAt");
        return page;
    }
    private Task<JsonObject> PageOperationsAsync(JsonObject query, InvocationContext context, IUnitOfWork uow, CancellationToken ct) =>
        queries.CatalogueValuesAsync(context, "operationDescriptors", registry.Descriptors.Select(Describe).ToArray(),
            query, ["name", "mode", "flowCallable"], ["name", "contractVersion"], row => CanExecute(registry.Resolve(row["name"]!.GetValue<string>(), row["contractVersion"]!.GetValue<int>()).Descriptor, context), ct);
    private Task<JsonObject> PageStepsAsync(JsonArray items, JsonObject query, string runId, InvocationContext context, IUnitOfWork uow, CancellationToken ct) =>
        queries.CatalogueValuesAsync(context, "steps:" + runId, items.OfType<JsonObject>().ToArray(), query,
            ["stepId", "outcome"], ["ordinal"], _ => true, ct);

    private InvocationContext Context(Caller caller) => new() { Caller = caller, CommandId = ids.NewId(), StartedAt = clock.UtcNow, WorkspaceTimezone = WorkspaceTimezone };
    private static JsonObject Describe(OperationDescriptor d) => new()
    {
        ["name"] = d.Name, ["contractVersion"] = d.ContractVersion, ["description"] = d.Description,
        ["inputSchema"] = d.InputSchema.DeepClone(), ["outputSchema"] = d.OutputSchema.DeepClone(), ["mode"] = d.Mode, ["flowCallable"] = d.FlowCallable,
        ["requiredGrants"] = Grants(d.RequiredGrants), ["capabilities"] = Grants(d.Capabilities),
        ["metadataPaths"] = Strings(d.MetadataPaths ?? [])
    };
    private static JsonArray Grants(IEnumerable<Grant> grants) => new(grants.Select(g => (JsonNode)new JsonObject { ["action"] = g.Action, ["resource"] = g.Resource }).ToArray());
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Order(StringComparer.Ordinal).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
    private static JsonObject Query(JsonObject args) => JsonArgs.Object(args, "query", new JsonObject());
    private static string Source(JsonObject value) => value["source"] is JsonValue raw && raw.GetValueKind() == JsonValueKind.String
        ? raw.GetValue<string>() : throw new BosException("INVALID_REQUEST", "Source must be a YAML string.", 400, "source");
    private static int? Version(JsonObject args, string field)
    {
        if (!args.ContainsKey(field)) return null;
        var version = JsonArgs.Int(args, field);
        return version > 0 ? version : throw new BosException("INVALID_REQUEST", "Version must be positive.", 400, field);
    }
    private static string Instant(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static JsonObject RequireFound(JsonObject? value) => value ?? throw new BosException("NOT_FOUND", "Object not found.", 404);
    private static BosException NoChange() => new("NO_CHANGE", "No normalized values changed.", 409);
    private static int Next(JsonObject value, string key) => JsonArgs.Int(value, key) == int.MaxValue ? throw new BosException("EXECUTION_LIMIT", "Revision/version range exhausted.") : checked(JsonArgs.Int(value, key) + 1);
    private static void Revision(JsonObject value, string field, JsonObject args)
    {
        var expected = JsonArgs.Int(args, "expectedRevision");
        if (expected <= 0) throw new BosException("INVALID_REQUEST", "Expected revision must be positive.", 400);
        if (expected != JsonArgs.Int(value, field)) throw new BosException("REVISION_CONFLICT", "Revision does not match.", 412);
    }
    private static JsonArray Errors(IReadOnlyList<FieldError> errors) => new(errors.Take(100).Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["code"] = e.Code, ["message"] = e.Message }).ToArray());
    private static JsonObject Report(ValidationReport report) => new() { ["valid"] = report.Valid, ["errors"] = Errors(report.Errors) };
    private static void EnsureValid(ValidationReport report) { if (!report.Valid) throw new BosException("INVALID_SCHEMA", "Definition is invalid.") { Details = report.Errors }; }
    private static void Keep(JsonObject row, params string[] fields) { foreach (var key in row.Select(x => x.Key).Except(fields).ToArray()) row.Remove(key); }
    private static void ValidateShape(JsonObject schema, JsonObject args)
    {
        if (schema["properties"] is JsonObject properties) JsonArgs.Only(args, properties.Select(x => x.Key).ToArray());
        if (schema["required"] is JsonArray required)
            foreach (var field in required) if (!args.ContainsKey(field!.GetValue<string>())) throw new BosException("INVALID_REQUEST", "Required argument is missing.", 400, field.GetValue<string>());
    }
    private async Task Audit(IUnitOfWork uow, InvocationContext c, string method, string name, int? version, JsonNode? oldRevision, JsonNode? newRevision, CancellationToken ct) =>
        await uow.Documents.AppendAuditAsync(new JsonObject
        {
            ["timestamp"] = Instant(c.StartedAt), ["actor"] = c.PrincipalId, ["credentialId"] = c.Caller.CredentialId, ["method"] = method,
            ["commandId"] = c.CommandId, ["runId"] = c.RunId, ["objectId"] = name, ["type"] = name, ["version"] = version,
            ["oldRevision"] = oldRevision?.DeepClone(), ["newRevision"] = newRevision?.DeepClone(), ["outcome"] = "succeeded"
        }, ct);
    private void CheckStorage()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(factory.DatabasePath))!;
        if (new DriveInfo(directory).AvailableFreeSpace < 512L * 1024 * 1024 ||
            (File.Exists(factory.DatabasePath) ? new FileInfo(factory.DatabasePath).Length : 0) +
            (File.Exists(factory.DatabasePath + "-wal") ? new FileInfo(factory.DatabasePath + "-wal").Length : 0) >= 2L * 1024 * 1024 * 1024)
            throw new BosException("CAPACITY_EXCEEDED", "Workspace storage capacity is unavailable.", 503);
    }
}
