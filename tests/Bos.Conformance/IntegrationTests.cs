using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;
using Bos.Host;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Bos.Conformance.TestWorkspace;

namespace Bos.Conformance;

/// <summary>
/// Independent end-to-end scenarios. Gate traits identify scenario coverage, not certification
/// of an entire A01–A24 gate (crash, packaging, backup and storage faults need separate evidence).
/// </summary>
public sealed class IntegrationTests
{
    [Fact, Trait("Gate", "A03")]
    public async Task Type_publication_retains_draft_and_old_versions_and_failed_evolution_is_atomic()
    {
        await using var workspace = await CreateAsync();
        var definition = NoteDefinition();
        var published = await workspace.PublishTypeAsync(definition);
        Assert.Equal(1, Number(published, "version"));
        var original = CanonicalJson.Serialize(published);
        var draft = await workspace.CallAsync("types.getDraft", new JsonObject { ["name"] = "Note" });
        Assert.Equal(2, Number(draft, "draftRevision"));
        var stale = await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("types.update", new JsonObject
        {
            ["name"] = "Note", ["expectedRevision"] = 1, ["definition"] = definition.DeepClone()
        }));
        Assert.Equal("REVISION_CONFLICT", stale.Code);
        var unchanged = await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("types.publish", new JsonObject
        {
            ["name"] = "Note", ["expectedRevision"] = 2
        }));
        Assert.Equal("NO_CHANGE", unchanged.Code);
        definition["fields"]!["optional"] = new JsonObject { ["kind"] = "string" };
        draft = await workspace.CallAsync("types.update", new JsonObject
        {
            ["name"] = "Note", ["expectedRevision"] = 2, ["definition"] = definition.DeepClone()
        });
        published = await workspace.CallAsync("types.publish", new JsonObject
        {
            ["name"] = "Note", ["expectedRevision"] = Number(draft, "draftRevision")
        });
        Assert.Equal(2, Number(published, "version"));
        var old = await workspace.CallAsync("types.get", new JsonObject { ["name"] = "Note", ["version"] = 1 });
        Assert.Equal(original, CanonicalJson.Serialize(old));
        draft = await workspace.CallAsync("types.getDraft", new JsonObject { ["name"] = "Note" });
        definition["fields"]!["title"]!["kind"] = "integer";
        definition["fields"]!["title"]!.AsObject().Remove("maxLength");
        draft = await workspace.CallAsync("types.update", new JsonObject
        {
            ["name"] = "Note", ["expectedRevision"] = Number(draft, "draftRevision"),
            ["definition"] = definition.DeepClone()
        });
        var before = await DefinitionState(workspace);
        await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("types.publish", new JsonObject
        {
            ["name"] = "Note", ["expectedRevision"] = Number(draft, "draftRevision")
        }));
        Assert.Equal(before, await DefinitionState(workspace));
    }

    [Fact, Trait("Gate", "A03"), Trait("Gate", "A06")]
    public async Task Crud_noop_and_stale_preconditions_preserve_history_and_tombstone_data()
    {
        await using var workspace = await CreateAsync();
        await workspace.PublishTypeAsync(NoteDefinition());
        var created = await workspace.CreateRecordAsync("Note", Parse("""{"title":"Original"}"""));
        Assert.Equal(1, Number(created, "revision"));
        var id = Text(created, "id");
        AssertJsonEqual(created, await workspace.RecordAsync("Note", id));
        var before = await workspace.BusinessSnapshotAsync();
        var audit = await workspace.RecordAuditSnapshotAsync(id);
        var noop = await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("records.update", Update(created, "Original")));
        Assert.Equal("NO_CHANGE", noop.Code);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
        Assert.Equal(audit, await workspace.RecordAuditSnapshotAsync(id));
        var updated = await workspace.CallAsync("records.update", Update(created, "Changed"));
        Assert.Equal(2, Number(updated, "revision"));
        var stale = await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("records.update", Update(created, "Lost update")));
        Assert.Equal("REVISION_CONFLICT", stale.Code);
        var deleted = await workspace.CallAsync("records.delete", Delete(updated));
        Assert.Equal(3, Number(deleted, "revision"));
        Assert.True(deleted["deleted"]!.GetValue<bool>());
        var missing = await Assert.ThrowsAsync<BosException>(() => workspace.RecordAsync("Note", id));
        Assert.Equal("NOT_FOUND", missing.Code);
        await using var uow = await workspace.Factory.BeginReadAsync();
        var tombstone = await uow.Records.GetAsync(id);
        Assert.NotNull(tombstone);
        Assert.True(tombstone["deleted"]!.GetValue<bool>());
        Assert.Equal("Changed", Text(tombstone["data"]!, "title"));
    }

    [Fact, Trait("Gate", "A06")]
    public async Task Separate_root_transactions_racing_the_same_revision_commit_exactly_one_update()
    {
        await using var workspace = await CreateAsync();
        await workspace.PublishTypeAsync(NoteDefinition());
        var record = await workspace.CreateRecordAsync("Note", Parse("""{"title":"Before"}"""));
        // Hold a separate read snapshot while both independent application invocations contend.
        await using var snapshot = await workspace.Factory.BeginReadAsync();
        Assert.NotNull(await snapshot.Records.GetAsync(Text(record, "id")));
        var results = await Race(
            () => workspace.CallAsync("records.update", Update(record, "Left")),
            () => workspace.CallAsync("records.update", Update(record, "Right")));
        Assert.Single(results, result => result.Error is null);
        Assert.Equal("REVISION_CONFLICT", Assert.Single(results, result => result.Error is not null).Error!.Code);
        var winner = results.Single(result => result.Error is null).Result!;
        var current = await workspace.RecordAsync("Note", Text(record, "id"));
        Assert.Equal(2, Number(current, "revision"));
        AssertJsonEqual(winner, current);
        Assert.Equal(1, Number((await snapshot.Records.GetAsync(Text(record, "id")))!, "revision"));
    }

    [Fact, Trait("Gate", "A08")]
    public async Task Concurrent_unique_inserts_and_updates_conflict_and_tombstones_release_reservations()
    {
        await using var workspace = await CreateAsync();
        var definition = NoteDefinition();
        definition["fields"]!["title"]!["unique"] = true;
        await workspace.PublishTypeAsync(definition);
        var inserts = await Race(
            () => workspace.CreateRecordAsync("Note", Parse("""{"title":"Duplicate"}""")),
            () => workspace.CreateRecordAsync("Note", Parse("""{"title":"Duplicate"}""")));
        Assert.Single(inserts, result => result.Error is null);
        Assert.Equal(409, Assert.Single(inserts, result => result.Error is not null).Error!.Status);
        var first = await workspace.CreateRecordAsync("Note", Parse("""{"title":"First"}"""));
        var second = await workspace.CreateRecordAsync("Note", Parse("""{"title":"Second"}"""));
        var updates = await Race(
            () => workspace.CallAsync("records.update", Update(first, "Contended")),
            () => workspace.CallAsync("records.update", Update(second, "Contended")));
        Assert.Single(updates, result => result.Error is null);
        Assert.Equal(409, Assert.Single(updates, result => result.Error is not null).Error!.Status);
        await workspace.CallAsync("records.delete", Delete(inserts.Single(result => result.Error is null).Result!));
        Assert.Equal("Duplicate", Text((await workspace.CreateRecordAsync("Note", Parse("""{"title":"Duplicate"}""")))["data"]!, "title"));
        var count = await workspace.CallAsync("records.count", new JsonObject { ["type"] = "Note", ["filter"] = new JsonObject() });
        Assert.Equal("3", Text(count, "count"));
    }

    [Fact, Trait("Gate", "A08")]
    public async Task Nested_object_array_references_restrict_delete_and_deleted_sources_release_them()
    {
        await using var workspace = await CreateAsync();
        await workspace.PublishTypeAsync(NoteDefinition());
        await workspace.PublishTypeAsync(ReferenceDefinition());
        var target = await workspace.CreateRecordAsync("Note", Parse("""{"title":"Target"}"""));
        var holder = await workspace.CreateRecordAsync("ReferenceHolder", HolderData(Text(target, "id")));
        var before = await workspace.BusinessSnapshotAsync();
        var blocked = await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("records.delete", Delete(target)));
        Assert.Equal(409, blocked.Status);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
        await workspace.CallAsync("records.delete", Delete(holder));
        Assert.True((await workspace.CallAsync("records.delete", Delete(target)))["deleted"]!.GetValue<bool>());
    }

    [Fact, Trait("Gate", "A05"), Trait("Gate", "A08")]
    public async Task Failed_flow_rolls_back_two_staged_updates_history_indexes_uniques_references_and_audit()
    {
        var staged = new List<string>();
        await using var workspace = await CreateAsync(registry => registry.Register(new AtomicFailureOperation(staged)));
        await workspace.PublishTypeAsync(NoteDefinition());
        await workspace.PublishTypeAsync(ReferenceDefinition());
        var oldTarget = await workspace.CreateRecordAsync("Note", Parse("""{"title":"Old"}"""));
        var newTarget = await workspace.CreateRecordAsync("Note", Parse("""{"title":"New"}"""));
        var first = await workspace.CreateRecordAsync("ReferenceHolder", HolderData(Text(oldTarget, "id"), "First"));
        var second = await workspace.CreateRecordAsync("ReferenceHolder", HolderData(Text(oldTarget, "id"), "Second"));
        await workspace.PublishFlowAsync("AtomicFailure", AtomicFailureFlow);
        var before = await workspace.BusinessSnapshotAsync();
        var audit = await workspace.RecordAuditSnapshotAsync(Text(first, "id"), Text(second, "id"));
        var receipt = await workspace.CallAsync("flows.run", new JsonObject
        {
            ["name"] = "AtomicFailure", ["version"] = 1,
            ["input"] = new JsonObject
            {
                ["first"] = Text(first, "id"), ["second"] = Text(second, "id"), ["target"] = Text(newTarget, "id")
            }
        });
        Assert.Equal("failed", Text(receipt, "status"));
        Assert.Equal("TEST_AFTER_TWO_WRITES", Text(receipt["error"]!, "code"));
        Assert.Equal(2, staged.Count); // proves failure was reached after real staged writes
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
        Assert.Equal(audit, await workspace.RecordAuditSnapshotAsync(Text(first, "id"), Text(second, "id")));
        Assert.True((await workspace.CallAsync("records.delete", Delete(newTarget)))["deleted"]!.GetValue<bool>());
        Assert.Equal(409, (await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("records.delete", Delete(oldTarget)))).Status);
    }

    [Fact, Trait("Gate", "A07")]
    public async Task Two_trusted_stock_operations_cannot_both_consume_the_last_unit()
    {
        await using var workspace = await CreateAsync(registry => registry.Register(new ConsumeStockOperation()));
        await workspace.PublishTypeAsync(Parse("""
            {"name":"Stock","description":"Stock race fixture","accessMode":"direct","allowedOperations":[],
             "fields":{"remaining":{"kind":"integer","required":true,"minimum":"0","indexed":true}}}
            """));
        var record = await workspace.CreateRecordAsync("Stock", Parse("""{"remaining":"1"}"""));
        var results = await Race(
            () => workspace.OperationAsync("TestConsumeStock", new JsonObject { ["id"] = Text(record, "id") }),
            () => workspace.OperationAsync("TestConsumeStock", new JsonObject { ["id"] = Text(record, "id") }));
        Assert.Single(results, result => result.Error is null);
        Assert.Equal("OUT_OF_STOCK", Assert.Single(results, result => result.Error is not null).Error!.Code);
        var current = await workspace.RecordAsync("Stock", Text(record, "id"));
        Assert.Equal("0", Text(current["data"]!, "remaining"));
        Assert.Equal(2, Number(current, "revision"));
    }

    [Fact, Trait("Gate", "A12")]
    public async Task Invalid_registered_operation_output_rolls_back_its_real_record_write()
    {
        var operation = new InvalidOutputOperation();
        await using var workspace = await CreateAsync(registry => registry.Register(operation));
        await workspace.PublishTypeAsync(NoteDefinition());
        var record = await workspace.CreateRecordAsync("Note", Parse("""{"title":"Original"}"""));
        var before = await workspace.BusinessSnapshotAsync();
        await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("TestInvalidOutput", new JsonObject { ["id"] = Text(record, "id") }));
        Assert.True(operation.StagedWrite);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
    }

    [Fact, Trait("Gate", "A04"), Trait("Gate", "A23")]
    public async Task Reference_flow_calculates_900_and_approval_and_direct_write_guards_hold()
    {
        await using var workspace = await CreateAsync();
        await workspace.InstallReferenceAsync();
        var created = await workspace.OperationAsync("CreateRequest", ExampleJson("CreateRequest.input.json"));
        var request = created["request"]!.AsObject();
        var direct = await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("records.update", new JsonObject
        {
            ["type"] = "MaterialRequest", ["id"] = Text(request, "id"), ["expectedRevision"] = Number(request, "revision"),
            ["patch"] = new JsonObject { ["status"] = "Approved" }
        }));
        Assert.Contains(direct.Status, new[] { 403, 422 });
        var receipt = await workspace.SubmitAsync(request);
        Assert.Equal("succeeded", Text(receipt, "status"));
        Assert.Equal("900.00", Text(receipt["result"]!, "total"));
        request = await workspace.RecordAsync("MaterialRequest", Text(request, "id"));
        Assert.Equal("Submitted", Text(request["data"]!, "status"));
        Assert.Equal("900.00", Text(request["data"]!, "total"));
        Assert.Equal(new[] { "300.00", "600.00" }, workspace.StagedPrices.Select(line => Text(line["data"]!, "amount")));
        var approved = await workspace.OperationAsync("ApproveRequest", RevisionInput(request));
        Assert.Equal("Approved", Text(approved["data"]!, "status"));
        var again = await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("ApproveRequest", RevisionInput(approved)));
        Assert.Equal("INVALID_STATE", again.Code);
        var revise = RevisionInput(approved);
        revise["title"] = "Forbidden after approval";
        Assert.Equal("INVALID_STATE", (await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("ReviseRequest", revise))).Code);
    }

    [Fact, Trait("Gate", "A23"), Trait("Gate", "A05")]
    public async Task Line_only_revision_invalidates_captured_parent_intent_and_stale_submit_rolls_back_pricing()
    {
        await using var workspace = await CreateAsync();
        await workspace.InstallReferenceAsync();
        var created = await workspace.OperationAsync("CreateRequest", ExampleJson("CreateRequest.input.json"));
        var parent = created["request"]!.AsObject();
        var line = created["lines"]![0]!;
        var input = RevisionInput(parent);
        var change = RevisionInput(line);
        change["quantity"] = "4.000";
        input["lineChanges"] = new JsonArray(change);
        var revised = await workspace.OperationAsync("ReviseRequest", input);
        var current = revised["request"]!;
        Assert.Equal(Number(parent, "revision") + 1, Number(current, "revision"));
        Assert.Equal("2", Text(current["data"]!, "contentVersion"));
        var before = await workspace.BusinessSnapshotAsync();
        var failed = await workspace.SubmitAsync(parent);
        Assert.Equal("failed", Text(failed, "status"));
        Assert.Equal("REVISION_CONFLICT", Text(failed["error"]!, "code"));
        Assert.Equal(2, workspace.StagedPrices.Count);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
    }

    [Fact, Trait("Gate", "A05"), Trait("Gate", "A23")]
    public async Task Reference_overflow_after_first_staged_line_rolls_back_every_business_effect()
    {
        await using var workspace = await CreateAsync();
        await workspace.InstallReferenceAsync();
        var input = ExampleJson("CreateRequest.input.json");
        input["lines"]![1]!["quantity"] = "999999999.999";
        input["lines"]![1]!["unitPrice"] = "9999999999999999.99";
        var created = await workspace.OperationAsync("CreateRequest", input);
        var request = created["request"]!.AsObject();
        var ids = created["lines"]!.AsArray().Select(line => Text(line!, "id")).Prepend(Text(request, "id")).ToArray();
        var before = await workspace.BusinessSnapshotAsync();
        var audit = await workspace.RecordAuditSnapshotAsync(ids);
        var failed = await workspace.SubmitAsync(request);
        Assert.Equal("failed", Text(failed, "status"));
        Assert.Equal("DECIMAL_OVERFLOW", Text(failed["error"]!, "code"));
        Assert.Single(workspace.StagedPrices);
        Assert.Equal("300.00", Text(workspace.StagedPrices[0]["data"]!, "amount"));
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
        Assert.Equal(audit, await workspace.RecordAuditSnapshotAsync(ids));
    }

    [Fact, Trait("Gate", "A05"), Trait("Gate", "A23")]
    public async Task Trusted_test_finalization_failure_after_both_prices_rolls_back_lines_parent_and_history()
    {
        await using var workspace = await CreateAsync(failFinalization: true);
        await workspace.InstallReferenceAsync();
        var created = await workspace.OperationAsync("CreateRequest", ExampleJson("CreateRequest.input.json"));
        var before = await workspace.BusinessSnapshotAsync();
        var failed = await workspace.SubmitAsync(created["request"]!.AsObject());
        Assert.Equal("failed", Text(failed, "status"));
        Assert.Equal("TEST_FINALIZATION_FAILURE", Text(failed["error"]!, "code"));
        Assert.True(workspace.FinalizationEntered);
        Assert.Equal(2, workspace.StagedPrices.Count);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
        var durable = await workspace.CallAsync("flows.getRun", new JsonObject { ["runId"] = Text(failed, "runId") });
        Assert.Equal("failed", Text(durable, "status"));
        Assert.Equal("TEST_FINALIZATION_FAILURE", Text(durable["error"]!, "code"));
        var steps = await workspace.CallAsync("flows.getRunSteps", new JsonObject { ["runId"] = Text(failed, "runId") });
        Assert.Contains(steps["items"]!.AsArray(), step => Text(step!, "stepId") == "finalize" && Text(step!, "outcome") == "failed");
    }

    [Fact, Trait("Gate", "A23")]
    public async Task Direct_finalization_cannot_submit_unpriced_lines_or_skip_recomputation()
    {
        await using var workspace = await CreateAsync();
        await workspace.InstallReferenceAsync();
        var created = await workspace.OperationAsync("CreateRequest", ExampleJson("CreateRequest.input.json"));
        var before = await workspace.BusinessSnapshotAsync();
        var failure = await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("FinalizeRequest", RevisionInput(created["request"]!)));
        Assert.Equal("AMOUNT_MISMATCH", failure.Code);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
    }

    [Fact, Trait("Gate", "A24")]
    public async Task Every_last_admin_loss_path_is_rejected_without_identity_receipt_or_audit_changes()
    {
        await using var workspace = await CreateAsync();
        var current = await workspace.CallAsync("identity.getCurrent", new JsonObject());
        var grants = current["effectiveGrants"]!.AsArray();
        foreach (var action in AdminActions)
            Assert.Contains(grants, grant => Text(grant!, "action") == action && Text(grant!, "resource") == "*");
        var admin = await workspace.CallAsync("users.get", new JsonObject { ["id"] = workspace.Admin.PrincipalId });
        var roleId = Assert.Single(admin["roles"]!.AsArray())!.GetValue<string>();
        var role = await workspace.CallAsync("roles.get", new JsonObject { ["id"] = roleId });
        var before = await IdentityState(workspace);
        var cases = new (string Method, JsonObject Args)[]
        {
            ("users.disable", new JsonObject { ["id"] = workspace.Admin.PrincipalId, ["expectedRevision"] = Number(admin, "revision") }),
            ("users.update", new JsonObject
            {
                ["id"] = workspace.Admin.PrincipalId, ["expectedRevision"] = Number(admin, "revision"),
                ["changes"] = new JsonObject { ["active"] = false }
            }),
            ("roles.remove", new JsonObject
            {
                ["userId"] = workspace.Admin.PrincipalId, ["roleId"] = roleId, ["expectedUserRevision"] = Number(admin, "revision")
            }),
            ("roles.update", new JsonObject
            {
                ["id"] = roleId, ["expectedRevision"] = Number(role, "revision"),
                ["changes"] = new JsonObject { ["grants"] = new JsonArray() }
            })
        };
        foreach (var (method, args) in cases)
        {
            await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync(method, args));
            Assert.Equal(before, await IdentityState(workspace));
        }
    }

    [Fact, Trait("Gate", "A04")]
    public async Task Real_scoped_service_token_cannot_gain_write_access_through_operation_or_flow()
    {
        await using var workspace = await CreateAsync();
        await workspace.InstallReferenceAsync();
        var roles = JsonNode.Parse(ExampleText("roles.json"))!.AsArray();
        var grants = roles[0]!["grants"]!.AsArray();
        // Principal has the complete Requester role, token ceiling deliberately omits line update.
        var scopes = new JsonArray(grants.Where(grant => !(Text(grant!, "action") == "record.update" &&
            Text(grant!, "resource") == "MaterialRequestLine")).Select(grant => grant!.DeepClone()).ToArray());
        var service = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "ScopedRequester", grants, scopes);
        var created = await workspace.OperationAsync("CreateRequest", ExampleJson("CreateRequest.input.json"), service.Caller);
        var before = await workspace.BusinessSnapshotAsync();
        var line = created["lines"]![0]!;
        var denied = await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("RepriceRequestLine", RevisionInput(line), service.Caller));
        Assert.Equal(403, denied.Status);
        // Denial may occur at flow admission (403) or be retained as an accepted failed run.
        var outcome = await Capture(() => workspace.SubmitAsync(created["request"]!.AsObject(), caller: service.Caller));
        if (outcome.Error is not null) Assert.Equal(403, outcome.Error.Status);
        else
        {
            Assert.Equal("failed", Text(outcome.Result!, "status"));
            Assert.Equal("FORBIDDEN", Text(outcome.Result!["error"]!, "code"));
        }
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
    }

    [Fact, Trait("Gate", "A01"), Trait("Gate", "A04"), Trait("Gate", "A05"), Trait("Gate", "A13"), Trait("Gate", "A23")]
    public async Task Http_real_token_auth_strict_json_schema_and_idempotent_crud_use_the_application_pipeline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bos-http-conformance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var host = ApiHost.Build(directory, []);
            var factory = host.Services.GetRequiredService<IUnitOfWorkFactory>();
            await factory.InitializeAsync();
            var identity = host.Services.GetRequiredService<IIdentityService>();
            var api = host.Services.GetRequiredService<IApplicationApi>();
            var admin = await BootstrapAsync(factory, identity);
            var service = await CreateServiceAsync(api, identity, admin, "HttpService", AllGrants());
            host.Urls.Clear();
            host.Urls.Add("http://127.0.0.1:0");
            await host.StartAsync();
            try
            {
                var server = host.Services.GetRequiredService<IServer>();
                var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
                using var client = new HttpClient { BaseAddress = new Uri(address) };
                var unauthenticated = await Post(client, "identity.getCurrent", "{}", token: null);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
                var invalidToken = await Post(client, "identity.getCurrent", "{}", token: "bos_invalid.invalid");
                Assert.Equal(HttpStatusCode.Unauthorized, invalidToken.StatusCode);
                var draft = await HttpResult(client, "types.create", new JsonObject { ["definition"] = NoteDefinition() }, service.Token);
                await HttpResult(client, "types.publish", new JsonObject
                {
                    ["name"] = "Note", ["expectedRevision"] = Number(draft, "draftRevision")
                }, service.Token);
                var key = NewKey();
                var duplicate = await Post(client, "records.create", """{"type":"Note","type":"Note","data":{"title":"Bad"}}""", service.Token, key);
                Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
                AssertErrorShape(await Body(duplicate));
                var unknown = await Post(client, "records.create", """{"type":"Note","data":{"title":"Bad","unknown":true}}""", service.Token, key);
                Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
                AssertErrorShape(await Body(unknown));
                var args = Parse("""{"type":"Note","data":{"title":"Created via HTTP"}}""");
                var created = await HttpResult(client, "records.create", args, service.Token, key);
                var replay = await HttpResult(client, "records.create", args, service.Token, key);
                AssertJsonEqual(created, replay);
                args["data"]!["title"] = "Changed body";
                var conflict = await Post(client, "records.create", args.ToJsonString(), service.Token, key);
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                Assert.Equal("IDEMPOTENCY_CONFLICT", Text((await Body(conflict))["error"]!, "code"));
                var get = await HttpResult(client, "records.get", new JsonObject { ["type"] = "Note", ["id"] = Text(created, "id") }, service.Token);
                AssertJsonEqual(created, get);
                var updated = await HttpResult(client, "records.update", Update(created, "Updated via HTTP"), service.Token);
                Assert.Equal(2, Number(updated, "revision"));
                var stale = await Post(client, "records.update", Update(created, "Stale").ToJsonString(), service.Token);
                Assert.Equal("REVISION_CONFLICT", Text((await Body(stale))["error"]!, "code"));
                await HttpResult(client, "records.delete", Delete(updated), service.Token);
                var count = await HttpResult(client, "records.count", new JsonObject { ["type"] = "Note", ["filter"] = new JsonObject() }, service.Token);
                Assert.Equal("0", Text(count, "count"));
                foreach (var typeName in new[] { "MaterialRequest", "MaterialRequestLine" })
                {
                    var typeDraft = await HttpResult(client, "types.create", new JsonObject { ["definition"] = ExampleJson(typeName + ".json") }, service.Token);
                    await HttpResult(client, "types.publish", new JsonObject
                    {
                        ["name"] = typeName, ["expectedRevision"] = Number(typeDraft, "draftRevision")
                    }, service.Token);
                }
                var flowDraft = await HttpResult(client, "flows.create", new JsonObject
                {
                    ["definition"] = new JsonObject { ["source"] = ExampleText("SubmitMaterialRequest.yaml") }
                }, service.Token);
                await HttpResult(client, "flows.publish", new JsonObject
                {
                    ["name"] = "SubmitMaterialRequest", ["expectedRevision"] = Number(flowDraft, "draftRevision")
                }, service.Token);
                var requestResult = await HttpResult(client, "operations.call", new JsonObject
                {
                    ["name"] = "CreateRequest", ["contractVersion"] = 1, ["input"] = ExampleJson("CreateRequest.input.json")
                }, service.Token);
                var parent = requestResult["request"]!;
                var runArgs = new JsonObject
                {
                    ["name"] = "SubmitMaterialRequest", ["version"] = 1,
                    ["input"] = new JsonObject { ["requestId"] = Text(parent, "id"), ["revision"] = "2" }
                };
                var failedRun = await HttpResult(client, "flows.run", runArgs, service.Token);
                Assert.Equal("failed", Text(failedRun, "status"));
                Assert.Equal("REVISION_CONFLICT", Text(failedRun["error"]!, "code"));
                var failedSteps = await HttpResult(client, "flows.getRunSteps", new JsonObject
                {
                    ["runId"] = Text(failedRun, "runId")
                }, service.Token);
                Assert.Equal(2, failedSteps["items"]!.AsArray().Count(step =>
                    Text(step!, "stepId") == "priceLine" && Text(step!, "outcome") == "succeeded"));
                foreach (var line in requestResult["lines"]!.AsArray())
                {
                    var persisted = await HttpResult(client, "records.get", new JsonObject
                    {
                        ["type"] = "MaterialRequestLine", ["id"] = Text(line!, "id")
                    }, service.Token);
                    AssertJsonEqual(line!, persisted);
                }
                var unchangedParent = await HttpResult(client, "records.get", new JsonObject
                {
                    ["type"] = "MaterialRequest", ["id"] = Text(parent, "id")
                }, service.Token);
                AssertJsonEqual(parent, unchangedParent);
                runArgs["input"]!["revision"] = "1";
                var submitted = await HttpResult(client, "flows.run", runArgs, service.Token);
                Assert.Equal("succeeded", Text(submitted, "status"));
                Assert.Equal("900.00", Text(submitted["result"]!, "total"));
                var submittedParent = await HttpResult(client, "records.get", new JsonObject
                {
                    ["type"] = "MaterialRequest", ["id"] = Text(parent, "id")
                }, service.Token);
                var bypass = Delete(submittedParent);
                bypass["patch"] = new JsonObject { ["status"] = "Approved" };
                var guarded = await Post(client, "records.update", bypass.ToJsonString(), service.Token);
                Assert.Equal(HttpStatusCode.Forbidden, guarded.StatusCode);
                Assert.Equal("POLICY_VIOLATION", Text((await Body(guarded))["error"]!, "code"));
                // Role and token scope are real, and authorization rechecks this narrower token.
                var readerGrants = Parse("""{"grants":[{"action":"record.read","resource":"Note"}]}""")["grants"]!.AsArray();
                var reader = await CreateServiceAsync(api, identity, admin, "HttpReader", readerGrants);
                var denied = await Post(client, "records.create", """{"type":"Note","data":{"title":"Denied"}}""", reader.Token);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                AssertErrorShape(await Body(denied));
                var healthy = await client.GetAsync("/health/live");
                Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
                // Specification §17 assigns revision mismatch to HTTP 412.
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            }
            finally { await host.StopAsync(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact, Trait("Surface", "Catalogue")]
    public async Task Initial_http_method_inventory_capabilities_health_and_scalar_authorization_are_available()
    {
        await using var workspace = await CreateAsync();
        // Independent inventory from specification §§18–21. CLI-only backup/restore and pure
        // expression helpers have their own transports and must not become callable RPC methods.
        var expected = """
            types.create types.update types.validate types.publish types.get types.list types.listVersions types.getDraft
            records.create records.get records.update records.delete records.validate records.list records.count
            operations.list operations.get operations.validate operations.call
            flows.create flows.update flows.getDraft flows.validate flows.publish flows.get flows.list flows.listVersions
            flows.run flows.getRun flows.listRuns flows.getRunSteps flows.disable flows.enable
            identity.getCurrent authorization.can
            users.create users.get users.list users.update users.disable users.setPassword
            roles.create roles.update roles.get roles.list roles.assign roles.remove
            tokens.create tokens.list tokens.revoke audit.list system.getCapabilities system.getHealth
            """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
        var capabilities = await workspace.CallAsync("system.getCapabilities", new JsonObject());
        var methods = capabilities["methods"]!.AsArray();
        Assert.Equal(expected, methods.Select(method => Text(method!, "name")).Order(StringComparer.Ordinal));
        Assert.Equal(expected, workspace.Api.Methods.Where(method => method.Transport == "http").Select(method => method.Name).Order(StringComparer.Ordinal));
        foreach (var method in methods)
        {
            Assert.Equal("http", Text(method!, "transport"));
            Assert.IsType<JsonObject>(method!["inputSchema"]);
            Assert.IsType<JsonObject>(method["outputSchema"]);
            Assert.Equal(!method["mutating"]!.GetValue<bool>(), method["readOnly"]!.GetValue<bool>());
        }
        var health = await workspace.CallAsync("system.getHealth", new JsonObject());
        Assert.Equal("ready", Text(health, "status"));
        Assert.NotEmpty(health["checks"]!.AsArray());
        foreach (var metric in new[] { "activeReaders", "activeWriters", "databaseBytes", "walBytes" })
            Assert.True(long.Parse(health["metrics"]![metric]!.ToJsonString(), CultureInfo.InvariantCulture) >= 0);
        var service = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "CatalogueReader",
            GrantArray(("record.read", "Note")));
        foreach (var (action, resource, allowed) in new[]
        {
            ("record.read", "Note", true), ("record.update", "Note", false), ("record.read", "Other", false)
        })
        {
            var result = await workspace.Api.InvokeAsync("authorization.can", new JsonObject
            {
                ["action"] = action, ["resource"] = resource
            }, service.Caller, null);
            Assert.Equal(200, result.Status);
            Assert.Equal(allowed, result.Result!.GetValue<bool>());
        }
        var limited = await workspace.CallAsync("system.getCapabilities", new JsonObject(), service.Caller);
        Assert.DoesNotContain(limited["methods"]!.AsArray(), method => Text(method!, "name") == "users.create");
    }

    [Fact, Trait("Surface", "Catalogue")]
    public async Task Advisory_type_record_and_flow_validation_return_reports_without_persisting_work()
    {
        await using var workspace = await CreateAsync();
        var before = await IdentityState(workspace);
        var report = await workspace.CallAsync("types.validate", new JsonObject { ["definition"] = NoteDefinition() });
        AssertValidReport(report, true);
        var invalid = NoteDefinition();
        invalid["fields"]!["title"]!["kind"] = "notAKind";
        AssertValidReport(await workspace.CallAsync("types.validate", new JsonObject { ["definition"] = invalid }), false);
        Assert.Equal(before, await IdentityState(workspace));
        await workspace.PublishTypeAsync(NoteDefinition());
        var business = await workspace.BusinessSnapshotAsync();
        AssertValidReport(await workspace.CallAsync("records.validate", Parse("""{"type":"Note","typeVersion":1,"data":{"title":"Valid"}}""")), true);
        AssertValidReport(await workspace.CallAsync("records.validate", Parse("""{"type":"Note","data":{"title":true}}""")), false);
        AssertValidReport(await workspace.CallAsync("flows.validate", new JsonObject
        {
            ["definition"] = new JsonObject { ["source"] = CatalogueFlow("Advisory", "valid") }
        }), true);
        AssertValidReport(await workspace.CallAsync("flows.validate", new JsonObject
        {
            ["definition"] = new JsonObject { ["source"] = "format: bos.flow/1.0\nsteps: []\nunknown: true\n" }
        }), false);
        Assert.Equal(business, await workspace.BusinessSnapshotAsync());
        await using var read = await workspace.Factory.BeginReadAsync();
        Assert.Null(await read.Documents.GetAsync("flowDrafts", "Advisory"));
        Assert.Empty(await read.Documents.ListAsync("runs"));
    }

    [Fact, Trait("Surface", "Catalogue")]
    public async Task Operation_catalogues_page_filter_resolve_and_validate_real_descriptors_without_execution()
    {
        await using var workspace = await CreateAsync();
        var before = await workspace.BusinessSnapshotAsync();
        var operations = await CatalogueRows(workspace, "operations.list", new JsonObject(), new JsonObject { ["limit"] = 2 });
        var expected = new[]
        {
            "ApproveRequest", "CreateRequest", "FinalizeRequest", "RepriceRequestLine", "ReviseRequest",
            "records.count", "records.create", "records.delete", "records.get", "records.list", "records.update", "records.validate"
        }.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, operations.Select(operation => Text(operation, "name")));
        foreach (var descriptor in operations)
        {
            var fetched = await workspace.CallAsync("operations.get", new JsonObject
            {
                ["name"] = Text(descriptor, "name"), ["contractVersion"] = Number(descriptor, "contractVersion")
            });
            AssertJsonEqual(descriptor, fetched);
            Assert.True(fetched["flowCallable"]!.GetValue<bool>());
            Assert.IsType<JsonObject>(fetched["inputSchema"]);
            Assert.IsType<JsonObject>(fetched["outputSchema"]);
            Assert.IsType<JsonArray>(fetched["requiredGrants"]);
            Assert.IsType<JsonArray>(fetched["capabilities"]);
        }
        var filtered = await CatalogueRows(workspace, "operations.list", new JsonObject(), EqualQuery("name", "CreateRequest"));
        Assert.Equal("CreateRequest", Text(Assert.Single(filtered), "name"));
        AssertValidReport(await workspace.CallAsync("operations.validate", new JsonObject
        {
            ["name"] = "CreateRequest", ["contractVersion"] = 1, ["input"] = ExampleJson("CreateRequest.input.json")
        }), true);
        AssertValidReport(await workspace.CallAsync("operations.validate", Parse("""{"name":"CreateRequest","input":{"title":"Missing other required fields"}}""")), false);
        AssertValidReport(await workspace.CallAsync("operations.validate", Parse("""{"name":"records.get","input":{"type":"Note","id":"00000000-0000-4000-8000-000000000001"}}""")), true);
        var unavailable = await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("operations.get", Parse("""{"name":"CreateRequest","contractVersion":999}""")));
        Assert.Equal("DEPENDENCY_UNAVAILABLE", unavailable.Code);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
    }

    [Fact, Trait("Gate", "A04"), Trait("Surface", "Catalogue")]
    public async Task Operation_discovery_intersects_principal_grants_with_real_token_scopes()
    {
        await using var workspace = await CreateAsync();
        var requester = JsonNode.Parse(ExampleText("roles.json"))![0]!["grants"]!.AsArray();
        var scopes = new JsonArray(requester.Where(grant => !(Text(grant!, "action") == "record.update" &&
            Text(grant!, "resource") == "MaterialRequestLine")).Select(grant => grant!.DeepClone()).ToArray());
        var service = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "DiscoveryRequester", requester, scopes);
        var rows = await CatalogueRows(workspace, "operations.list", new JsonObject(), new JsonObject { ["limit"] = 1 }, service.Caller);
        var custom = rows.Where(row => !Text(row, "name").StartsWith("records.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "CreateRequest", "FinalizeRequest" }, custom.Select(row => Text(row, "name")));
        var denied = await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("operations.get", new JsonObject { ["name"] = "RepriceRequestLine" }, service.Caller));
        Assert.Equal(403, denied.Status);
        var first = await CataloguePage(workspace, "operations.list", new JsonObject(), new JsonObject { ["limit"] = 1 });
        Assert.NotNull(first["nextCursor"]);
        Assert.Equal(400, (await Assert.ThrowsAsync<BosException>(() => CataloguePage(workspace, "operations.list", new JsonObject(),
            new JsonObject { ["limit"] = 1, ["cursor"] = first["nextCursor"]!.DeepClone() }, service.Caller))).Status);
    }

    [Theory, InlineData("types"), InlineData("flows"), Trait("Gate", "A09"), Trait("Surface", "Catalogue")]
    public async Task Version_catalogue_cursors_bind_parent_method_and_principal_and_preserve_ascending_versions(string family)
    {
        await using var workspace = await CreateAsync();
        foreach (var name in new[] { "Alpha", "Beta" }) await PublishCatalogueVersions(workspace, family, name);
        var args = new JsonObject { ["name"] = "Alpha" };
        var rows = await CatalogueRows(workspace, family + ".listVersions", args, new JsonObject { ["limit"] = 1 });
        Assert.Equal(new[] { 1, 2 }, rows.Select(row => Number(row, "version")));
        Assert.All(rows, row =>
        {
            Assert.Equal("Alpha", Text(row, "name"));
            foreach (var field in new[] { "hash", "publishedAt" }) Assert.False(string.IsNullOrEmpty(Text(row, field)));
            Assert.False(row.ContainsKey("definition"));
            Assert.False(row.ContainsKey("source"));
            Assert.False(row.ContainsKey("ast"));
        });
        var first = await CataloguePage(workspace, family + ".listVersions", args, new JsonObject { ["limit"] = 1 });
        var query = new JsonObject { ["limit"] = 1, ["cursor"] = first["nextCursor"]!.DeepClone() };
        Assert.Equal(400, (await Assert.ThrowsAsync<BosException>(() => CataloguePage(workspace, family + ".listVersions",
            new JsonObject { ["name"] = "Beta" }, query))).Status);
        var service = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "VersionReader",
            GrantArray(("types.read", "*"), ("flows.read", "*")));
        Assert.Equal(400, (await Assert.ThrowsAsync<BosException>(() => CataloguePage(workspace, family + ".listVersions", args, query, service.Caller))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<BosException>(() => CataloguePage(workspace, family + ".listVersions", args, EqualQuery("name", "Alpha")))).Status);
        var otherFamily = family == "types" ? "flows" : "types";
        await PublishCatalogueVersions(workspace, otherFamily, "Alpha");
        Assert.Equal(400, (await Assert.ThrowsAsync<BosException>(() => CataloguePage(workspace, otherFamily + ".listVersions", args, query))).Status);
        var active = await workspace.CallAsync(family + ".get", args);
        Assert.Equal(2, Number(active, "version"));
        var summaries = await CatalogueRows(workspace, family + ".list", new JsonObject(), EqualQuery("name", "Alpha"));
        Assert.Equal(2, Number(Assert.Single(summaries), "activeVersion"));
    }

    [Fact, Trait("Surface", "Catalogue"), Trait("Gate", "A19")]
    public async Task Audit_catalogue_reads_actual_sql_rows_filters_before_paging_and_binds_cursor_filters()
    {
        await using var workspace = await CreateAsync();
        await workspace.PublishTypeAsync(NoteDefinition());
        var first = await workspace.CreateRecordAsync("Note", Parse("""{"title":"First audit target"}"""));
        await workspace.CreateRecordAsync("Note", Parse("""{"title":"Other target"}"""));
        var second = await workspace.CallAsync("records.update", Update(first, "Revised audit target"));
        var records = await workspace.CallAsync("records.list", new JsonObject
        {
            ["type"] = "Note", ["query"] = EqualQuery("title", "Revised audit target")
        });
        AssertJsonEqual(second, Assert.Single(records["items"]!.AsArray())!);
        Assert.Null(records["nextCursor"]);
        await workspace.CallAsync("records.delete", Delete(second));
        var query = EqualQuery("objectId", Text(first, "id"));
        query["limit"] = 1;
        var actual = await CatalogueRows(workspace, "audit.list", new JsonObject(), query);
        Assert.Equal(new[] { "records.create", "records.update", "records.delete" }, actual.Select(row => Text(row, "method")));
        Assert.All(actual, row => Assert.Equal(workspace.Admin.PrincipalId, Text(row, "actor")));
        await using (var read = await workspace.Factory.BeginReadAsync())
        {
            await using var command = read.Connection.CreateCommand();
            command.Transaction = read.Transaction;
            command.CommandText = "SELECT json FROM Audit WHERE json_extract(json,'$.objectId') = $id ORDER BY sequence";
            var parameter = command.CreateParameter(); parameter.ParameterName = "$id"; parameter.Value = Text(first, "id"); command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync();
            var expected = new List<JsonObject>();
            while (await reader.ReadAsync()) expected.Add(Parse(reader.GetString(0)));
            Assert.Equal(3, expected.Count);
            Assert.Equal(expected.Select(row => CanonicalJson.Serialize(row)), actual.Select(row => CanonicalJson.Serialize(row)));
        }
        var page = await CataloguePage(workspace, "audit.list", new JsonObject(), query);
        var changed = EqualQuery("method", "records.update"); changed["cursor"] = page["nextCursor"]!.DeepClone();
        Assert.Equal(400, (await Assert.ThrowsAsync<BosException>(() => CataloguePage(workspace, "audit.list", new JsonObject(), changed))).Status);
        Assert.Empty(await CatalogueRows(workspace, "audit.list", new JsonObject(), EqualQuery("objectId", "' OR 1=1 --")));
    }

    [Fact, Trait("Surface", "Catalogue"), Trait("Gate", "A16")]
    public async Task Flow_control_revisions_disable_admission_and_enable_without_changing_published_content()
    {
        await using var workspace = await CreateAsync();
        await workspace.PublishFlowAsync("Controlled", CatalogueFlow("Controlled", "result"));
        var before = await workspace.CallAsync("flows.get", new JsonObject { ["name"] = "Controlled" });
        var disabled = await workspace.CallAsync("flows.disable", new JsonObject
        {
            ["name"] = "Controlled", ["expectedRevision"] = Number(before, "controlRevision")
        });
        Assert.True(disabled["disabled"]!.GetValue<bool>());
        Assert.Equal(Number(before, "controlRevision") + 1, Number(disabled, "revision"));
        var projected = await workspace.CallAsync("flows.get", new JsonObject { ["name"] = "Controlled", ["version"] = 1 });
        Assert.True(projected["disabled"]!.GetValue<bool>());
        Assert.Equal(Text(before, "hash"), Text(projected, "hash"));
        Assert.Equal(Text(before, "sourceHash"), Text(projected, "sourceHash"));
        var summaries = await CatalogueRows(workspace, "flows.list", new JsonObject(), EqualQuery("disabled", true));
        Assert.Equal("Controlled", Text(Assert.Single(summaries), "name"));
        Assert.Equal(Number(disabled, "revision"), Number(summaries[0], "controlRevision"));
        var key = NewKey();
        var runArgs = RunArguments("Controlled");
        var denied = await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("flows.run", runArgs, key: key));
        Assert.Equal("FLOW_DISABLED", denied.Code);
        await using (var read = await workspace.Factory.BeginReadAsync())
        {
            Assert.Empty(await read.Documents.ListAsync("runs"));
            Assert.DoesNotContain(await read.Documents.ListAsync("receipts"), row => row["key"]?.GetValue<string>() == key);
        }
        Assert.Equal("REVISION_CONFLICT", (await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("flows.enable", new JsonObject
        {
            ["name"] = "Controlled", ["expectedRevision"] = Number(before, "controlRevision")
        }))).Code);
        var enabled = await workspace.CallAsync("flows.enable", new JsonObject { ["name"] = "Controlled", ["expectedRevision"] = Number(disabled, "revision") });
        Assert.False(enabled["disabled"]!.GetValue<bool>());
        Assert.Equal(Number(disabled, "revision") + 1, Number(enabled, "revision"));
        var succeeded = await workspace.CallAsync("flows.run", runArgs, key: key);
        Assert.Equal("succeeded", Text(succeeded, "status"));
        Assert.Equal("result", Text(succeeded["result"]!, "message"));
    }

    [Fact, Trait("Surface", "Catalogue")]
    public async Task Run_and_step_catalogues_page_filter_and_bind_continuations_to_the_run()
    {
        await using var workspace = await CreateAsync();
        await workspace.PublishFlowAsync("Steps", CatalogueFlow("Steps", "metadata"));
        var first = await workspace.CallAsync("flows.run", RunArguments("Steps"));
        var second = await workspace.CallAsync("flows.run", RunArguments("Steps"));
        var parent = new JsonObject { ["runId"] = Text(first, "runId") };
        var rows = await CatalogueRows(workspace, "flows.getRunSteps", parent, new JsonObject { ["limit"] = 1 });
        Assert.Equal(new[] { "bind", "check", "output" }, rows.Select(row => Text(row, "stepId")));
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(row => Number(row, "ordinal")));
        Assert.All(rows, row => Assert.Equal("succeeded", Text(row, "outcome")));
        var exact = await CatalogueRows(workspace, "flows.getRunSteps", parent, EqualQuery("stepId", "check"));
        Assert.Equal("check", Text(Assert.Single(exact), "stepId"));
        Assert.Empty(await CatalogueRows(workspace, "flows.getRunSteps", parent, EqualQuery("outcome", "failed")));
        var page = await CataloguePage(workspace, "flows.getRunSteps", parent, new JsonObject { ["limit"] = 1 });
        var continuation = new JsonObject { ["limit"] = 1, ["cursor"] = page["nextCursor"]!.DeepClone() };
        Assert.Equal(400, (await Assert.ThrowsAsync<BosException>(() => CataloguePage(workspace, "flows.getRunSteps",
            new JsonObject { ["runId"] = Text(second, "runId") }, continuation))).Status);
        var filterChange = EqualQuery("stepId", "output"); filterChange["cursor"] = page["nextCursor"]!.DeepClone();
        Assert.Equal(400, (await Assert.ThrowsAsync<BosException>(() => CataloguePage(workspace, "flows.getRunSteps", parent, filterChange))).Status);
        var runs = await CatalogueRows(workspace, "flows.listRuns", new JsonObject(), EqualQuery("flowName", "Steps"));
        Assert.Equal(2, runs.Count);
        Assert.Equal(new[] { Text(first, "runId"), Text(second, "runId") }.Order(), runs.Select(row => Text(row, "runId")).Order());
        Assert.All(runs, row => { Assert.False(row.ContainsKey("result")); Assert.False(row.ContainsKey("input")); });
    }

    [Fact, Trait("Surface", "Catalogue"), Trait("Gate", "A24")]
    public async Task Identity_user_role_and_token_catalogues_follow_real_activation_membership_and_revocation()
    {
        await using var workspace = await CreateAsync();
        var alpha = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "AlphaService", GrantArray(("record.read", "Note")));
        var beta = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "BetaService", GrantArray(("record.read", "Note")));
        var query = EqualQuery("kind", "service"); query["limit"] = 1;
        var users = await CatalogueRows(workspace, "users.list", new JsonObject(), query);
        Assert.Equal(new[] { "AlphaService", "BetaService" }, users.Select(row => Text(row, "userName")));
        Assert.All(users, row =>
        {
            Assert.True(row["active"]!.GetValue<bool>());
            foreach (var secret in new[] { "passwordHash", "securityStamp", "concurrencyStamp" }) Assert.False(row.ContainsKey(secret));
        });
        var user = await workspace.CallAsync("users.get", new JsonObject { ["id"] = alpha.Caller.PrincipalId });
        var role = await workspace.CallAsync("roles.create", new JsonObject
        {
            ["role"] = new JsonObject { ["name"] = "ExtraReader", ["grants"] = GrantArray(("types.read", "Note")) }
        });
        var assigned = await workspace.CallAsync("roles.assign", new JsonObject
        {
            ["userId"] = alpha.Caller.PrincipalId, ["roleId"] = Text(role, "id"), ["expectedUserRevision"] = Number(user, "revision")
        });
        Assert.Equal(Number(user, "revision") + 1, Number(assigned, "revision"));
        Assert.Contains(assigned["roles"]!.AsArray(), id => id!.GetValue<string>() == Text(role, "id"));
        role = await workspace.CallAsync("roles.update", new JsonObject
        {
            ["id"] = Text(role, "id"), ["expectedRevision"] = Number(role, "revision"),
            ["changes"] = new JsonObject { ["grants"] = GrantArray(("types.read", "Note"), ("record.read", "Note")) }
        });
        Assert.Equal(2, Number(role, "revision"));
        AssertJsonEqual(role, await workspace.CallAsync("roles.get", new JsonObject { ["id"] = Text(role, "id") }));
        var roles = await CatalogueRows(workspace, "roles.list", new JsonObject(), EqualQuery("name", "ExtraReader"));
        AssertJsonEqual(role, Assert.Single(roles));
        var removed = await workspace.CallAsync("roles.remove", new JsonObject
        {
            ["userId"] = alpha.Caller.PrincipalId, ["roleId"] = Text(role, "id"), ["expectedUserRevision"] = Number(assigned, "revision")
        });
        Assert.DoesNotContain(removed["roles"]!.AsArray(), id => id!.GetValue<string>() == Text(role, "id"));
        var token = Assert.Single(await CatalogueRows(workspace, "tokens.list", new JsonObject { ["principalId"] = alpha.Caller.PrincipalId }, EqualQuery("revoked", false)));
        Assert.Equal(alpha.Caller.CredentialId, Text(token, "id"));
        foreach (var secret in new[] { "token", "secret", "secretHash" }) Assert.False(token.ContainsKey(secret));
        var revoked = await workspace.CallAsync("tokens.revoke", new JsonObject { ["id"] = Text(token, "id") });
        Assert.NotNull(revoked["revokedAt"]);
        Assert.Null(await workspace.Identity.AuthenticateTokenAsync(alpha.Token));
        Assert.Single(await CatalogueRows(workspace, "tokens.list", new JsonObject { ["principalId"] = alpha.Caller.PrincipalId }, EqualQuery("revoked", true)));
        Assert.Empty(await CatalogueRows(workspace, "tokens.list", new JsonObject { ["principalId"] = alpha.Caller.PrincipalId }, EqualQuery("revoked", false)));
        var disabled = await workspace.CallAsync("users.disable", new JsonObject { ["id"] = beta.Caller.PrincipalId, ["expectedRevision"] = Number(users[1], "revision") });
        Assert.False(disabled["active"]!.GetValue<bool>());
        Assert.Null(await workspace.Identity.AuthenticateTokenAsync(beta.Token));
        var inactive = await CatalogueRows(workspace, "users.list", new JsonObject(), EqualQuery("active", false));
        Assert.Equal(beta.Caller.PrincipalId, Text(Assert.Single(inactive), "id"));
    }

    [Theory, InlineData("users.list"), InlineData("roles.list"), InlineData("tokens.list"), Trait("Surface", "Catalogue"), Trait("Gate", "A09")]
    public async Task Identity_catalogue_cursors_reject_changed_caller_filter_and_token_parent(string method)
    {
        await using var workspace = await CreateAsync();
        var scopes = GrantArray(("identity.manage", "*"));
        var alpha = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "CursorAlpha", scopes);
        var beta = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "CursorBeta", scopes);
        await workspace.CallAsync("tokens.create", new JsonObject { ["principalId"] = alpha.Caller.PrincipalId, ["scopes"] = scopes.DeepClone() });
        var parent = method == "tokens.list" ? new JsonObject { ["principalId"] = alpha.Caller.PrincipalId } : new JsonObject();
        var first = await CataloguePage(workspace, method, parent, new JsonObject { ["limit"] = 1 });
        Assert.NotNull(first["nextCursor"]);
        var query = new JsonObject { ["limit"] = 1, ["cursor"] = first["nextCursor"]!.DeepClone() };
        var next = await CataloguePage(workspace, method, parent, query);
        Assert.NotEqual(Text(first["items"]![0]!, "id"), Text(next["items"]![0]!, "id"));
        var rejections = new List<BosException>
        {
            await Assert.ThrowsAsync<BosException>(() => CataloguePage(workspace, method, parent, query, beta.Caller))
        };
        var changed = method switch
        {
            "users.list" => EqualQuery("kind", "service"),
            "roles.list" => EqualQuery("name", "CursorAlphaRole"),
            _ => EqualQuery("revoked", false)
        };
        changed["cursor"] = first["nextCursor"]!.DeepClone();
        rejections.Add(await Assert.ThrowsAsync<BosException>(() => CataloguePage(workspace, method, parent, changed)));
        if (method == "tokens.list")
            rejections.Add(await Assert.ThrowsAsync<BosException>(() => CataloguePage(workspace, method,
                new JsonObject { ["principalId"] = beta.Caller.PrincipalId }, query)));
        Assert.All(rejections, rejection =>
        {
            Assert.Equal("cursor", rejection.Path);
            Assert.Equal(400, rejection.Status);
        });
    }

    [Theory, InlineData("users.list"), InlineData("roles.list"), InlineData("tokens.list"), InlineData("audit.list"), Trait("Gate", "A04"), Trait("Surface", "Catalogue")]
    public async Task Administrative_catalogues_deny_a_legitimate_record_reader(string method)
    {
        await using var workspace = await CreateAsync();
        var service = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "RestrictedCatalogue", GrantArray(("record.read", "Note")));
        var args = method == "tokens.list" ? new JsonObject { ["principalId"] = service.Caller.PrincipalId } : new JsonObject();
        var denied = await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync(method, args, service.Caller));
        Assert.Equal("FORBIDDEN", denied.Code);
        Assert.Equal(403, denied.Status);
    }

    [Theory, InlineData("createdAt"), InlineData("expiresAt"), InlineData("revokedAt"), InlineData("audit"), Trait("Surface", "Catalogue"), Trait("Gate", "A02")]
    public async Task Token_metadata_and_identity_audit_instants_use_the_canonical_millisecond_wire_format(string field)
    {
        await using var workspace = await CreateAsync();
        var service = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "CanonicalTokenTime", GrantArray(("record.read", "Note")));
        var token = Assert.Single(await CatalogueRows(workspace, "tokens.list", new JsonObject { ["principalId"] = service.Caller.PrincipalId }, new JsonObject()));
        const string canonicalInstant = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$";
        var revoked = await workspace.CallAsync("tokens.revoke", new JsonObject { ["id"] = Text(token, "id") });
        var audit = await CatalogueRows(workspace, "audit.list", new JsonObject(), EqualQuery("method", "tokens.revoke"));
        var entry = Assert.Single(audit);
        var instant = field switch
        {
            "audit" => (entry["timestamp"] ?? entry["at"])!.GetValue<string>(),
            "revokedAt" => Text(revoked, "revokedAt"),
            _ => Text(token, field)
        };
        Assert.Matches(canonicalInstant, instant);
    }

    [Fact, Trait("Gate", "A13")]
    public async Task Direct_command_replay_denies_a_real_service_principal_after_record_read_permission_is_removed()
    {
        await using var workspace = await CreateAsync();
        await workspace.PublishTypeAsync(NoteDefinition());
        var service = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "ReplayWriter",
            GrantArray(("record.read", "Note"), ("record.create", "Note")));
        var args = Parse("""{"type":"Note","data":{"title":"Permission-gated receipt"}}""");
        var key = NewKey();
        var created = await workspace.CallAsync("records.create", args, service.Caller, key);
        AssertJsonEqual(created, await workspace.CallAsync("records.create", args, service.Caller, key));
        var user = await workspace.CallAsync("users.get", new JsonObject { ["id"] = service.Caller.PrincipalId });
        var roleId = Assert.Single(user["roles"]!.AsArray())!.GetValue<string>();
        var role = await workspace.CallAsync("roles.get", new JsonObject { ["id"] = roleId });
        await workspace.CallAsync("roles.update", new JsonObject
        {
            ["id"] = roleId, ["expectedRevision"] = Number(role, "revision"),
            ["changes"] = new JsonObject { ["grants"] = GrantArray(("record.create", "Note")) }
        });
        Assert.NotNull(await workspace.Identity.AuthenticateTokenAsync(service.Token));
        var before = await workspace.BusinessSnapshotAsync();
        var denied = await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("records.create", args, service.Caller, key));
        Assert.Equal("FORBIDDEN", denied.Code);
        Assert.Equal(403, denied.Status);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
    }

    [Fact, Trait("Gate", "A13"), Trait("Gate", "A19")]
    public async Task Administrative_password_receipt_excludes_secret_and_different_secret_replay_cannot_reset_password()
    {
        await using var workspace = await CreateAsync();
        const string originalSecret = "Original-Cafe\u0301-Password-2026!";
        const string replaySecret = "Different-Replay-Password-2026!";
        var user = await workspace.CallAsync("users.create", Parse("""{"user":{"userName":"PasswordUser","displayName":"Password test","kind":"human"}}"""));
        var args = new JsonObject { ["id"] = Text(user, "id"), ["expectedRevision"] = Number(user, "revision"), ["newPassword"] = originalSecret };
        var key = NewKey();
        var changed = await workspace.CallAsync("users.setPassword", args, key: key);
        Assert.True(changed["passwordChanged"]!.GetValue<bool>());
        user = await workspace.CallAsync("users.update", new JsonObject
        {
            ["id"] = Text(user, "id"), ["expectedRevision"] = Number(changed, "revision"), ["changes"] = new JsonObject { ["active"] = true }
        });
        var login = await workspace.Identity.LoginAsync("PasswordUser", originalSecret);
        Assert.Equal(Text(user, "id"), login.Caller.PrincipalId);
        var before = await IdentityDocument(workspace, Text(user, "id"));
        args["newPassword"] = replaySecret;
        var replay = await workspace.CallAsync("users.setPassword", args, key: key);
        AssertJsonEqual(changed, replay);
        AssertJsonEqual(before, await IdentityDocument(workspace, Text(user, "id")));
        Assert.Equal(401, (await Assert.ThrowsAsync<BosException>(() => workspace.Identity.LoginAsync("PasswordUser", replaySecret))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<BosException>(() => workspace.Identity.LoginAsync("PasswordUser", originalSecret.Normalize(NormalizationForm.FormC)))).Status);
        Assert.Equal(Text(user, "id"), (await workspace.Identity.LoginAsync("PasswordUser", originalSecret)).Caller.PrincipalId);
        await AssertNoDurableSecrets(workspace, originalSecret, replaySecret, originalSecret.Normalize(NormalizationForm.FormC));
        await using var read = await workspace.Factory.BeginReadAsync();
        var receipt = Assert.Single(await read.Documents.ListAsync("receipts"), item => item["key"]?.GetValue<string>() == key);
        Assert.Equal(Text(changed, "userId"), Text(receipt["result"]!, "userId"));
        Assert.False(receipt["result"]!.AsObject().ContainsKey("newPassword"));
    }

    [Fact, Trait("Gate", "A13"), Trait("Gate", "A19")]
    public async Task Token_creation_replay_discloses_null_and_preserves_one_durable_issuance()
    {
        await using var workspace = await CreateAsync();
        var grants = GrantArray(("record.read", "Note"));
        var service = await CreateServiceAsync(workspace.Api, workspace.Identity, workspace.Admin, "TokenReplay", grants);
        var args = new JsonObject { ["principalId"] = service.Caller.PrincipalId, ["scopes"] = grants.DeepClone() };
        var key = NewKey();
        var issued = await workspace.CallAsync("tokens.create", args, key: key);
        var token = Text(issued, "token");
        Assert.NotNull(await workspace.Identity.AuthenticateTokenAsync(token));
        var before = await IdentityState(workspace);
        var replay = await workspace.CallAsync("tokens.create", args, key: key);
        Assert.True(replay.ContainsKey("token"));
        Assert.Null(replay["token"]);
        Assert.Equal("SECRET_ALREADY_DISCLOSED", Text(replay, "replayCode"));
        AssertJsonEqual(issued["metadata"]!, replay["metadata"]!);
        Assert.Equal(before, await IdentityState(workspace));
        var tokens = await CatalogueRows(workspace, "tokens.list", new JsonObject { ["principalId"] = service.Caller.PrincipalId }, new JsonObject { ["limit"] = 1 });
        Assert.Equal(2, tokens.Count); // fixture credential plus this single issuance
        Assert.Single(tokens, metadata => Text(metadata, "id") == Text(issued["metadata"]!, "id"));
        await AssertNoDurableSecrets(workspace, token, token[(token.IndexOf('.') + 1)..]);
    }

    [Fact, Trait("Gate", "A03"), Trait("Gate", "A17")]
    public async Task Exact_decomposed_crlf_yaml_survives_draft_publication_get_and_replay_with_byte_exact_source_hash()
    {
        await using var workspace = await CreateAsync();
        var source = "# Cafe\u0301 source bytes\r\n" + CatalogueFlow("ExactSource", "Cafe\u0301").Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
        Assert.False(source.IsNormalized(NormalizationForm.FormC));
        var args = new JsonObject { ["definition"] = new JsonObject { ["source"] = source } };
        var key = NewKey();
        var draft = await workspace.CallAsync("flows.create", args, key: key);
        Assert.Equal(source, Text(draft, "source"));
        Assert.Equal(source, Text(await workspace.CallAsync("flows.create", args, key: key), "source"));
        Assert.Equal(source, Text(await workspace.CallAsync("flows.getDraft", new JsonObject { ["name"] = "ExactSource" }), "source"));
        var published = await workspace.CallAsync("flows.publish", new JsonObject { ["name"] = "ExactSource", ["expectedRevision"] = Number(draft, "draftRevision") });
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        Assert.Equal(expectedHash, Text(published, "sourceHash"));
        Assert.Equal(source, Text(published, "source"));
        var fetched = await workspace.CallAsync("flows.get", new JsonObject { ["name"] = "ExactSource", ["version"] = 1 });
        Assert.Equal(source, Text(fetched, "source"));
        Assert.Equal(expectedHash, Text(fetched, "sourceHash"));
        var changedSource = source + "# layout-only change\r\n";
        draft = await workspace.CallAsync("flows.getDraft", new JsonObject { ["name"] = "ExactSource" });
        draft = await workspace.CallAsync("flows.update", new JsonObject
        {
            ["name"] = "ExactSource", ["expectedRevision"] = Number(draft, "draftRevision"),
            ["definition"] = new JsonObject { ["source"] = changedSource }
        });
        Assert.Equal(changedSource, Text(draft, "source"));
        Assert.Equal("NO_CHANGE", (await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("flows.publish", new JsonObject
        {
            ["name"] = "ExactSource", ["expectedRevision"] = Number(draft, "draftRevision")
        }))).Code);
        Assert.Equal(expectedHash, Text(await workspace.CallAsync("flows.get", new JsonObject { ["name"] = "ExactSource" }), "sourceHash"));
        var definition = NoteDefinition(); definition["description"] = "Cafe\u0301 canonical type";
        var typeDraft = await workspace.CallAsync("types.create", new JsonObject { ["definition"] = definition });
        Assert.Equal("Caf\u00e9 canonical type", Text(typeDraft["definition"]!, "description"));
        var type = await workspace.CallAsync("types.publish", new JsonObject { ["name"] = "Note", ["expectedRevision"] = Number(typeDraft, "draftRevision") });
        Assert.Equal(CanonicalJson.Hash(typeDraft["definition"]), Text(type, "hash"));
        Assert.Equal("Caf\u00e9 canonical type", Text(type["definition"]!, "description"));
        Assert.Equal("Caf\u00e9 canonical type", Text((await workspace.CallAsync("types.getDraft", new JsonObject { ["name"] = "Note" }))["definition"]!, "description"));
        Assert.Equal(Text(type, "hash"), Text(await workspace.CallAsync("types.get", new JsonObject { ["name"] = "Note" }), "hash"));
        var run = await workspace.CallAsync("flows.run", RunArguments("ExactSource"));
        Assert.Equal("succeeded", Text(run, "status"));
        Assert.Equal("Caf\u00e9", Text(run["result"]!, "message"));
    }

    private static JsonArray GrantArray(params (string Action, string Resource)[] grants) => new(grants.Select(grant => (JsonNode)new JsonObject
    {
        ["action"] = grant.Action, ["resource"] = grant.Resource
    }).ToArray());

    private static JsonObject EqualQuery(string field, JsonNode? value) => new()
    {
        ["where"] = new JsonArray(new JsonObject { ["field"] = field, ["op"] = "eq", ["value"] = value?.DeepClone() })
    };

    private static Task<JsonObject> CataloguePage(TestWorkspace workspace, string method, JsonObject parent, JsonObject query, Caller? caller = null)
    {
        var args = (JsonObject)parent.DeepClone(); args["query"] = query.DeepClone();
        return workspace.CallAsync(method, args, caller);
    }

    private static async Task<List<JsonObject>> CatalogueRows(TestWorkspace workspace, string method, JsonObject parent, JsonObject query, Caller? caller = null)
    {
        var rows = new List<JsonObject>();
        var next = (JsonObject)query.DeepClone();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var page = await CataloguePage(workspace, method, parent, next, caller);
            var items = Assert.IsType<JsonArray>(page["items"]);
            Assert.True(items.Count <= (next["limit"]?.GetValue<int>() ?? 50));
            rows.AddRange(items.Select(item => Assert.IsType<JsonObject>(item)));
            Assert.True(page.ContainsKey("nextCursor"));
            if (page["nextCursor"] is null) return rows;
            var cursor = Text(page, "nextCursor");
            Assert.True(cursors.Add(cursor), "Catalogue repeated a continuation cursor.");
            next["cursor"] = cursor;
        }
        Assert.Fail("Catalogue did not terminate within the fixture's bounded page count.");
        return rows;
    }

    private static void AssertValidReport(JsonObject report, bool valid)
    {
        Assert.Equal(valid, report["valid"]!.GetValue<bool>());
        var errors = Assert.IsType<JsonArray>(report["errors"]);
        if (valid) Assert.Empty(errors); else Assert.NotEmpty(errors);
    }

    private static JsonObject RunArguments(string name) => new() { ["name"] = name, ["version"] = 1, ["input"] = new JsonObject() };

    private static string CatalogueFlow(string name, string message) => $$$$"""
        format: bos.flow/1.0
        name: {{{{name}}}}
        mode: command
        transaction: atomic
        inputSchema: {fields: {}}
        outputSchema: {fields: {message: {kind: string, required: true}}}
        dependencies: {types: {}, operations: {}}
        capabilities: []
        steps:
          - id: bind
            let: greeting
            value: {literal: "{{{{message}}}}"}
          - id: check
            require: {literal: true}
            code: TEST_CHECK
            message: Expected true
          - id: output
            return: {object: {message: {path: greeting}}}
        """;

    private static async Task PublishCatalogueVersions(TestWorkspace workspace, string family, string name)
    {
        if (family == "types")
        {
            var definition = NoteDefinition(); definition["name"] = name;
            await workspace.PublishTypeAsync(definition);
            definition["fields"]!["extra"] = new JsonObject { ["kind"] = "string" };
            var draft = await workspace.CallAsync("types.getDraft", new JsonObject { ["name"] = name });
            draft = await workspace.CallAsync("types.update", new JsonObject { ["name"] = name, ["expectedRevision"] = Number(draft, "draftRevision"), ["definition"] = definition });
            await workspace.CallAsync("types.publish", new JsonObject { ["name"] = name, ["expectedRevision"] = Number(draft, "draftRevision") });
        }
        else
        {
            await workspace.PublishFlowAsync(name, CatalogueFlow(name, "one"));
            var draft = await workspace.CallAsync("flows.getDraft", new JsonObject { ["name"] = name });
            draft = await workspace.CallAsync("flows.update", new JsonObject
            {
                ["name"] = name, ["expectedRevision"] = Number(draft, "draftRevision"), ["definition"] = new JsonObject { ["source"] = CatalogueFlow(name, "two") }
            });
            await workspace.CallAsync("flows.publish", new JsonObject { ["name"] = name, ["expectedRevision"] = Number(draft, "draftRevision") });
        }
    }

    private static async Task<JsonObject> IdentityDocument(TestWorkspace workspace, string id)
    {
        await using var read = await workspace.Factory.BeginReadAsync();
        return (await read.Documents.GetAsync("users", id))!;
    }

    private static async Task AssertNoDurableSecrets(TestWorkspace workspace, params string[] secrets)
    {
        await using var read = await workspace.Factory.BeginReadAsync();
        await using var command = read.Connection.CreateCommand();
        command.Transaction = read.Transaction;
        command.CommandText = "SELECT json FROM Documents UNION ALL SELECT json FROM Audit UNION ALL SELECT json FROM RecordHistory";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var json = Parse(reader.GetString(0));
            void Inspect(JsonNode? node)
            {
                if (node is JsonObject obj) foreach (var pair in obj) Inspect(pair.Value);
                else if (node is JsonArray array) foreach (var item in array) Inspect(item);
                else if (node is JsonValue value && value.TryGetValue<string>(out var text))
                    foreach (var secret in secrets) Assert.False(text.Contains(secret, StringComparison.Ordinal), "Plaintext credential persisted in a database document.");
            }
            Inspect(json);
        }
    }

    private static JsonObject NoteDefinition() => Parse("""
        {"name":"Note","description":"Independent conformance fixture","accessMode":"direct","allowedOperations":[],
         "fields":{"title":{"kind":"string","required":true,"maxLength":200,"indexed":true}}}
        """);

    private static JsonObject ReferenceDefinition() => Parse("""
        {"name":"ReferenceHolder","description":"Nested references and unique rollback fixture","accessMode":"direct","allowedOperations":[],
         "fields":{"label":{"kind":"string","required":true,"indexed":true,"unique":true},
          "nested":{"kind":"object","required":true,"fields":{"links":{"kind":"array","required":true,"maxItems":4,
           "items":{"kind":"object","fields":{"target":{"kind":"reference","targetType":"Note","required":true}}}}}}}}
        """);

    private static JsonObject HolderData(string target, string label = "Holder") => new()
    {
        ["label"] = label,
        ["nested"] = new JsonObject { ["links"] = new JsonArray(new JsonObject { ["target"] = target }) }
    };

    private static JsonObject RevisionInput(JsonNode record) => new()
    {
        ["id"] = Text(record, "id"), ["expectedRevision"] = Number(record, "revision")
    };

    private static JsonObject Delete(JsonNode record)
    {
        var input = RevisionInput(record);
        input["type"] = Text(record, "type");
        return input;
    }

    private static JsonObject Update(JsonNode record, string title)
    {
        var input = Delete(record);
        input["patch"] = new JsonObject { ["title"] = title };
        return input;
    }

    private static async Task<string> DefinitionState(TestWorkspace workspace)
    {
        await using var uow = await workspace.Factory.BeginReadAsync();
        var state = new JsonObject();
        foreach (var collection in new[] { "typeDrafts", "typeVersions", "typeActive" })
            state[collection] = new JsonArray((await uow.Documents.ListAsync(collection)).Select(item => item.DeepClone()).ToArray());
        return state.ToJsonString();
    }

    private static async Task<string> IdentityState(TestWorkspace workspace)
    {
        await using var uow = await workspace.Factory.BeginReadAsync();
        var state = new JsonObject();
        foreach (var collection in new[] { "users", "roles", "tokens", "receipts" })
            state[collection] = new JsonArray((await uow.Documents.ListAsync(collection)).Select(item => item.DeepClone()).ToArray());
        await using var command = uow.Connection.CreateCommand();
        command.Transaction = uow.Transaction;
        command.CommandText = "SELECT json FROM Audit ORDER BY sequence";
        await using var reader = await command.ExecuteReaderAsync();
        var audit = new JsonArray();
        while (await reader.ReadAsync()) audit.Add(JsonNode.Parse(reader.GetString(0)));
        state["audit"] = audit;
        return CanonicalJson.Serialize(state);
    }

    private static void AssertJsonEqual(JsonNode expected, JsonNode actual) =>
        Assert.Equal(CanonicalJson.Serialize(expected), CanonicalJson.Serialize(actual));

    private sealed record Outcome(JsonObject? Result, BosException? Error);
    private static async Task<Outcome> Capture(Func<Task<JsonObject>> call)
    {
        try { return new Outcome(await call(), null); }
        catch (BosException error) { return new Outcome(null, error); }
    }

    private static async Task<Outcome[]> Race(params Func<Task<JsonObject>>[] calls)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ready = new CountdownEvent(calls.Length);
        var tasks = calls.Select(call => Task.Run(async () =>
        {
            ready.Signal();
            await start.Task;
            return await Capture(call);
        })).ToArray();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "Competing callers did not reach the starting barrier.");
        start.SetResult();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string method, string json, string? token, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/methods/" + method);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key ?? NewKey());
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<JsonObject> Body(HttpResponseMessage response) => Parse(await response.Content.ReadAsStringAsync());
    private static async Task<JsonObject> HttpResult(HttpClient client, string method, JsonObject args, string token, string? key = null)
    {
        using var response = await Post(client, method, args.ToJsonString(), token, key);
        var body = await Body(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method}: {(int)response.StatusCode} {body}");
        Assert.False(string.IsNullOrWhiteSpace(Text(body["meta"]!, "traceId")));
        return Assert.IsType<JsonObject>(body["result"]);
    }

    private static void AssertErrorShape(JsonObject body)
    {
        var error = Assert.IsType<JsonObject>(body["error"]);
        foreach (var field in new[] { "code", "message", "details", "traceId", "retryable" }) Assert.True(error.ContainsKey(field), field);
        Assert.IsType<JsonArray>(error["details"]);
        Assert.False(string.IsNullOrWhiteSpace(Text(error, "traceId")));
    }

    private abstract class FixtureOperation(string name, JsonObject inputSchema, JsonObject outputSchema, Grant[] capabilities) : IOperation
    {
        public OperationDescriptor Descriptor { get; } = new(name, 1, "Trusted test-only transaction probe", inputSchema,
            outputSchema, "write", true, [new Grant("operations.execute", name)], capabilities);
        public abstract Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input,
            IDispatcher dispatcher, CancellationToken ct);
    }

    private sealed class AtomicFailureOperation(List<string> staged) : FixtureOperation("TestAtomicFailure",
        Parse("""{"fields":{"first":{"kind":"string","required":true},"second":{"kind":"string","required":true},"target":{"kind":"string","required":true}}}"""),
        Parse("""{"fields":{}}"""), [new("record.read", "ReferenceHolder"), new("record.update", "ReferenceHolder"), new("record.read", "Note")])
    {
        public override async Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct)
        {
            foreach (var field in new[] { "first", "second" })
            {
                var get = new JsonObject { ["type"] = "ReferenceHolder", ["id"] = Text(input, field) };
                var record = (await dispatcher.CallAsync("records.get", 1, get, context, uow, ct))!;
                var args = Delete(record);
                args["patch"] = HolderData(Text(input, "target"), "Staged" + field);
                var updated = (await dispatcher.CallAsync("records.update", 1, args, context, uow, ct))!;
                Assert.Equal(2, Number(updated, "revision"));
                Assert.Equal("Staged" + field, Text(updated["data"]!, "label"));
                Assert.Equal(Text(input, "target"), updated["data"]!["nested"]!["links"]![0]!["target"]!.GetValue<string>());
                var reread = await dispatcher.CallAsync("records.get", 1, get, context, uow, ct);
                AssertJsonEqual(updated, reread!);
                staged.Add(Text(updated, "id"));
            }
            throw new BosException("TEST_AFTER_TWO_WRITES", "Test failure after two staged mutations.");
        }
    }

    private sealed class ConsumeStockOperation() : FixtureOperation("TestConsumeStock",
        Parse("""{"fields":{"id":{"kind":"string","required":true}}}"""),
        Parse("""{"fields":{"remaining":{"kind":"integer","required":true}}}"""),
        [new("record.read", "Stock"), new("record.update", "Stock")])
    {
        public override async Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct)
        {
            var record = (await dispatcher.CallAsync("records.get", 1, new JsonObject { ["type"] = "Stock", ["id"] = Text(input, "id") }, context, uow, ct))!;
            var remaining = long.Parse(Text(record["data"]!, "remaining"), CultureInfo.InvariantCulture);
            if (remaining < 1) throw new BosException("OUT_OF_STOCK", "No stock remains.");
            var update = Delete(record);
            update["patch"] = new JsonObject { ["remaining"] = (remaining - 1).ToString(CultureInfo.InvariantCulture) };
            await dispatcher.CallAsync("records.update", 1, update, context, uow, ct);
            return new JsonObject { ["remaining"] = (remaining - 1).ToString(CultureInfo.InvariantCulture) };
        }
    }

    private sealed class InvalidOutputOperation() : FixtureOperation("TestInvalidOutput",
        Parse("""{"fields":{"id":{"kind":"string","required":true}}}"""),
        Parse("""{"fields":{"ok":{"kind":"boolean","required":true}}}"""),
        [new("record.read", "Note"), new("record.update", "Note")])
    {
        public bool StagedWrite { get; private set; }

        public override async Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct)
        {
            var record = (await dispatcher.CallAsync("records.get", 1, new JsonObject { ["type"] = "Note", ["id"] = Text(input, "id") }, context, uow, ct))!;
            var updated = (await dispatcher.CallAsync("records.update", 1, Update(record, "Must roll back"), context, uow, ct))!;
            Assert.Equal(2, Number(updated, "revision"));
            StagedWrite = true;
            return new JsonObject { ["ok"] = "not a boolean" };
        }
    }

    private const string AtomicFailureFlow = """
        format: bos.flow/1.0
        name: AtomicFailure
        mode: command
        transaction: atomic
        inputSchema:
          fields:
            first: {kind: string, required: true}
            second: {kind: string, required: true}
            target: {kind: string, required: true}
        outputSchema: {fields: {}}
        dependencies:
          types: {ReferenceHolder: [1], Note: [1]}
          operations: {TestAtomicFailure: 1}
        capabilities:
          - operations.execute:TestAtomicFailure
          - record.read:ReferenceHolder
          - record.update:ReferenceHolder
          - record.read:Note
        steps:
          - id: failAfterWrites
            call: TestAtomicFailure
            args:
              first: {path: input.first}
              second: {path: input.second}
              target: {path: input.target}
          - id: output
            return: {object: {}}
        """;
}
