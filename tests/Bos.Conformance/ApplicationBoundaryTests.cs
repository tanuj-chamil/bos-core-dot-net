using System.Text.Json.Nodes;
using Bos.Contracts;
using Xunit;

namespace Bos.Conformance;

public sealed class ApplicationBoundaryTests
{
    [Theory]
    [InlineData("{\"field\":42,\"op\":\"eq\",\"value\":\"x\"}")]
    [InlineData("{\"field\":{},\"op\":\"eq\",\"value\":\"x\"}")]
    [InlineData("{\"field\":\"principalId\",\"op\":\"eq\",\"value\":42}")]
    public async Task Run_catalogue_rejects_malformed_filters_as_request_errors(string filter)
    {
        await using var w = await TestWorkspace.CreateAsync();
        var error = await Assert.ThrowsAsync<BosException>(() => w.Api.InvokeAsync("flows.listRuns", new JsonObject
        {
            ["query"] = new JsonObject { ["where"] = new JsonArray(JsonNode.Parse(filter)) }
        }, w.Admin, null));
        Assert.Equal("INVALID_REQUEST", error.Code);
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task Discovery_applies_combined_record_grants_and_exposes_builtin_operations_without_custom_execute_grants()
    {
        await using var w = await TestWorkspace.CreateAsync();
        var service = await TestWorkspace.CreateServiceAsync(w.Api, w.Identity, w.Admin, "RecordReader", new JsonArray(
            new JsonObject { ["action"] = "record.read", ["resource"] = "Note" },
            new JsonObject { ["action"] = "record.create", ["resource"] = "Unreadable" }));
        var capabilities = await w.CallAsync("system.getCapabilities", new JsonObject(), service.Caller);
        var names = capabilities["methods"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("types.get", names);
        Assert.Contains("operations.list", names);
        Assert.Contains("records.get", names);
        Assert.DoesNotContain("records.create", names);
        Assert.DoesNotContain("records.validate", names);
        var operations = await w.CallAsync("operations.list", new JsonObject(), service.Caller);
        var executable = operations["items"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("records.get", executable);
        Assert.DoesNotContain("records.create", executable);
        Assert.DoesNotContain("records.validate", executable);
    }

    [Fact]
    public async Task Flow_create_receipts_are_scoped_to_the_flow_name_and_recheck_exact_management_rights()
    {
        await using var w = await TestWorkspace.CreateAsync();
        var grants = new JsonArray(
            new JsonObject { ["action"] = "flows.manage", ["resource"] = "First" },
            new JsonObject { ["action"] = "flows.manage", ["resource"] = "Second" });
        var service = await TestWorkspace.CreateServiceAsync(w.Api, w.Identity, w.Admin, "FlowEditor", grants);
        var key = TestWorkspace.NewKey();
        JsonObject Args(string name) => new() { ["definition"] = new JsonObject { ["source"] = Source(name) } };
        var first = await w.CallAsync("flows.create", Args("First"), service.Caller, key);
        var second = await w.CallAsync("flows.create", Args("Second"), service.Caller, key);
        Assert.Equal("First", first["name"]!.GetValue<string>());
        Assert.Equal("Second", second["name"]!.GetValue<string>());
        var rolePage = await w.CallAsync("roles.list", new JsonObject { ["query"] = new JsonObject { ["where"] = new JsonArray(new JsonObject { ["field"] = "name", ["op"] = "eq", ["value"] = "FlowEditorRole" }) } });
        var role = rolePage["items"]![0]!;
        await w.CallAsync("roles.update", new JsonObject
        {
            ["id"] = role["id"]!.DeepClone(), ["expectedRevision"] = role["revision"]!.DeepClone(),
            ["changes"] = new JsonObject { ["grants"] = new JsonArray(new JsonObject { ["action"] = "flows.manage", ["resource"] = "Second" }) }
        });
        var error = await Assert.ThrowsAsync<BosException>(() => w.Api.InvokeAsync("flows.create", Args("First"), service.Caller, key));
        Assert.Equal("FORBIDDEN", error.Code);
        var allowedReplay = await w.CallAsync("flows.create", Args("Second"), service.Caller, key);
        Assert.True(JsonNode.DeepEquals(second, allowedReplay));
    }

    private static string Source(string name) => $$$$"""
        format: bos.flow/1.0
        name: {{{{name}}}}
        mode: query
        transaction: readOnly
        inputSchema: {fields: {}}
        outputSchema: {fields: {value: {kind: string, required: true}}}
        dependencies: {types: {}, operations: {}}
        capabilities: []
        steps:
          - id: result
            return: {object: {value: {literal: ok}}}
        """;
}
