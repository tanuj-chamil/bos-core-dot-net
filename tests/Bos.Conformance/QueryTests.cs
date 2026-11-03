using System.Data.Common;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;
using Bos.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bos.Conformance;

public sealed class QueryTests
{
    private static readonly JsonObject Definition = JsonNode.Parse("""
        {"name":"Item","accessMode":"direct","allowedOperations":[],"fields":{
          "amount":{"kind":"integer","indexed":true,"nullable":true},
          "price":{"kind":"decimal","precision":18,"scale":2,"indexed":true},
          "label":{"kind":"string","maxLength":100,"indexed":true},
          "hidden":{"kind":"string","maxLength":100}}}
        """)!.AsObject();
    private static SqliteQueryEngine Engine() => new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static InvocationContext Context(string principal = "alice") => new()
    {
        Caller = new Caller(principal), Budget = new Budget { Deadline = DateTimeOffset.UtcNow.AddMinutes(1) }
    };
    private static JsonObject Query(string json) => JsonNode.Parse(json)!.AsObject();
    private static string Id(int id) => $"00000000-0000-4000-8000-{id:D12}";
    private static int[] Ids(JsonObject page) => page["items"]!.AsArray().Select(n => int.Parse(n!["id"]!.GetValue<string>()[^12..])).ToArray();

    [Theory]
    [InlineData("asc", new[] { 6, 5, 1, 2, 4, 3 })]
    [InlineData("desc", new[] { 3, 4, 2, 1, 5, 6 })]
    public async Task Keyset_pages_preserve_numeric_sentinel_and_tie_order(string direction, int[] expected)
    {
        await using var uow = await QueryUow.CreateAsync();
        await Seed(uow);
        var engine = Engine();
        var context = Context();
        var query = Query($$"""{"orderBy":[{"field":"amount","direction":"{{direction}}"}],"limit":2}""");
        var actual = new List<int>();
        do
        {
            var page = await engine.ListAsync(uow, context, "Item", Definition, query);
            actual.AddRange(Ids(page));
            query["cursor"] = page["nextCursor"]?.DeepClone();
        } while (query["cursor"] is not null);
        Assert.Equal(expected, actual);
        Assert.Equal(6, context.Budget.Returned);
        Assert.Contains("Item", context.ReadTypes);
    }

    [Theory]
    [InlineData("eq", "\"2\"", new[] { 2, 4 })]
    [InlineData("ne", "\"2\"", new[] { 1, 3 })]
    [InlineData("gt", "\"2\"", new[] { 3 })]
    [InlineData("gte", "\"2\"", new[] { 2, 3, 4 })]
    [InlineData("lt", "\"2\"", new[] { 1 })]
    [InlineData("lte", "\"2\"", new[] { 1, 2, 4 })]
    [InlineData("in", "[\"2\",\"10\"]", new[] { 2, 3, 4 })]
    [InlineData("notIn", "[\"2\"]", new[] { 1, 3 })]
    [InlineData("in", "[]", new int[] { })]
    [InlineData("notIn", "[]", new[] { 1, 2, 3, 4 })]
    [InlineData("isNull", "true", new[] { 5 })]
    [InlineData("isNull", "false", new[] { 1, 2, 3, 4 })]
    [InlineData("isMissing", "true", new[] { 6 })]
    [InlineData("isMissing", "false", new[] { 1, 2, 3, 4, 5 })]
    public async Task Comparisons_distinguish_present_null_and_missing(string op, string value, int[] expected)
    {
        await using var uow = await QueryUow.CreateAsync();
        await Seed(uow);
        var query = Query($$"""{"where":[{"field":"amount","op":"{{op}}","value":{{value}}}]}""");
        var engine = Engine();
        Assert.Equal(expected, Ids(await engine.ListAsync(uow, Context(), "Item", Definition, query)));
        var count = await engine.CountAsync(uow, Context(), "Item", Definition, query);
        Assert.Equal(expected.Length.ToString(), count["count"]!.GetValue<string>());
    }

    [Fact]
    public async Task Decimal_filters_and_sort_use_typed_keys()
    {
        await using var uow = await QueryUow.CreateAsync();
        await Add(uow, 1, Query("""{"price":"-10.50"}"""));
        await Add(uow, 2, Query("""{"price":"2.00"}"""));
        await Add(uow, 3, Query("""{"price":"10.00"}"""));
        var query = Query("""{"where":[{"field":"price","op":"gt","value":"0.00"}],"orderBy":[{"field":"price","direction":"asc"}]}""");
        Assert.Equal(new[] { 2, 3 }, Ids(await Engine().ListAsync(uow, Context(), "Item", Definition, query)));
    }

    [Fact]
    public async Task String_order_matches_CLR_ordinal_for_supplementary_unicode()
    {
        await using var uow = await QueryUow.CreateAsync();
        await Add(uow, 1, new JsonObject { ["label"] = "\uE000" });
        await Add(uow, 2, new JsonObject { ["label"] = "\U00010000" });
        var engine = Engine();
        var query = Query("""{"orderBy":[{"field":"label","direction":"asc"}],"limit":1}""");
        var first = await engine.ListAsync(uow, Context(), "Item", Definition, query);
        Assert.Equal(new[] { 2 }, Ids(first));
        query["cursor"] = first["nextCursor"]!.DeepClone();
        Assert.Equal(new[] { 1 }, Ids(await engine.ListAsync(uow, Context(), "Item", Definition, query)));
        query = new JsonObject { ["where"] = new JsonArray(new JsonObject { ["field"] = "label", ["op"] = "gt", ["value"] = "\U00010000" }) };
        Assert.Equal(new[] { 1 }, Ids(await engine.ListAsync(uow, Context(), "Item", Definition, query)));
    }

    [Fact]
    public async Task Normalized_equivalent_filters_share_cursor_binding()
    {
        await using var uow = await QueryUow.CreateAsync();
        await Seed(uow);
        var engine = Engine();
        var query = Query("""{"where":[{"field":"amount","op":"in","value":["+02","2","10"]},{"field":"label","op":"ne","value":"none"}],"limit":1}""");
        var first = await engine.ListAsync(uow, Context(), "Item", Definition, query);
        var equivalent = Query("""{"where":[{"field":"label","op":"ne","value":"none"},{"field":"amount","op":"in","value":["10","2"]}],"limit":2}""");
        equivalent["cursor"] = first["nextCursor"]!.DeepClone();
        Assert.Equal(new[] { 3, 4 }, Ids(await engine.ListAsync(uow, Context(), "Item", Definition, equivalent)));
    }

    [Fact]
    public async Task Cursors_roundtrip_maximum_length_string_sort_keys()
    {
        await using var uow = await QueryUow.CreateAsync();
        var prefix = new string('a', 16383);
        await Add(uow, 1, new JsonObject { ["label"] = prefix + "a" });
        await Add(uow, 2, new JsonObject { ["label"] = prefix + "b" });
        var definition = (JsonObject)Definition.DeepClone();
        definition["fields"]!["label"]!["maxLength"] = 16384;
        var query = Query("""{"orderBy":[{"field":"label"}],"limit":1}""");
        var first = await Engine().ListAsync(uow, Context(), "Item", definition, query);
        Assert.Equal(new[] { 1 }, Ids(first));
        query["cursor"] = first["nextCursor"]!.DeepClone();
        Assert.Equal(new[] { 2 }, Ids(await Engine().ListAsync(uow, Context(), "Item", definition, query)));
    }

    [Fact]
    public async Task Id_filters_AND_projection_and_tombstone_exclusion()
    {
        await using var uow = await QueryUow.CreateAsync();
        await Seed(uow);
        await Add(uow, 7, Query("""{"amount":"2","hidden":"secret"}"""), deleted: true);
        var query = Query($$"""{"where":[{"field":"amount","op":"eq","value":"2"},{"field":"id","op":"eq","value":"{{Id(2)}}"}],"select":["amount","hidden"]}""");
        var page = await Engine().ListAsync(uow, Context(), "Item", Definition, query);
        Assert.Equal(new[] { 2 }, Ids(page));
        var row = page["items"]![0]!.AsObject();
        Assert.Equal(new[] { "data", "id", "revision", "type", "typeVersion" }, row.Select(p => p.Key).Order().ToArray());
        Assert.False(row["data"]!.AsObject().ContainsKey("label"));
        Assert.Equal("kept", row["data"]!["hidden"]!.GetValue<string>());
        var count = await Engine().CountAsync(uow, Context(), "Item", Definition, new JsonObject());
        Assert.Equal("6", count["count"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"limit\":201}")]
    [InlineData("{\"limit\":null}")]
    [InlineData("{\"limit\":\"2\"}")]
    [InlineData("{\"where\":null}")]
    [InlineData("{\"where\":[{\"field\":\"hidden\",\"op\":\"eq\",\"value\":\"x\"}]}")]
    [InlineData("{\"where\":[{\"field\":\"amount\",\"op\":\"eq\",\"value\":null}]}")]
    [InlineData("{\"where\":[{\"field\":\"amount\",\"op\":\"ne\",\"value\":null}]}")]
    [InlineData("{\"where\":[{\"field\":\"amount\",\"op\":\"in\",\"value\":[null]}]}")]
    [InlineData("{\"where\":[{\"field\":\"amount\",\"op\":\"eq\",\"value\":2}]}")]
    [InlineData("{\"where\":[{\"field\":\"amount\",\"op\":\"isNull\",\"value\":\"true\"}]}")]
    [InlineData("{\"where\":[{\"field\":\"amount\",\"op\":\"like\",\"value\":\"2\"}]}")]
    [InlineData("{\"select\":[\"unknown\"]}")]
    [InlineData("{\"orderBy\":[{\"field\":\"hidden\"}]}")]
    [InlineData("{\"orderBy\":[{\"field\":\"amount\",\"direction\":\"DROP TABLE Records\"}]}")]
    [InlineData("{\"offset\":1}")]
    public async Task Invalid_queries_fail_before_executing_sql(string json)
    {
        await using var uow = await QueryUow.CreateAsync();
        await Assert.ThrowsAsync<BosException>(() => Engine().ListAsync(uow, Context(), "Item", Definition, Query(json)));
    }

    [Fact]
    public async Task Filter_and_IN_caps_are_enforced()
    {
        await using var uow = await QueryUow.CreateAsync();
        var where = new JsonArray(Enumerable.Range(0, 11).Select(_ => (JsonNode?)Query("""{"field":"amount","op":"eq","value":"2"}""")).ToArray());
        await Assert.ThrowsAsync<BosException>(() => Engine().ListAsync(uow, Context(), "Item", Definition, new JsonObject { ["where"] = where }));
        var values = new JsonArray(Enumerable.Range(0, 51).Select(i => (JsonNode?)JsonValue.Create(i.ToString())).ToArray());
        var query = new JsonObject { ["where"] = new JsonArray(new JsonObject { ["field"] = "amount", ["op"] = "in", ["value"] = values }) };
        await Assert.ThrowsAsync<BosException>(() => Engine().ListAsync(uow, Context(), "Item", Definition, query));
    }

    [Fact]
    public async Task Hostile_values_are_parameters()
    {
        await using var uow = await QueryUow.CreateAsync();
        var hostile = "' OR 1=1; DROP TABLE Records; --";
        await Add(uow, 1, new JsonObject { ["label"] = hostile });
        await Add(uow, 2, new JsonObject { ["label"] = "normal" });
        var query = new JsonObject { ["where"] = new JsonArray(new JsonObject { ["field"] = "label", ["op"] = "eq", ["value"] = hostile }) };
        Assert.Equal(new[] { 1 }, Ids(await Engine().ListAsync(uow, Context(), "Item", Definition, query)));
        Assert.Equal("2", (await Engine().CountAsync(uow, Context(), "Item", Definition, new JsonObject()))["count"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cursors_reject_tampering_principal_query_schema_and_dependency_changes()
    {
        await using var uow = await QueryUow.CreateAsync();
        await Seed(uow);
        var engine = Engine();
        var context = Context();
        context.TypeVersions["Item"] = [1];
        var page = await engine.ListAsync(uow, context, "Item", Definition, Query("""{"limit":1}"""));
        var cursor = page["nextCursor"]!.GetValue<string>();
        var queries = new[]
        {
            new JsonObject { ["cursor"] = (cursor[0] == 'A' ? "B" : "A") + cursor[1..] },
            Query("""{"where":[{"field":"amount","op":"eq","value":"2"}]}"""),
            Query("""{"select":["amount"]}"""),
            Query("""{"orderBy":[{"field":"amount","direction":"desc"}]}""")
        };
        foreach (var query in queries)
        {
            if (!query.ContainsKey("cursor")) query["cursor"] = cursor;
            Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.ListAsync(uow, context, "Item", Definition, query))).Code);
        }
        var continuation = new JsonObject { ["cursor"] = cursor, ["limit"] = 3 };
        var bob = Context("bob"); bob.TypeVersions["Item"] = [1];
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.ListAsync(uow, bob, "Item", Definition, continuation))).Code);
        var changed = (JsonObject)Definition.DeepClone(); changed["description"] = "new schema";
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.ListAsync(uow, context, "Item", changed, continuation))).Code);
        context.TypeVersions["Item"] = [1, 2];
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.ListAsync(uow, context, "Item", Definition, continuation))).Code);
        context.TypeVersions["Item"] = [1];
        Assert.Equal(3, (await engine.ListAsync(uow, context, "Item", Definition, continuation))["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task Flow_read_dependencies_and_accepted_versions_apply_to_list_and_count()
    {
        await using var uow = await QueryUow.CreateAsync();
        await Add(uow, 1, Query("""{"amount":"2"}"""), version: 2);
        var context = Context(); context.RunId = "run";
        var engine = Engine();
        Assert.Equal("SCHEMA_VERSION_MISMATCH", (await Assert.ThrowsAsync<BosException>(() => engine.ListAsync(uow, context, "Item", Definition, new JsonObject()))).Code);
        context.TypeVersions["Item"] = [1];
        Assert.Equal("SCHEMA_VERSION_MISMATCH", (await Assert.ThrowsAsync<BosException>(() => engine.ListAsync(uow, context, "Item", Definition, new JsonObject()))).Code);
        Assert.Equal("SCHEMA_VERSION_MISMATCH", (await Assert.ThrowsAsync<BosException>(() => engine.CountAsync(uow, context, "Item", Definition, new JsonObject()))).Code);
        context.TypeVersions["Item"] = [1, 2];
        Assert.Single((await engine.ListAsync(uow, context, "Item", Definition, new JsonObject()))["items"]!.AsArray());
    }

    [Theory]
    [InlineData("limit", "1")]
    [InlineData("cursor", "null")]
    [InlineData("select", "[]")]
    [InlineData("orderBy", "[]")]
    public async Task Count_rejects_non_filter_arguments(string name, string value)
    {
        await using var uow = await QueryUow.CreateAsync();
        await Assert.ThrowsAsync<BosException>(() => Engine().CountAsync(uow, Context(), "Item", Definition, Query($$"""{"{{name}}":{{value}}}""")));
    }

    [Fact]
    public async Task Deadline_cancellation_and_return_budget_are_enforced_and_handler_is_removed()
    {
        await using var uow = await QueryUow.CreateAsync();
        await Seed(uow);
        var context = Context(); context.Budget.Deadline = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal("QUERY_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Engine().CountAsync(uow, context, "Item", Definition, new JsonObject()))).Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine().CountAsync(uow, Context(), "Item", Definition, new JsonObject(), cancellation.Token));
        var exhausted = Context(); exhausted.Budget.ReturnRecords(1000);
        Assert.Equal("EXECUTION_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Engine().ListAsync(uow, exhausted, "Item", Definition, new JsonObject()))).Code);
        Assert.Equal("6", (await Engine().CountAsync(uow, Context(), "Item", Definition, new JsonObject()))["count"]!.GetValue<string>());
    }

    [Fact]
    public async Task VM_work_limit_aborts_exact_count_without_partial_result()
    {
        await using var uow = await QueryUow.CreateAsync();
        await uow.Execute("""
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<130000)
            INSERT INTO Records(id,type,typeVersion,revision,deleted,json)
            SELECT printf('%012d',x),'Item',1,1,0,'{}' FROM n;
            """);
        var exception = await Assert.ThrowsAsync<BosException>(() => Engine().CountAsync(uow, Context(), "Item", Definition, new JsonObject()));
        Assert.Equal("QUERY_LIMIT", exception.Code);
        // SQLITE_INTERRUPT invalidates the active statement; the handler must not leak into unrelated work.
        Assert.Equal(130000L, await uow.Scalar("SELECT COUNT(*) FROM Records"));
    }

    [Fact]
    public async Task Catalogue_visibility_precedes_paging_and_cursor_is_keyset_bound()
    {
        await using var uow = await QueryUow.CreateAsync();
        for (var i = 1; i <= 7; i++)
            await uow.Document("users", Id(i), new JsonObject { ["id"] = Id(i), ["userName"] = i <= 4 ? "same" : "z" + i, ["active"] = i % 2 == 0 });
        var engine = Engine();
        var context = Context();
        var query = Query("""{"limit":1}""");
        bool Visible(JsonObject doc) => doc["active"]!.GetValue<bool>();
        var result = new List<int>();
        do
        {
            var page = await engine.CatalogueAsync(uow, context, "users", query, ["userName", "active"], ["userName", "id"], Visible);
            result.AddRange(Ids(page));
            query["cursor"] = page["nextCursor"]?.DeepClone();
        } while (query["cursor"] is not null);
        Assert.Equal(new[] { 2, 4, 6 }, result);
        var first = await engine.CatalogueAsync(uow, context, "users", Query("""{"limit":1}"""), ["userName", "active"], ["userName", "id"], Visible);
        var changed = Query("""{"where":[{"field":"active","op":"eq","value":true}]}""");
        changed["cursor"] = first["nextCursor"]!.DeepClone();
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.CatalogueAsync(uow, context, "users", changed, ["userName", "active"], ["userName", "id"], Visible))).Code);
        changed.Remove("cursor");
        Assert.Equal(new[] { 2, 4, 6 }, Ids(await engine.CatalogueAsync(uow, context, "users", changed, ["userName", "active"], ["userName", "id"], _ => true)));
    }

    [Fact]
    public async Task Catalogue_numeric_and_descending_orders_page_without_offset()
    {
        await using var uow = await QueryUow.CreateAsync();
        foreach (var version in new[] { 10, 2, 1 })
            await uow.Document("typeVersions", "Item:" + version, new JsonObject { ["version"] = version, ["name"] = "Item" });
        var query = Query("""{"limit":1}""");
        var actual = new List<int>();
        do
        {
            var page = await Engine().CatalogueAsync(uow, Context(), "typeVersions", query, [], ["version"], _ => true);
            actual.Add(page["items"]![0]!["version"]!.GetValue<int>());
            query["cursor"] = page["nextCursor"]?.DeepClone();
        } while (query["cursor"] is not null);
        Assert.Equal(new[] { 1, 2, 10 }, actual);
        for (var i = 1; i <= 3; i++)
            await uow.Document("runs", Id(i), new JsonObject { ["id"] = Id(i), ["runId"] = Id(i), ["startedAt"] = $"2026-01-0{i}T00:00:00Z" });
        Assert.Equal(new[] { 3, 2, 1 }, Ids(await Engine().CatalogueAsync(uow, Context(), "runs", new JsonObject(), [], ["startedAt", "runId"], _ => true)));
    }

    [Theory]
    [InlineData("{\"where\":[{\"field\":\"unknown\",\"op\":\"eq\",\"value\":\"x\"}]}")]
    [InlineData("{\"where\":[{\"field\":\"userName\",\"op\":\"ne\",\"value\":\"x\"}]}")]
    [InlineData("{\"where\":[{\"field\":\"userName\",\"op\":\"eq\",\"value\":null}]}")]
    [InlineData("{\"where\":[{\"field\":\"active\",\"op\":\"eq\",\"value\":1}]}")]
    [InlineData("{\"where\":[{\"field\":\"userName\",\"op\":\"eq\",\"value\":true}]}")]
    [InlineData("{\"orderBy\":[]}")]
    public async Task Catalogue_rejects_unknown_and_mistyped_filters(string json)
    {
        await using var uow = await QueryUow.CreateAsync();
        await Assert.ThrowsAsync<BosException>(() => Engine().CatalogueAsync(uow, Context(), "users", Query(json), ["userName", "active"], ["userName", "id"], _ => true));
        await Assert.ThrowsAsync<BosException>(() => Engine().CatalogueValuesAsync(Context(), "users", [], Query(json), ["userName", "active"], ["userName", "id"], _ => true));
    }

    [Fact]
    public async Task Value_catalogues_filter_and_page_operations_by_typed_fixed_order()
    {
        var values = new[]
        {
            Query("""{"name":"Apply","contractVersion":10,"mode":"readOnly","flowCallable":true}"""),
            Query("""{"name":"Apply","contractVersion":2,"mode":"readOnly","flowCallable":true}"""),
            Query("""{"name":"Apply","contractVersion":1,"mode":"readOnly","flowCallable":true}"""),
            Query("""{"name":"Hidden","contractVersion":1,"mode":"readOnly","flowCallable":true}"""),
            Query("""{"name":"NotCallable","contractVersion":1,"mode":"readOnly","flowCallable":false}"""),
            Query("""{"name":"WrongType","contractVersion":1,"mode":"readOnly","flowCallable":1}"""),
            Query("""{"name":"Writes","contractVersion":1,"mode":"write","flowCallable":true}""")
        };
        var context = Context();
        var query = Query("""{"where":[{"field":"mode","op":"eq","value":"readOnly"},{"field":"flowCallable","op":"eq","value":true}],"limit":1}""");
        var versions = new List<int>();
        do
        {
            var page = await Engine().CatalogueValuesAsync(context, "operationDescriptors", values.Reverse().ToArray(), query,
                ["name", "mode", "flowCallable"], ["name", "contractVersion"], row => row["name"]!.GetValue<string>() != "Hidden");
            versions.Add(page["items"]![0]!["contractVersion"]!.GetValue<int>());
            Assert.Equal("Apply", page["items"]![0]!["name"]!.GetValue<string>());
            page["items"]![0]!["name"] = "caller mutation";
            query["cursor"] = page["nextCursor"]?.DeepClone();
        } while (query["cursor"] is not null);
        Assert.Equal(new[] { 1, 2, 10 }, versions);
        Assert.Equal(values.Length * 3, context.Budget.Nodes);
        Assert.Equal(3, context.Budget.Returned);
        Assert.Equal("Apply", values[0]["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Value_catalogues_use_ID_ties_and_reapply_visibility_after_input_reordering()
    {
        var values = Enumerable.Range(1, 6).Select(id => new JsonObject { ["id"] = Id(id), ["userName"] = "same" }).Reverse().ToArray();
        var query = Query("""{"limit":2}""");
        var engine = Engine();
        var first = await engine.CatalogueValuesAsync(Context(), "users", values, query, ["userName"], ["userName"], _ => true);
        Assert.Equal(new[] { 1, 2 }, Ids(first));
        query["cursor"] = first["nextCursor"]!.DeepClone();
        var second = await engine.CatalogueValuesAsync(Context(), "users", values.Reverse().ToArray(), query, ["userName"], ["userName"], row => row["id"]!.GetValue<string>() != Id(3));
        Assert.Equal(new[] { 4, 5 }, Ids(second));
        query["cursor"] = second["nextCursor"]!.DeepClone();
        var final = await engine.CatalogueValuesAsync(Context(), "users", values, query, ["userName"], ["userName"], _ => true);
        Assert.Equal(new[] { 6 }, Ids(final));
        Assert.Null(final["nextCursor"]);
    }

    [Fact]
    public async Task Value_catalogues_page_all_500_run_steps_without_positional_cursors()
    {
        var values = Enumerable.Range(1, 500).Select(ordinal => new JsonObject { ["ordinal"] = ordinal, ["stepId"] = "sameStep", ["outcome"] = "succeeded" }).Reverse().ToArray();
        var context = Context();
        var query = Query("""{"where":[{"field":"stepId","op":"eq","value":"sameStep"},{"field":"outcome","op":"eq","value":"succeeded"}],"limit":200}""");
        var actual = new List<int>();
        do
        {
            var page = await Engine().CatalogueValuesAsync(context, "steps:runA", values, query, ["stepId", "outcome"], ["ordinal"], _ => true);
            Assert.InRange(page["items"]!.AsArray().Count, 1, 200);
            actual.AddRange(page["items"]!.AsArray().Select(row => row!["ordinal"]!.GetValue<int>()));
            query["cursor"] = page["nextCursor"]?.DeepClone();
        } while (query["cursor"] is not null);
        Assert.Equal(Enumerable.Range(1, 500), actual);
        Assert.Equal(1500, context.Budget.Nodes);
        Assert.Equal(500, context.Budget.Returned);
    }

    [Fact]
    public async Task Value_catalogues_support_registries_above_1000_and_enforce_input_bound()
    {
        var values = Enumerable.Range(1, 1200).Select(i => new JsonObject { ["name"] = $"Op{i:D4}", ["contractVersion"] = 1 }).Reverse().ToArray();
        var context = Context();
        var page = await Engine().CatalogueValuesAsync(context, "operationDescriptors", values, Query("""{"limit":200}"""), ["name"], ["name", "contractVersion"], _ => true);
        Assert.Equal(200, page["items"]!.AsArray().Count);
        Assert.Equal("Op0001", page["items"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Op0200", page["items"]![199]!["name"]!.GetValue<string>());
        Assert.NotNull(page["nextCursor"]);
        Assert.Equal(1200, context.Budget.Nodes);
        var overBound = Enumerable.Repeat(new JsonObject(), 10001).ToArray();
        Assert.Equal("QUERY_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Engine().CatalogueValuesAsync(Context(), "operationDescriptors", overBound, new JsonObject(), [], ["name"], _ => false))).Code);
        var atBound = Context();
        var empty = await Engine().CatalogueValuesAsync(atBound, "operationDescriptors", overBound[..10000], new JsonObject(), [], ["name"], _ => false);
        Assert.Empty(empty["items"]!.AsArray());
        Assert.Equal(10000, atBound.Budget.Nodes);
    }

    [Fact]
    public async Task SQL_and_value_cursors_reject_cross_source_and_parent_substitution()
    {
        await using var uow = await QueryUow.CreateAsync();
        var values = new List<JsonObject>();
        foreach (var name in new[] { "A", "B" })
            foreach (var version in new[] { 1, 2 })
            {
                var row = new JsonObject { ["name"] = name, ["version"] = version };
                values.Add(row);
                await uow.Document("typeVersions", name + ":" + version, row);
            }
        var engine = Engine();
        var context = Context(); context.CursorScope = "typeVersions:A";
        var query = Query("""{"limit":1}""");
        bool A(JsonObject row) => row["name"]!.GetValue<string>() == "A";
        var sql = await engine.CatalogueAsync(uow, context, "typeVersions", query, [], ["version"], A);
        var memory = await engine.CatalogueValuesAsync(context, "typeVersions", values, query, [], ["version"], A);
        query["cursor"] = sql["nextCursor"]!.DeepClone();
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.CatalogueValuesAsync(context, "typeVersions", values, query, [], ["version"], A))).Code);
        query["cursor"] = memory["nextCursor"]!.DeepClone();
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.CatalogueAsync(uow, context, "typeVersions", query, [], ["version"], A))).Code);
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.CatalogueValuesAsync(context, "flowVersions", values, query, [], ["version"], A))).Code);
        context.CursorScope = "typeVersions:B";
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.CatalogueValuesAsync(context, "typeVersions", values, query, [], ["version"], row => !A(row)))).Code);
        query["cursor"] = sql["nextCursor"]!.DeepClone();
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.CatalogueAsync(uow, context, "typeVersions", query, [], ["version"], row => !A(row)))).Code);
        context.CursorScope = "typeVersions:A";
        var continued = await engine.CatalogueAsync(uow, context, "typeVersions", query, [], ["version"], A);
        Assert.Equal(2, continued["items"]![0]!["version"]!.GetValue<int>());
    }

    [Fact]
    public async Task Value_cursors_bind_run_scope_principal_filters_and_sort()
    {
        var values = Enumerable.Range(1, 3).Select(i => new JsonObject { ["ordinal"] = i, ["stepId"] = "step", ["outcome"] = "succeeded" }).ToArray();
        var engine = Engine();
        var query = Query("""{"limit":1}""");
        var first = await engine.CatalogueValuesAsync(Context(), "steps:runA", values, query, ["stepId", "outcome"], ["ordinal"], _ => true);
        query["cursor"] = first["nextCursor"]!.DeepClone();
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.CatalogueValuesAsync(Context(), "steps:runB", values, query, ["stepId", "outcome"], ["ordinal"], _ => true))).Code);
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.CatalogueValuesAsync(Context("bob"), "steps:runA", values, query, ["stepId", "outcome"], ["ordinal"], _ => true))).Code);
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.CatalogueValuesAsync(Context(), "steps:runA", values, query, ["stepId", "outcome"], ["-ordinal"], _ => true))).Code);
        query["where"] = new JsonArray(Query("""{"field":"outcome","op":"eq","value":"succeeded"}"""));
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<BosException>(() => engine.CatalogueValuesAsync(Context(), "steps:runA", values, query, ["stepId", "outcome"], ["ordinal"], _ => true))).Code);
    }

    [Fact]
    public async Task Value_catalogues_enforce_item_return_deadline_and_cancellation_budgets()
    {
        var values = new[] { Query("""{"name":"One","contractVersion":1}"""), Query("""{"name":"Two","contractVersion":1}""") };
        var exhausted = Context(); exhausted.Budget.Node(9999);
        Assert.Equal("EXECUTION_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Engine().CatalogueValuesAsync(exhausted, "ops", values, new JsonObject(), [], ["name", "contractVersion"], _ => false))).Code);
        var returned = Context(); returned.Budget.ReturnRecords(1000);
        Assert.Equal("EXECUTION_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Engine().CatalogueValuesAsync(returned, "ops", values, new JsonObject(), [], ["name", "contractVersion"], _ => true))).Code);
        var expired = Context(); expired.Budget.Deadline = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal("QUERY_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Engine().CatalogueValuesAsync(expired, "ops", values, new JsonObject(), [], ["name", "contractVersion"], _ => true))).Code);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine().CatalogueValuesAsync(Context(), "ops", values, new JsonObject(), [], ["name", "contractVersion"], _ => { cancellation.Cancel(); return false; }, cancellation.Token));
    }

    [Fact]
    public async Task Catalogue_normalizes_strings_and_reapplies_visibility_on_continuation()
    {
        await using var uow = await QueryUow.CreateAsync();
        for (var id = 1; id <= 3; id++) await uow.Document("users", Id(id), new JsonObject { ["id"] = Id(id), ["userName"] = "é" });
        var query = new JsonObject { ["limit"] = 1, ["where"] = new JsonArray(new JsonObject { ["field"] = "userName", ["op"] = "eq", ["value"] = "e\u0301" }) };
        var first = await Engine().CatalogueAsync(uow, Context(), "users", query, ["userName"], ["userName", "id"], _ => true);
        Assert.Equal(new[] { 1 }, Ids(first));
        query["cursor"] = first["nextCursor"]!.DeepClone();
        var page = await Engine().CatalogueAsync(uow, Context(), "users", query, ["userName"], ["userName", "id"], row => row["id"]!.GetValue<string>() != Id(2));
        Assert.Equal(new[] { 3 }, Ids(page));
        Assert.Null(page["nextCursor"]);
    }

    [Fact]
    public async Task Catalogue_checks_cancellation_and_deadline_during_visibility_scan()
    {
        await using var uow = await QueryUow.CreateAsync();
        await uow.Document("users", Id(1), new JsonObject { ["id"] = Id(1), ["userName"] = "one" });
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine().CatalogueAsync(uow, Context(), "users", new JsonObject(), [], ["userName"], _ => { cancellation.Cancel(); return false; }, cancellation.Token));
        var context = Context();
        context.Budget.Deadline = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal("QUERY_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Engine().CatalogueAsync(uow, context, "users", new JsonObject(), [], ["userName"], _ => true))).Code);
    }

    [Fact]
    public async Task Catalogue_invisible_scan_is_SQL_work_bounded()
    {
        await using var uow = await QueryUow.CreateAsync();
        await uow.Execute("""
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000)
            INSERT INTO Documents(collection,key,json) SELECT 'users',printf('%012d',x),json_object('userName',printf('%012d',x),'active',0) FROM n;
            """);
        Assert.Equal("QUERY_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Engine().CatalogueAsync(uow, Context(), "users", new JsonObject(), ["active"], ["userName"], _ => false))).Code);
    }

    [Fact]
    public async Task Audit_catalogue_reads_real_appends_with_filters_and_numeric_keyset_pages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bos-audit-query-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteUnitOfWorkFactory(path);
            await factory.InitializeAsync();
            await using (var write = await factory.BeginWriteAsync())
            {
                for (var i = 1; i <= 12; i++)
                    await write.Documents.AppendAuditAsync(new JsonObject
                    {
                        ["sequence"] = 999, ["actor"] = i == 2 ? "bob" : "alice",
                        ["method"] = i == 4 ? "records.update" : "records.create", ["objectId"] = Id(i)
                    });
                await write.CommitAsync();
            }

            await using var read = await factory.BeginReadAsync();
            Assert.Empty(await read.Documents.ListAsync("audit"));
            var context = Context();
            context.Budget.ReturnRecords(1000);
            var engine = Engine();
            var all = await engine.CatalogueAsync(read, context, "audit", new JsonObject(), ["actor", "method"], ["sequence"], _ => true);
            Assert.Equal(Enumerable.Range(1, 12).Select(i => (long)i), all["items"]!.AsArray().Select(row => row!["sequence"]!.GetValue<long>()));
            Assert.Null(all["nextCursor"]);

            var query = Query("""{"where":[{"field":"actor","op":"eq","value":"alice"},{"field":"method","op":"eq","value":"records.create"}],"limit":3}""");
            var sequences = new List<long>();
            do
            {
                var page = await engine.CatalogueAsync(read, context, "audit", query, ["actor", "method"], ["sequence"], row => row["sequence"]!.GetValue<long>() != 6);
                sequences.AddRange(page["items"]!.AsArray().Select(row => row!["sequence"]!.GetValue<long>()));
                query["cursor"] = page["nextCursor"]?.DeepClone();
            } while (query["cursor"] is not null);
            Assert.Equal(new long[] { 1, 3, 5, 7, 8, 9, 10, 11, 12 }, sequences);
            Assert.Equal(1000, context.Budget.Returned);
            Assert.Empty(await read.Documents.ListAsync("audit"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task Real_storage_queries_share_staged_indexes_and_rollback()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bos-query-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteUnitOfWorkFactory(path);
            await factory.InitializeAsync();
            await using (var write = await factory.BeginWriteAsync())
            {
                await write.Documents.PutAsync("typeVersions", "Item:1", new JsonObject { ["name"] = "Item", ["version"] = 1, ["definition"] = Definition.DeepClone() });
                var context = Context(); context.RunId = "flow"; context.TypeVersions["Item"] = [1];
                foreach (var id in new[] { 1, 2 })
                {
                    var record = new JsonObject
                    {
                        ["id"] = Id(id), ["type"] = "Item", ["typeVersion"] = 1, ["revision"] = 1, ["deleted"] = false,
                        ["data"] = new JsonObject { ["amount"] = id.ToString() }, ["createdAt"] = "2026-01-01T00:00:00.000Z",
                        ["updatedAt"] = "2026-01-01T00:00:00.000Z", ["createdBy"] = "alice", ["updatedBy"] = "alice"
                    };
                    await write.Records.SaveAsync(record, Definition, context);
                }
                var query = Query("""{"orderBy":[{"field":"amount"}],"limit":1}""");
                var first = await Engine().ListAsync(write, context, "Item", Definition, query);
                Assert.Equal(new[] { 1 }, Ids(first));
                query["cursor"] = first["nextCursor"]!.DeepClone();
                Assert.Equal(new[] { 2 }, Ids(await Engine().ListAsync(write, context, "Item", Definition, query)));
                Assert.Equal("2", (await Engine().CountAsync(write, context, "Item", Definition, new JsonObject()))["count"]!.GetValue<string>());
                await write.RollbackAsync();
            }
            await using var read = await factory.BeginReadAsync();
            Assert.Equal("0", (await Engine().CountAsync(read, Context(), "Item", Definition, new JsonObject()))["count"]!.GetValue<string>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    private static async Task Seed(QueryUow uow)
    {
        var amounts = new[] { "-10", "2", "10", "2" };
        for (var i = 0; i < amounts.Length; i++)
            await Add(uow, i + 1, new JsonObject { ["amount"] = amounts[i], ["label"] = "row" + i, ["hidden"] = "kept" });
        await Add(uow, 5, new JsonObject { ["amount"] = null });
        await Add(uow, 6, new JsonObject());
    }

    private static async Task Add(QueryUow uow, int id, JsonObject data, int version = 1, bool deleted = false)
    {
        var record = new JsonObject { ["id"] = Id(id), ["type"] = "Item", ["typeVersion"] = version, ["revision"] = 1, ["deleted"] = deleted, ["data"] = data };
        await uow.Execute("INSERT INTO Records(id,type,typeVersion,revision,deleted,json) VALUES($id,'Item',$version,1,$deleted,$json)", ("$id", Id(id)), ("$version", version), ("$deleted", deleted ? 1 : 0), ("$json", CanonicalJson.Serialize(record)));
        foreach (var (name, node) in Definition["fields"]!.AsObject())
        {
            var field = node!.AsObject();
            if (field["indexed"]?.GetValue<bool>() != true) continue;
            var exists = data.TryGetPropertyValue(name, out var value);
            var state = !exists ? 0 : value is null ? 1 : 2;
            var kind = field["kind"]!.GetValue<string>();
            await uow.Execute("INSERT INTO IndexValues(recordId,type,field,state,kind,value,orderKey) VALUES($id,'Item',$field,$state,$kind,$value,$key)",
                ("$id", Id(id)), ("$field", name), ("$state", state), ("$kind", kind), ("$value", value is null ? null : CanonicalJson.Serialize(value)), ("$key", state == 2 ? TypedIndex.Key(kind, value!) : null));
        }
    }

    private sealed class QueryUow : IUnitOfWork
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private SqliteTransaction _transaction = null!;
        public DbConnection Connection => _connection;
        public DbTransaction Transaction => _transaction;
        public bool IsWrite => true;
        public IDocumentRepository Documents => throw new NotSupportedException();
        public IRecordRepository Records => throw new NotSupportedException();

        public static async Task<QueryUow> CreateAsync()
        {
            var uow = new QueryUow();
            await uow._connection.OpenAsync();
            uow._transaction = uow._connection.BeginTransaction();
            await uow.Execute("""
                CREATE TABLE Records(id TEXT PRIMARY KEY,type TEXT,typeVersion INTEGER,revision INTEGER,deleted INTEGER,json TEXT);
                CREATE INDEX records_type ON Records(type,deleted,id);
                CREATE TABLE IndexValues(recordId TEXT,type TEXT,field TEXT,state INTEGER,kind TEXT,value TEXT,orderKey TEXT COLLATE BINARY,PRIMARY KEY(recordId,field));
                CREATE INDEX index_lookup ON IndexValues(type,field,state,orderKey,recordId);
                CREATE TABLE Documents(collection TEXT,key TEXT,json TEXT,PRIMARY KEY(collection,key));
                """);
            return uow;
        }

        public async Task Execute(string sql, params (string Name, object? Value)[] values)
        {
            using var command = _connection.CreateCommand(); command.Transaction = _transaction; command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<object?> Scalar(string sql)
        {
            using var command = _connection.CreateCommand(); command.Transaction = _transaction; command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        public Task Document(string collection, string key, JsonObject document) => Execute("INSERT INTO Documents(collection,key,json) VALUES($c,$k,$j)", ("$c", collection), ("$k", key), ("$j", CanonicalJson.Serialize(document)));
        public Task CommitAsync(CancellationToken ct = default) => _transaction.CommitAsync(ct);
        public Task RollbackAsync(CancellationToken ct = default) => _transaction.RollbackAsync(ct);
        public async ValueTask DisposeAsync() { await _transaction.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
