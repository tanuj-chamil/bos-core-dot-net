using System.Data.Common;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Application;
using Bos.Core;
using Bos.Flows;
using Xunit;

namespace Bos.Conformance;

public sealed class FlowLanguageTests
{
    [Fact]
    public void Canonical_ast_ignores_layout_but_source_hash_does_not()
    {
        var compiler = new FlowCompiler(new Registry());
        string source = Source(Return(Object(("value", Literal("hello"))))).ToJsonString();
        var a = compiler.Compile(source); var b = compiler.Compile("# comment\n" + source + "\n");
        Assert.Equal("bos.ast/1.0", a.Ast["astFormat"]!.GetValue<string>());
        Assert.Equal(a.AstHash, b.AstHash); Assert.NotEqual(a.SourceHash, b.SourceHash);
        Assert.Equal(CanonicalJson.Hash(a.Ast), a.AstHash);
    }

    [Theory]
    [InlineData("a: 1\na: 2")]
    [InlineData("a: {b: 1, b: 2}")]
    [InlineData("a: &anchor [1]\nb: *anchor")]
    [InlineData("a: !tag text")]
    [InlineData("true: text")]
    [InlineData("? [a, b]\n: text")]
    [InlineData("a: {<<: {b: text}}")]
    [InlineData("a: 1\n---\nb: 2")]
    [InlineData("a: 2025-01-01")]
    [InlineData("a: 0xFF")]
    [InlineData("a: 01")]
    [InlineData("a: .nan")]
    [InlineData("a: 1.2")]
    public void Unsafe_yaml_is_rejected_before_execution(string yaml)
        => Assert.Throws<BosException>(() => new FlowCompiler(new Registry()).Compile(yaml));

    [Fact]
    public void Closed_grammar_schema_scope_unique_ids_and_return_paths_are_checked()
    {
        var compiler = new FlowCompiler(new Registry());
        void Invalid(JsonObject source) => Assert.Throws<BosException>(() => compiler.Compile(source.ToJsonString()));
        var unknown = Source(Return(Object(("value", Literal("ok"))))); unknown["surprise"] = true; Invalid(unknown);
        var schema = Source(Return(Object(("value", Literal("ok"))))); schema["inputSchema"]!["fields"]!["x"] = new JsonObject { ["kind"] = "string", ["indexed"] = true }; Invalid(schema);
        Invalid(Source(new JsonObject { ["id"] = "x", ["let"] = "input", ["value"] = Literal("x") }, Return(Object(("value", Literal("ok"))))));
        Invalid(Source(new JsonObject { ["id"] = "x", ["if"] = Literal(true), ["then"] = new JsonArray(Return(Object(("value", Literal("a"))))) }));
        Invalid(Source(new JsonObject { ["id"] = "output", ["let"] = "x", ["value"] = Literal("x") }, Return(Object(("value", Literal("ok"))))));
        Invalid(Source(Return(Object(("value", Path("unknown.field"))))));
        Invalid(Source(Return(Object(("value", Fn("unknown", Literal("x")))))));
        Invalid(Source(Return(Object(("value", Fn("add", Integer("1"), Decimal("1.0")))))));
        Invalid(Source(Return(Object(("value", new JsonObject { ["literal"] = "x", ["path"] = "input" })))));
        var branch = new JsonObject { ["id"] = "branch", ["if"] = Literal(true), ["then"] = new JsonArray(new JsonObject { ["id"] = "local", ["let"] = "local", ["value"] = Literal("x") }) };
        Invalid(Source(branch, Return(Object(("value", Path("local"))))));
    }

    public static IEnumerable<object[]> HelperCases()
    {
        yield return Case("equal", "true", Integer("2"), Integer("2"));
        yield return Case("notEqual", "true", Literal("a"), Literal("b"));
        yield return Case("greaterThan", "true", Decimal("2.00"), Decimal("1.0"));
        yield return Case("greaterOrEqual", "true", Integer("2"), Integer("2"));
        yield return Case("lessThan", "true", Literal("A"), Literal("a"));
        yield return Case("lessOrEqual", "true", Integer("2"), Integer("2"));
        yield return Case("and", "false", Literal(true), Literal(false));
        yield return Case("or", "true", Literal(false), Literal(true));
        yield return Case("not", "false", Literal(true));
        yield return Case("isNull", "true", Literal(null));
        yield return Case("isMissing", "false", Literal(null));
        yield return Case("isEmpty", "false", Integer("0"));
        yield return Case("coalesce", "\"x\"", Literal(null), Literal("x"));
        yield return Case("add", "\"3\"", Integer("1"), Integer("2"));
        yield return Case("subtract", "\"-1.50\"", Decimal("1.00"), Decimal("2.50"));
        yield return Case("multiply", "\"3.750\"", Decimal("1.50"), Decimal("2.5"));
        yield return Case("divide", "\"0.12\"", Decimal("1"), Decimal("8"), Integer("2"), Literal("halfEven"));
        yield return Case("round", "\"-1.22\"", Decimal("-1.225"), Integer("2"), Literal("halfEven"));
        yield return Case("abs", "\"12\"", Integer("-12"));
        yield return Case("length", "\"2\"", Array(Integer("1"), Integer("2")));
        yield return Case("contains", "true", Array(Literal("a"), Literal("b")), Literal("b"));
        yield return Case("sum", "\"3.00\"", Array(Decimal("1.00"), Decimal("2.00")));
        yield return Case("min", "\"1\"", Array(Integer("2"), Integer("1")));
        yield return Case("max", "\"2\"", Array(Integer("2"), Integer("1")));
        yield return Case("pluck", "[\"x\",\"y\"]", Array(Object(("name", Literal("x"))), Object(("name", Literal("y")))), Literal("name"));
        yield return Case("trim", "\"x\"", Literal(" x "));
        yield return Case("concat", "\"xy\"", Literal("x"), Literal("y"));
        yield return Case("lower", "\"abc\"", Literal("ABC"));
        yield return Case("upper", "\"ABC\"", Literal("abc"));
        yield return Case("startsWith", "true", Literal("Abc"), Literal("A"));
        yield return Case("endsWith", "false", Literal("Abc"), Literal("C"));
        yield return Case("today", "\"2026-11-04\"", Literal("Asia/Colombo"));
        yield return Case("now", "\"2026-11-03T23:00:00.000Z\"");
        yield return Case("addDays", "\"2024-03-01\"", Fn("parseDate", Literal("2024-02-28")), Integer("2"));
        yield return Case("daysBetween", "\"-2\"", Fn("parseDate", Literal("2024-03-01")), Fn("parseDate", Literal("2024-02-28")));
        yield return Case("toString", "\"1.20\"", Decimal("1.20"));
        yield return Case("parseInteger", "\"12\"", Literal("12"));
        yield return Case("parseDecimal", "\"12.50\"", Literal("12.50"));
        yield return Case("parseDate", "\"2024-02-29\"", Literal("2024-02-29"));
    }
    private static object[] Case(string name, string expected, params JsonObject[] args) => [name, expected, args];

    [Theory]
    [MemberData(nameof(HelperCases))]
    public async Task All_pure_helpers_have_typed_deterministic_results(string name, string expected, JsonObject[] args)
    {
        // toString verifies scalar output independently of the schema's scalar kind;
        // pluck retains its array result and receives a matching array schema.
        JsonObject expression = Fn(name, args);
        JsonObject source;
        if (name == "pluck")
        {
            source = Source(Return(Object(("value", expression))));
            source["outputSchema"]!["fields"]!["value"] = new JsonObject { ["kind"] = "array", ["required"] = true, ["maxItems"] = 200, ["items"] = new JsonObject { ["kind"] = "string" } };
        }
        else source = Source(Return(Object(("value", Fn("toString", expression)))));
        var result = await Run(source);
        string actual = result!["value"]!.ToJsonString();
        if (name != "pluck" && JsonNode.Parse(expected) is JsonValue e && e.TryGetValue<bool>(out bool b)) expected = JsonValue.Create(b ? "true" : "false")!.ToJsonString();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), $"{name}: expected {expected}, got {actual}");
    }

    [Theory]
    [InlineData("halfEven", "-1.22")]
    [InlineData("halfAwayFromZero", "-1.23")]
    [InlineData("towardZero", "-1.22")]
    public async Task Decimal_rounding_is_exact_and_signed(string mode, string expected)
    {
        var expression = Fn("round", Decimal("-1.225"), Integer("2"), Literal(mode));
        Assert.Equal(expected, (await Run(Source(Return(Object(("value", Fn("toString", expression)))))))!["value"]!.GetValue<string>());
        var exact = Fn("subtract", Fn("multiply", Decimal("999999999999.99"), Decimal("1.00000000000001")), Decimal("999999999999.99"));
        Assert.Equal("0.0099999999999999", (await Run(Source(Return(Object(("value", Fn("toString", exact)))))))!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Arithmetic_overflow_never_silently_rounds()
    {
        var integer = Source(Return(Object(("value", Fn("toString", Fn("add", Integer(long.MaxValue.ToString()), Integer("1")))))));
        Assert.Equal("ARITHMETIC_OVERFLOW", (await Assert.ThrowsAsync<BosException>(() => Run(integer))).Code);
        var precision = Source(Return(Object(("value", Fn("toString", Fn("multiply", Decimal("99999999999999.99"), Decimal("1.00000000000001")))))));
        Assert.Equal("ARITHMETIC_OVERFLOW", (await Assert.ThrowsAsync<BosException>(() => Run(precision))).Code);
    }

    [Fact]
    public async Task Missing_is_distinct_and_short_circuit_does_not_evaluate_unused_expressions()
    {
        var bad = Fn("greaterThan", Fn("divide", Decimal("1"), Decimal("0"), Integer("2"), Literal("halfEven")), Decimal("0"));
        var source = Source(Return(Object(("value", Fn("toString", Fn("or", Literal(true), bad))))));
        Assert.Equal("true", (await Run(source))!["value"]!.GetValue<string>());
        source = Source(Return(Object(("value", Fn("toString", Fn("isMissing", Path("input.optional")))))));
        source["inputSchema"]!["fields"]!["optional"] = new JsonObject { ["kind"] = "string", ["nullable"] = true };
        Assert.Equal("true", (await Run(source))!["value"]!.GetValue<string>());
        Assert.Equal("false", (await Run(source, new JsonObject { ["optional"] = null }))!["value"]!.GetValue<string>());
        var missing = Source(Return(Object(("value", Path("input.optional")))));
        missing["inputSchema"]!["fields"]!["optional"] = new JsonObject { ["kind"] = "string" };
        Assert.Equal("MISSING_VALUE", (await Assert.ThrowsAsync<BosException>(() => Run(missing))).Code);
    }

    [Fact]
    public async Task Loop_bound_is_checked_before_first_call_and_return_ends_entire_flow()
    {
        var registry = new Registry(); registry.Add("Read", new JsonObject { ["fields"] = new JsonObject() }, new JsonObject { ["fields"] = new JsonObject() });
        var loop = new JsonObject { ["id"] = "loop", ["forEach"] = Array(Integer("1"), Integer("2")), ["as"] = "item", ["maxItems"] = 1,
            ["steps"] = new JsonArray(new JsonObject { ["id"] = "read", ["call"] = "Read", ["args"] = new JsonObject() }) };
        var source = Source(loop, Return(Object(("value", Literal("after")))));
        source["dependencies"]!["operations"]!["Read"] = 1;
        source["capabilities"] = new JsonArray("operations.execute:Read");
        var dispatcher = new Dispatcher();
        Assert.Equal("EXECUTION_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Run(source, registry: registry, dispatcher: dispatcher))).Code);
        Assert.Equal(0, dispatcher.Calls);
        loop["steps"] = new JsonArray(new JsonObject { ["id"] = "inside", ["return"] = Object(("value", Literal("inside"))) }); loop["maxItems"] = 2;
        Assert.Equal("inside", (await Run(source = Source(loop, Return(Object(("value", Literal("after")))))))!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Operation_failure_aborts_and_keeps_nested_location()
    {
        var registry = new Registry(); registry.Add("Read", new JsonObject { ["fields"] = new JsonObject() }, new JsonObject { ["fields"] = new JsonObject() });
        var source = Source(new JsonObject { ["id"] = "loop", ["forEach"] = Array(Integer("1")), ["as"] = "item", ["maxItems"] = 1,
            ["steps"] = new JsonArray(new JsonObject { ["id"] = "read", ["call"] = "Read", ["args"] = new JsonObject() }) }, Return(Object(("value", Literal("after")))));
        source["dependencies"]!["operations"]!["Read"] = 1;
        source["capabilities"] = new JsonArray("operations.execute:Read");
        var context = Context(); var dispatcher = new Dispatcher { Handler = _ => throw new BosException("INJECTED", "Failure") };
        var error = await Assert.ThrowsAsync<BosException>(() => Run(source, registry: registry, dispatcher: dispatcher, context: context));
        Assert.Equal("INJECTED", error.Code); Assert.True(context.Poisoned); Assert.Equal("read", context.StepId); Assert.Equal("loop[0]", context.IterationPath);
        Assert.Contains(error.Details, x => x.Path == "stepId" && x.Message == "read");
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task Exact_record_schema_decodes_business_numbers_and_metadata_separately()
    {
        var registry = new Registry();
        var input = JsonNode.Parse("""{"fields":{"expectedRevision":{"kind":"integer","required":true}}}""")!.AsObject();
        var output = JsonNode.Parse("""{"fields":{"id":{"kind":"string"},"type":{"kind":"string"},"typeVersion":{"kind":"integer"},"revision":{"kind":"integer"},"data":{"kind":"object","fields":{"quantity":{"kind":"integer"},"amount":{"kind":"decimal","precision":18,"scale":2}}}}}""")!.AsObject();
        registry.Add("Read", input, output, ["input.expectedRevision", "output.typeVersion", "output.revision"]);
        var call = new JsonObject { ["id"] = "read", ["call"] = "Read", ["args"] = new JsonObject { ["expectedRevision"] = Integer("7") }, ["as"] = "record" };
        var source = Source(call, new JsonObject { ["id"] = "check", ["require"] = Fn("equal", Path("record.revision"), Integer("7")), ["code"] = "BAD", ["message"] = "bad revision" },
            Return(Object(("value", Fn("toString", Fn("add", Path("record.data.quantity"), Integer("1")))))));
        source["dependencies"]!["operations"]!["Read"] = 1; source["dependencies"]!["types"]!["Thing"] = new JsonArray(1);
        source["capabilities"] = new JsonArray("operations.execute:Read");
        var uow = new UnitOfWork();
        uow.Docs.Values["Thing:1"] = JsonNode.Parse("""{"definition":{"name":"Thing","fields":{"quantity":{"kind":"integer","required":true},"amount":{"kind":"decimal","precision":18,"scale":2,"required":true}}}}""")!.AsObject();
        var dispatcher = new Dispatcher { Handler = args =>
        {
            Assert.Equal(7, args["expectedRevision"]!.GetValue<int>());
            return JsonNode.Parse("""{"id":"id","type":"Thing","typeVersion":1,"revision":7,"data":{"quantity":"12","amount":"1.20"}}""");
        } };
        Assert.Equal("13", (await Run(source, registry: registry, dispatcher: dispatcher, uow: uow))!["value"]!.GetValue<string>());
        Assert.Equal(new[] { "typeVersions/Thing:1" }, uow.Docs.Reads);
    }

    [Fact]
    public async Task Extended_schema_values_bind_with_normalization_as_the_flow_authority()
    {
        var source = Source(Return(Object(
            ("total", Fn("toString", Fn("add", Path("input.rate"), Decimal("0.50")))),
            ("at", Path("input.at")),
            ("labels", Path("input.labels")),
            ("choice", Path("input.choice")),
            ("money", Path("input.money")))));
        source["inputSchema"] = JsonNode.Parse("""
        {"fields":{
            "rate":{"kind":"percentage","precision":5,"scale":2,"required":true},
            "at":{"kind":"time","required":true},
            "labels":{"kind":"map","maxProperties":2,"values":{"kind":"integer"},"required":true},
            "choice":{"kind":"taggedUnion","variants":{"Count":{"kind":"integer"}},"required":true},
            "money":{"kind":"money","precision":8,"scale":2,"currencies":["USD"],"required":true}
        }}
        """);
        source["outputSchema"] = JsonNode.Parse("""
        {"fields":{
            "total":{"kind":"string","required":true},
            "at":{"kind":"time","required":true},
            "labels":{"kind":"map","maxProperties":2,"values":{"kind":"integer"},"required":true},
            "choice":{"kind":"taggedUnion","variants":{"Count":{"kind":"integer"}},"required":true},
            "money":{"kind":"money","precision":8,"scale":2,"currencies":["USD"],"required":true}
        }}
        """);
        var input = JsonNode.Parse("""{"rate":"1.2","at":"12:03:04.5","labels":{"a":"+01"},"choice":{"tag":"Count","value":"+02"},"money":{"amount":"3.4","currency":"usd"}}""")!.AsObject();
        var result = await Run(source, input);
        Assert.Equal("1.70", result!["total"]!.GetValue<string>());
        Assert.Equal("12:03:04.500", result["at"]!.GetValue<string>());
        Assert.Equal("1", result["labels"]!["a"]!.GetValue<string>());
        Assert.Equal("2", result["choice"]!["value"]!.GetValue<string>());
        Assert.Equal("3.40", result["money"]!["amount"]!.GetValue<string>());
        Assert.Equal("USD", result["money"]!["currency"]!.GetValue<string>());
    }

    [Fact]
    public async Task Runtime_budget_is_shared_by_helper_items()
    {
        var context = Context(); context.Budget.Node(9995);
        var source = Source(Return(Object(("value", Fn("toString", Fn("sum", Array(Integer("1"), Integer("2"), Integer("3"))))))));
        Assert.Equal("EXECUTION_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Run(source, context: context))).Code);
    }

    [Fact]
    public async Task Reference_flow_compiles_and_executes_through_dispatcher()
    {
        string root = RepositoryRoot();
        var parentDefinition = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(root, "docs/examples/MaterialRequest.json")))!.AsObject();
        var lineDefinition = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(root, "docs/examples/MaterialRequestLine.json")))!.AsObject();
        var registry = new Registry();
        registry.Add("records.get", JsonNode.Parse("""{"fields":{"type":{"kind":"string","required":true},"id":{"kind":"string","required":true}}}""")!.AsObject(), new JsonObject(), ["output.typeVersion", "output.revision"]);
        registry.Add("records.list", JsonNode.Parse("""{"fields":{"type":{"kind":"string","required":true},"query":{}}}""")!.AsObject(), JsonNode.Parse("""{"fields":{"items":{"kind":"array","items":{},"maxItems":200},"nextCursor":{"kind":"string","nullable":true}}}""")!.AsObject(), ["output.items[].typeVersion", "output.items[].revision"]);
        MaterialRequestOperations.Register(registry);
        var source = File.ReadAllText(System.IO.Path.Combine(root, "docs/examples/SubmitMaterialRequest.yaml"));
        var compiled = new FlowCompiler(registry).Compile(source);
        const string requestId = "11111111-1111-4111-8111-111111111111";
        var parent = JsonNode.Parse("""{"id":"11111111-1111-4111-8111-111111111111","type":"MaterialRequest","typeVersion":1,"revision":1,"data":{"title":"Test","currency":"LKR","status":"Draft","total":"0.00","contentVersion":"1"}}""")!.AsObject();
        var line = JsonNode.Parse("""{"id":"22222222-2222-4222-8222-222222222222","type":"MaterialRequestLine","typeVersion":1,"revision":1,"data":{"requestId":"11111111-1111-4111-8111-111111111111","item":"Test","quantity":"2.000","unitPrice":"450.00","amount":"0.00"}}""")!.AsObject();
        foreach (var record in new[] { parent, line })
        {
            record["deleted"] = false; record["createdAt"] = "2026-11-03T23:00:00.000Z"; record["updatedAt"] = "2026-11-03T23:00:00.000Z";
            record["createdBy"] = "principal"; record["updatedBy"] = "principal";
        }
        var uow = new UnitOfWork();
        uow.Docs.Values["MaterialRequest:1"] = new JsonObject { ["definition"] = parentDefinition };
        uow.Docs.Values["MaterialRequestLine:1"] = new JsonObject { ["definition"] = lineDefinition };
        var seen = new List<string>();
        var dispatcher = new Dispatcher { NamedHandler = (name, args) =>
        {
            seen.Add(name);
            switch (name)
            {
                case "records.get": return parent.DeepClone();
                case "records.list":
                    Assert.Equal(100, args["query"]!["limit"]!.GetValue<int>());
                    return new JsonObject { ["items"] = new JsonArray(line.DeepClone()), ["nextCursor"] = null };
                case "RepriceRequestLine": Assert.Equal(1, args["expectedRevision"]!.GetValue<int>()); return line.DeepClone();
                case "FinalizeRequest":
                    Assert.Equal(1, args["expectedRevision"]!.GetValue<int>());
                    var final = parent.DeepClone().AsObject(); final["data"]!["total"] = "900.00"; return final;
                default: throw new InvalidOperationException();
            }
        } };
        var result = await new FlowInterpreter(registry).ExecuteAsync(compiled.Ast, new JsonObject { ["requestId"] = requestId, ["revision"] = "1" }, Context(), uow, dispatcher);
        Assert.Equal(requestId, result!["requestId"]!.GetValue<string>()); Assert.Equal("900.00", result["total"]!.GetValue<string>());
        Assert.Equal(new[] { "records.get", "records.list", "RepriceRequestLine", "FinalizeRequest" }, seen);
    }

    [Fact]
    public void Publication_rejects_missing_dependencies_noncallable_operations_and_writes_in_queries()
    {
        var registry = new Registry();
        var schema = new JsonObject { ["fields"] = new JsonObject() };
        registry.Register(new Operation(new("Write", 1, "Write", schema, schema, "write", true, [], [])));
        var call = new JsonObject { ["id"] = "call", ["call"] = "Write", ["args"] = new JsonObject() };
        var source = Source(call, Return(Object(("value", Literal("ok")))));
        Assert.Throws<BosException>(() => new FlowCompiler(registry).Compile(source.ToJsonString()));
        source["dependencies"]!["operations"]!["Write"] = 1;
        Assert.Equal("MODE_VIOLATION", Assert.Throws<BosException>(() => new FlowCompiler(registry).Compile(source.ToJsonString())).Code);
        source["mode"] = "command"; source["transaction"] = "atomic";
        source["capabilities"] = new JsonArray("record.read:*");
        Assert.Throws<BosException>(() => new FlowCompiler(registry).Compile(source.ToJsonString()));
        registry.Register(new Operation(new("Private", 1, "Private", schema, schema, "read", false, [], [])));
        source = Source(Return(Object(("value", Literal("ok"))))); source["dependencies"]!["operations"]!["Private"] = 1;
        Assert.Throws<BosException>(() => new FlowCompiler(registry).Compile(source.ToJsonString()));
    }

    [Fact]
    public async Task Output_logs_allocations_and_step_entries_are_bounded()
    {
        var output = Source(new JsonObject { ["id"] = "text", ["let"] = "text", ["value"] = Literal(new string('a', 400)) },
            Return(Object(("value", Array(Enumerable.Range(0, 200).Select(_ => Path("text")).ToArray())))));
        output["outputSchema"]!["fields"]!["value"] = new JsonObject { ["kind"] = "array", ["required"] = true, ["maxItems"] = 200, ["items"] = new JsonObject { ["kind"] = "string" } };
        Assert.Equal("EXECUTION_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Run(output))).Code);
        var logs = Source(new JsonObject { ["id"] = "log1", ["log"] = new string('a', 16000) }, new JsonObject { ["id"] = "log2", ["log"] = new string('a', 16000) }, Return(Object(("value", Literal("ok")))));
        Assert.Equal("EXECUTION_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Run(logs))).Code);
        var allocation = Source(new JsonObject { ["id"] = "text", ["let"] = "text", ["value"] = Literal(new string('a', 16000)) },
            Return(Object(("value", Fn("concat", Enumerable.Range(0, 17).Select(_ => Path("text")).ToArray())))));
        Assert.Equal("EXECUTION_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Run(allocation))).Code);
        var inner = Enumerable.Range(0, 6).Select(i => (JsonNode)new JsonObject { ["id"] = "step" + i, ["let"] = "local" + i, ["value"] = Literal("x") }).ToArray();
        var steps = Source(new JsonObject { ["id"] = "loop", ["forEach"] = Array(Enumerable.Range(0, 100).Select(_ => Integer("1")).ToArray()), ["as"] = "item", ["maxItems"] = 100, ["steps"] = new JsonArray(inner) }, Return(Object(("value", Literal("ok")))));
        var context = Context();
        Assert.Equal("EXECUTION_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Run(steps, context: context))).Code);
        Assert.Equal(500, context.Steps.Count);
    }

    [Fact]
    public void Source_and_expression_nesting_are_bounded()
    {
        var compiler = new FlowCompiler(new Registry());
        Assert.Throws<BosException>(() => compiler.Compile(new string(' ', 65537)));
        JsonObject expression = Literal(true);
        for (int i = 0; i < 16; i++) expression = Fn("not", expression);
        Assert.Throws<BosException>(() => compiler.Compile(Source(Return(Object(("value", Fn("toString", expression))))).ToJsonString()));
    }

    [Fact]
    public async Task Literal_strings_remain_data_and_dynamic_helper_inputs_are_checked()
    {
        const string text = "${input.secret}; System.IO.File.Delete(\"anything\")";
        Assert.Equal(text, (await Run(Source(Return(Object(("value", Literal(text)))))))!["value"]!.GetValue<string>());
        var invalid = Source(Return(Object(("value", Fn("toString", Fn("add", Literal("1"), Integer("2")))))));
        Assert.Throws<BosException>(() => new FlowCompiler(new Registry()).Compile(invalid.ToJsonString()));
        var registry = new Registry(); registry.Add("Read", new JsonObject { ["fields"] = new JsonObject() }, new JsonObject());
        var source = Source(new JsonObject { ["id"] = "read", ["call"] = "Read", ["args"] = new JsonObject(), ["as"] = "read" },
            new JsonObject { ["id"] = "check", ["require"] = Path("read.value"), ["code"] = "BAD", ["message"] = "bad" }, Return(Object(("value", Literal("ok")))));
        source["dependencies"]!["operations"]!["Read"] = 1;
        source["capabilities"] = new JsonArray("operations.execute:Read");
        var error = await Assert.ThrowsAsync<BosException>(() => Run(source, registry: registry, dispatcher: new Dispatcher { Handler = _ => new JsonObject { ["value"] = "true" } }));
        Assert.Equal("TYPE_MISMATCH", error.Code); Assert.Equal("check", error.Path);
    }

    [Fact]
    public async Task Operation_boundary_cannot_mutate_lexical_values()
    {
        var registry = new Registry();
        registry.Add("Read", JsonNode.Parse("""{"fields":{"value":{"kind":"object","fields":{"name":{"kind":"string","required":true}},"required":true}}}""")!.AsObject(), new JsonObject { ["fields"] = new JsonObject() });
        var source = Source(new JsonObject { ["id"] = "bind", ["let"] = "original", ["value"] = Object(("name", Literal("original"))) },
            new JsonObject { ["id"] = "read", ["call"] = "Read", ["args"] = new JsonObject { ["value"] = Path("original") } }, Return(Object(("value", Path("original.name")))));
        source["dependencies"]!["operations"]!["Read"] = 1;
        source["capabilities"] = new JsonArray("operations.execute:Read");
        var dispatcher = new Dispatcher { Handler = args => { args["value"]!["name"] = "changed"; return new JsonObject(); } };
        Assert.Equal("original", (await Run(source, registry: registry, dispatcher: dispatcher))!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unpinned_returned_record_versions_fail_before_type_lookup()
    {
        var registry = new Registry(); registry.Add("Read", new JsonObject { ["fields"] = new JsonObject() }, new JsonObject(), ["output.typeVersion", "output.revision"]);
        var source = Source(new JsonObject { ["id"] = "read", ["call"] = "Read", ["args"] = new JsonObject() }, Return(Object(("value", Literal("ok")))));
        source["dependencies"]!["operations"]!["Read"] = 1; source["dependencies"]!["types"]!["Thing"] = new JsonArray(1);
        source["capabilities"] = new JsonArray("operations.execute:Read");
        var uow = new UnitOfWork();
        var error = await Assert.ThrowsAsync<BosException>(() => Run(source, registry: registry, uow: uow,
            dispatcher: new Dispatcher { Handler = _ => JsonNode.Parse("""{"id":"id","type":"Thing","typeVersion":2,"revision":1,"data":{}}""") }));
        Assert.Equal("DEPENDENCY_UNAVAILABLE", error.Code); Assert.Equal("read", error.Path); Assert.Empty(uow.Docs.Reads);
    }

    [Fact]
    public async Task Unsupported_ast_format_is_not_reinterpreted()
    {
        var registry = new Registry();
        var ast = new FlowCompiler(registry).Compile(Source(Return(Object(("value", Literal("ok"))))).ToJsonString()).Ast;
        ast["astFormat"] = "bos.ast/2.0";
        var dispatcher = new Dispatcher();
        var error = await Assert.ThrowsAsync<BosException>(() => new FlowInterpreter(registry).ExecuteAsync(ast, new(), Context(), new UnitOfWork(), dispatcher));
        Assert.Equal("DEPENDENCY_UNAVAILABLE", error.Code); Assert.Equal(0, dispatcher.Calls);
    }

    [Fact]
    public async Task Business_output_field_names_do_not_turn_json_numbers_into_metadata()
    {
        var registry = new Registry();
        registry.Add("Read", new JsonObject { ["fields"] = new JsonObject() }, JsonNode.Parse("""{"fields":{"count":{"kind":"integer","required":true}}}""")!.AsObject());
        var source = Source(new JsonObject { ["id"] = "read", ["call"] = "Read", ["args"] = new JsonObject(), ["as"] = "read" },
            Return(Object(("value", Fn("toString", Fn("add", Path("read.count"), Integer("1")))))));
        source["dependencies"]!["operations"]!["Read"] = 1;
        source["capabilities"] = new JsonArray("operations.execute:Read");
        var dispatcher = new Dispatcher { Handler = _ => new JsonObject { ["count"] = "12" } };
        Assert.Equal("13", (await Run(source, registry: registry, dispatcher: dispatcher))!["value"]!.GetValue<string>());
        dispatcher.Handler = _ => new JsonObject { ["count"] = 12 };
        Assert.Equal("CONVERSION_FAILED", (await Assert.ThrowsAsync<BosException>(() => Run(source, registry: registry, dispatcher: dispatcher))).Code);
    }

    [Fact]
    public void Create_capabilities_require_one_unambiguous_type_version()
    {
        var registry = new Registry(); var schema = new JsonObject { ["fields"] = new JsonObject() };
        registry.Register(new Operation(new("Create", 1, "Create", schema, schema, "write", true, [], [new("record.create", "Thing")])));
        var source = Source(new JsonObject { ["id"] = "create", ["call"] = "Create", ["args"] = new JsonObject() }, Return(Object(("value", Literal("ok")))));
        source["mode"] = "command"; source["transaction"] = "atomic";
        source["dependencies"]!["operations"]!["Create"] = 1; source["dependencies"]!["types"]!["Thing"] = new JsonArray(1, 2);
        source["capabilities"] = new JsonArray("record.create:Thing", "operations.execute:Create");
        Assert.Throws<BosException>(() => new FlowCompiler(registry).Compile(source.ToJsonString()));
        source["dependencies"]!["types"]!["Thing"] = new JsonArray(1);
        Assert.NotNull(new FlowCompiler(registry).Compile(source.ToJsonString()));
    }

    [Fact]
    public async Task Actual_count_contract_requires_explicit_integer_conversion()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.PublishTypeAsync(ThingDefinition());
        await workspace.CreateRecordAsync("Thing", new JsonObject { ["quantity"] = "12" });
        var source = RecordSource("records.count", new JsonObject { ["type"] = Literal("Thing") },
            Fn("toString", Fn("add", Fn("parseInteger", Path("result.count")), Integer("1"))));
        var receipt = await PublishAndRun(workspace, source);
        Assert.Equal("succeeded", receipt["status"]!.GetValue<string>());
        Assert.Equal("2", receipt["result"]!["value"]!.GetValue<string>());
        var invalid = RecordSource("records.count", new JsonObject { ["type"] = Literal("Thing") }, Fn("toString", Fn("add", Path("result.count"), Integer("1"))));
        Assert.Throws<BosException>(() => new FlowCompiler(workspace.Registry).Compile(invalid.ToJsonString()));
    }

    [Fact]
    public async Task Projected_list_preserves_missing_fields_without_defaults_and_decodes_selected_numbers()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.PublishTypeAsync(ThingDefinition());
        await workspace.CreateRecordAsync("Thing", new JsonObject { ["quantity"] = "12", ["title"] = "private title" });
        var loop = new JsonObject { ["id"] = "inspect", ["forEach"] = Path("result.items"), ["as"] = "row", ["maxItems"] = 100,
            ["steps"] = new JsonArray(new JsonObject { ["id"] = "missingTitle", ["require"] = Fn("and", Fn("isMissing", Path("row.data.title")),
                Fn("equal", Fn("coalesce", Path("row.data.title"), Literal("fallback")), Literal("fallback"))), ["code"] = "PROJECTION_LEAK", ["message"] = "Unselected title must be missing" }) };
        var source = RecordSource("records.list", new JsonObject { ["type"] = Literal("Thing"), ["query"] = Object(("select", Array(Literal("quantity")))) },
            Fn("toString", Fn("sum", Fn("pluck", Path("result.items"), Literal("data.quantity")))), loop);
        var receipt = await PublishAndRun(workspace, source);
        Assert.Equal("succeeded", receipt["status"]!.GetValue<string>());
        Assert.Equal("12", receipt["result"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Projected_missing_value_cannot_be_returned_as_null_or_a_schema_default()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.PublishTypeAsync(ThingDefinition());
        await workspace.CreateRecordAsync("Thing", new JsonObject { ["quantity"] = "12" });
        var source = RecordSource("records.list", new JsonObject { ["type"] = Literal("Thing"), ["query"] = Object(("select", Array(Literal("quantity")))) }, Literal("unreachable"),
            new JsonObject { ["id"] = "inspect", ["forEach"] = Path("result.items"), ["as"] = "row", ["maxItems"] = 100,
                ["steps"] = new JsonArray(new JsonObject { ["id"] = "missingOutput", ["return"] = Object(("value", Path("row.data.title"))) }) });
        source["outputSchema"]!["fields"]!["value"]!["nullable"] = true;
        var receipt = await PublishAndRun(workspace, source);
        Assert.Equal("failed", receipt["status"]!.GetValue<string>());
        Assert.Equal("MISSING_VALUE", receipt["error"]!["code"]!.GetValue<string>());
        Assert.Equal("missingOutput", receipt["error"]!["stepId"]!.GetValue<string>());
        Assert.Equal("inspect[0]", receipt["error"]!["iterationPath"]!.GetValue<string>());
        Assert.False(receipt.ContainsKey("result"));
    }

    [Fact]
    public async Task Actual_delete_result_is_a_tombstone_summary_with_numeric_revision()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.PublishTypeAsync(ThingDefinition());
        var record = await workspace.CreateRecordAsync("Thing", new JsonObject { ["quantity"] = "12" });
        var args = new JsonObject { ["type"] = Literal("Thing"), ["id"] = Literal(record["id"]!.GetValue<string>()), ["expectedRevision"] = Integer("1") };
        var source = RecordSource("records.delete", args, Fn("toString", Fn("add", Path("result.revision"), Integer("1"))));
        var receipt = await PublishAndRun(workspace, source);
        Assert.Equal("succeeded", receipt["status"]!.GetValue<string>());
        Assert.Equal("3", receipt["result"]!["value"]!.GetValue<string>());
        var invalid = RecordSource("records.delete", args, Path("result.data.title"));
        Assert.Throws<BosException>(() => new FlowCompiler(workspace.Registry).Compile(invalid.ToJsonString()));
    }

    [Fact]
    public async Task Read_only_record_validation_uses_authorization_grants_without_performing_a_write()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.PublishTypeAsync(ThingDefinition());
        var args = new JsonObject { ["type"] = Literal("Thing"), ["data"] = Object(("quantity", Integer("12"))), ["typeVersion"] = Integer("1") };
        var source = RecordSource("records.validate", args, Fn("toString", Path("result.valid")));
        source["capabilities"]!.AsArray().Add("record.update:Thing");
        var before = await workspace.BusinessSnapshotAsync();
        var receipt = await PublishAndRun(workspace, source);
        Assert.Equal("succeeded", receipt["status"]!.GetValue<string>()); Assert.Equal("true", receipt["result"]!["value"]!.GetValue<string>());
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
        source["steps"]![0]!["args"]!["typeVersion"] = Integer("2");
        Assert.Throws<BosException>(() => new FlowCompiler(workspace.Registry).Compile(source.ToJsonString()));
        source["steps"]![0]!["args"]!["typeVersion"] = Integer("1");
        source["steps"]!.AsArray().Last()!["return"] = Object(("value", Fn("toString", Path("result.vlid"))));
        Assert.Throws<BosException>(() => new FlowCompiler(workspace.Registry).Compile(source.ToJsonString()));
    }

    [Fact]
    public async Task Full_get_still_rejects_missing_required_data_instead_of_applying_a_projection_or_default()
    {
        var registry = new OperationRegistry();
        var source = RecordSource("records.get", new JsonObject { ["type"] = Literal("Thing"), ["id"] = Literal("11111111-1111-4111-8111-111111111111") }, Fn("toString", Path("result.data.quantity")));
        var uow = new UnitOfWork(); uow.Docs.Values["Thing:1"] = new JsonObject { ["definition"] = ThingDefinition() };
        var dispatcher = new Dispatcher { Handler = _ => JsonNode.Parse("""{"id":"11111111-1111-4111-8111-111111111111","type":"Thing","typeVersion":1,"revision":1,"deleted":false,"createdAt":"2026-11-03T23:00:00.000Z","createdBy":"principal","updatedAt":"2026-11-03T23:00:00.000Z","updatedBy":"principal","data":{"quantity":"12"}}""") };
        var ast = new FlowCompiler(registry).Compile(source.ToJsonString()).Ast;
        var error = await Assert.ThrowsAsync<BosException>(() => new FlowInterpreter(registry).ExecuteAsync(ast, new(), Context(), uow, dispatcher));
        Assert.Equal("CONVERSION_FAILED", error.Code);
    }

    [Fact]
    public async Task Pinned_operation_binds_nested_metadata_and_opaque_slots_with_the_actual_dispatcher()
    {
        var first = new VersionProbe(1); var second = new VersionProbe(2);
        await using var workspace = await TestWorkspace.CreateAsync(registry => { registry.Register(first); registry.Register(second); });
        var source = Source(new JsonObject { ["id"] = "probe", ["call"] = "VersionProbe", ["args"] = new JsonObject
        { ["request"] = Object(("version", Integer("1")), ("payload", Object(("quantity", Integer("12"))))) }, ["as"] = "result" },
            Return(Object(("value", Fn("toString", Fn("add", Path("result.response.version"), Integer("1")))))));
        source["dependencies"]!["operations"]!["VersionProbe"] = 1; source["capabilities"] = new JsonArray("operations.execute:VersionProbe");
        var receipt = await PublishAndRun(workspace, source);
        Assert.Equal("succeeded", receipt["status"]!.GetValue<string>());
        Assert.Equal("3", receipt["result"]!["value"]!.GetValue<string>());
        Assert.Equal(1, first.Calls); Assert.Equal(0, second.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Declared_custom_operations_require_the_dispatchers_implicit_execute_capability(bool called)
    {
        var registry = new OperationRegistry(); registry.Register(new VersionProbe(1));
        var source = Source(Return(Object(("value", Literal("ok")))));
        if (called) source["steps"]!.AsArray().Insert(0, new JsonObject { ["id"] = "probe", ["call"] = "VersionProbe", ["args"] = new JsonObject
        { ["request"] = Object(("version", Integer("1")), ("payload", Object(("quantity", Integer("12"))))) } });
        source["dependencies"]!["operations"]!["VersionProbe"] = 1;
        Assert.Throws<BosException>(() => new FlowCompiler(registry).Compile(source.ToJsonString()));
    }

    [Theory]
    [InlineData("records.create")]
    [InlineData("records.update")]
    [InlineData("records.delete")]
    [InlineData("records.validate")]
    public void Builtin_record_mutation_and_validation_capabilities_include_read_permission(string operation)
    {
        var source = RecordSource(operation, new JsonObject { ["type"] = Literal("Thing"), ["id"] = Literal("11111111-1111-4111-8111-111111111111"),
            ["expectedRevision"] = Integer("1"), ["data"] = Object(("quantity", Integer("1"))), ["patch"] = Object(("quantity", Integer("1"))) }, Literal("ok"));
        var args = source["steps"]![0]!["args"]!.AsObject();
        foreach (var key in args.Select(x => x.Key).ToArray())
            if (!new OperationRegistry().Resolve(operation, 1).Descriptor.InputSchema["fields"]!.AsObject().ContainsKey(key)) args.Remove(key);
        source["mode"] = "command"; source["transaction"] = "atomic";
        source["capabilities"] = new JsonArray("record." + (operation == "records.validate" ? "update" : operation[8..]) + ":Thing");
        Assert.Throws<BosException>(() => new FlowCompiler(new OperationRegistry()).Compile(source.ToJsonString()));
    }

    [Theory]
    [InlineData("records.create", "data")]
    [InlineData("records.update", "patch")]
    [InlineData("records.validate", "data")]
    public void Opaque_record_slots_still_have_required_object_shapes(string operation, string slot)
    {
        var registry = new OperationRegistry();
        var args = new JsonObject { ["type"] = Literal("Thing") };
        if (operation == "records.update") { args["id"] = Literal("11111111-1111-4111-8111-111111111111"); args["expectedRevision"] = Integer("1"); }
        var source = RecordSource(operation, args, Literal("ok"));
        if (operation == "records.validate") source["capabilities"]!.AsArray().Add("record.update:Thing");
        Assert.Throws<BosException>(() => new FlowCompiler(registry).Compile(source.ToJsonString()));
        source["steps"]![0]!["args"]![slot] = Literal(false);
        Assert.Throws<BosException>(() => new FlowCompiler(registry).Compile(source.ToJsonString()));
        source["steps"]![0]!["args"]![slot] = Object(("quantity", Integer("12")));
        Assert.NotNull(new FlowCompiler(registry).Compile(source.ToJsonString()));
    }

    [Fact]
    public async Task Coalesce_of_an_absent_optional_field_keeps_the_fallbacks_runtime_kind()
    {
        var source = Source(Return(Object(("value", Fn("toString", Fn("add", Fn("coalesce", Path("input.optional"), Integer("1")), Integer("1")))))));
        source["inputSchema"]!["fields"]!["optional"] = new JsonObject { ["kind"] = "string" };
        Assert.Equal("2", (await Run(source))!["value"]!.GetValue<string>());
        Assert.Equal("TYPE_MISMATCH", (await Assert.ThrowsAsync<BosException>(() => Run(source, new JsonObject { ["optional"] = "1" }))).Code);
    }

    [Fact]
    public async Task Gregorian_literal_binding_does_not_depend_on_the_hosts_calendar()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("th-TH");
            var source = Source(Return(Object(("value", Literal("2024-02-29")))));
            source["outputSchema"]!["fields"]!["value"]!["kind"] = "date";
            Assert.Equal("2024-02-29", (await Run(source))!["value"]!.GetValue<string>());
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task Output_defaults_do_not_supply_an_omitted_required_result_or_leak_into_an_optional_result()
    {
        var source = Source(Return(Object()));
        source["outputSchema"]!["fields"]!["value"]!["default"] = "not returned";
        Assert.Throws<BosException>(() => new FlowCompiler(new Registry()).Compile(source.ToJsonString()));
        source["outputSchema"]!["fields"]!["value"]!["required"] = false;
        Assert.False((await Run(source))!.AsObject().ContainsKey("value"));
    }

    [Fact]
    public async Task Nested_iteration_step_metadata_is_bounded_before_the_entry_count_limit()
    {
        var body = new JsonArray(Enumerable.Range(0, 4).Select(i => (JsonNode)new JsonObject { ["id"] = "bind" + i, ["let"] = "local" + i, ["value"] = Literal("x") }).ToArray());
        for (int i = 11; i >= 0; i--)
        {
            string id = ("loop" + i).PadRight(64, 'x');
            body = new JsonArray(new JsonObject { ["id"] = id, ["forEach"] = Array(Enumerable.Range(0, i == 11 ? 100 : 1).Select(_ => Integer("1")).ToArray()),
                ["as"] = "item" + i, ["maxItems"] = 100, ["steps"] = body });
        }
        var source = Source(body[0]!.DeepClone().AsObject(), Return(Object(("value", Literal("ok")))));
        var context = Context();
        Assert.Equal("EXECUTION_LIMIT", (await Assert.ThrowsAsync<BosException>(() => Run(source, context: context))).Code);
        Assert.True(context.Steps.Count < 500);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(new JsonArray(context.Steps.Select(s => s.DeepClone()).ToArray()))) <= 262144);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Declared_metadata_paths_do_not_reclassify_business_integer_names(bool annotated)
    {
        var operation = new MetadataProbe(annotated);
        await using var workspace = await TestWorkspace.CreateAsync(r => r.Register(operation));
        var input = MetadataWire(annotated);
        var expected = MetadataWire(annotated);
        if (annotated) { expected["request"]!["expectedRevision"] = "3"; expected["response"]!["expectedRevision"] = 8; }
        var result = await workspace.OperationAsync("MetadataProbe", input.DeepClone().AsObject());
        Assert.True(JsonNode.DeepEquals(expected, result));
        var report = await workspace.CallAsync("operations.validate", new JsonObject { ["name"] = "MetadataProbe", ["contractVersion"] = 1, ["input"] = input.DeepClone() });
        Assert.True(report["valid"]!.GetValue<bool>());
        var wrong = input.DeepClone().AsObject(); wrong["version"] = 7;
        report = await workspace.CallAsync("operations.validate", new JsonObject { ["name"] = "MetadataProbe", ["contractVersion"] = 1, ["input"] = wrong });
        Assert.False(report["valid"]!.GetValue<bool>());
        wrong = input.DeepClone().AsObject(); wrong["request"]!["expectedRevision"] = annotated ? JsonValue.Create("3") : JsonValue.Create(3);
        report = await workspace.CallAsync("operations.validate", new JsonObject { ["name"] = "MetadataProbe", ["contractVersion"] = 1, ["input"] = wrong });
        Assert.False(report["valid"]!.GetValue<bool>());
        operation.NumericBusinessOutput = true;
        Assert.Equal("OUTPUT_INVALID", (await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("MetadataProbe", input.DeepClone().AsObject()))).Code);
        operation.NumericBusinessOutput = false;
        if (annotated)
        {
            operation.StringMetadataOutput = true;
            Assert.Equal("OUTPUT_INVALID", (await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("MetadataProbe", input.DeepClone().AsObject()))).Code);
            operation.StringMetadataOutput = false;
        }
        JsonObject Meta(int n) => annotated ? Literal(n) : Integer(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var args = new JsonObject
        {
            ["version"] = Integer(long.MaxValue.ToString()), ["revision"] = Integer("-12"), ["expectedRevision"] = Integer("-7"),
            ["data"] = Object(("version", Integer("1000000000000")), ("revision", Integer("-13"))),
            ["request"] = Object(("expectedRevision", Meta(3))), ["response"] = Object(("expectedRevision", Integer("8"))),
            ["lineChanges"] = Array(Object(("expectedRevision", Meta(4)), ("version", Integer("21"))), Object(("expectedRevision", Meta(5)), ("version", Integer("22")))),
            ["serials"] = Array(Meta(6), Meta(7)),
            ["snapshot"] = Object(("id", Literal("business id")), ("type", Literal("business type")), ("typeVersion", Integer("2000000000000")),
                ("revision", Integer("-5")), ("data", Object(("version", Integer("31")))))
        };
        var summary = Fn("concat", Fn("toString", Fn("add", Path("result.version"), Integer("0"))), Literal("|"), Fn("toString", Path("result.revision")),
            Literal("|"), Fn("toString", Path("result.data.version")), Literal("|"), Fn("toString", Path("result.request.expectedRevision")),
            Literal("|"), Fn("toString", Path("result.response.expectedRevision")), Literal("|"), Fn("toString", Fn("sum", Fn("pluck", Path("result.lineChanges"), Literal("expectedRevision")))),
            Literal("|"), Fn("toString", Fn("sum", Fn("pluck", Path("result.lineChanges"), Literal("version")))), Literal("|"), Fn("toString", Fn("sum", Path("result.serials"))),
            Literal("|"), Fn("toString", Path("result.snapshot.data.version")));
        var source = Source(new JsonObject { ["id"] = "probe", ["call"] = "MetadataProbe", ["args"] = args, ["as"] = "result" }, Return(Object(("value", summary))));
        source["dependencies"]!["operations"]!["MetadataProbe"] = 1; source["capabilities"] = new JsonArray("operations.execute:MetadataProbe");
        var receipt = await PublishAndRun(workspace, source);
        Assert.Equal("succeeded", receipt["status"]!.GetValue<string>());
        Assert.Equal("9223372036854775807|-12|1000000000000|3|8|9|43|13|31", receipt["result"]!["value"]!.GetValue<string>());
        // An ordinary field with the old heuristic's name still requires a typed
        // integer expression or quoted business value, even beside annotated paths.
        source["steps"]![0]!["args"]!["expectedRevision"] = Literal(7);
        Assert.Throws<BosException>(() => new FlowCompiler(workspace.Registry).Compile(source.ToJsonString()));
    }

    [Fact]
    public async Task Metadata_defaults_and_literal_numbers_use_the_declared_path_not_the_field_name()
    {
        await using var workspace = await TestWorkspace.CreateAsync(r => r.Register(new MetadataDefaults()));
        var result = await workspace.OperationAsync("MetadataDefaults", new JsonObject());
        Assert.Equal(3, result["serial"]!.GetValue<int>()); Assert.Equal(long.MaxValue.ToString(), result["version"]!.GetValue<string>());
        Assert.False(result.ContainsKey("revision"));
        var source = Source(new JsonObject { ["id"] = "probe", ["call"] = "MetadataDefaults", ["args"] = new JsonObject(), ["as"] = "result" },
            Return(Object(("value", Fn("concat", Fn("toString", Path("result.version")), Literal("|"), Fn("toString", Path("result.serial")), Literal("|"), Fn("toString", Fn("isMissing", Path("result.revision"))))))));
        source["dependencies"]!["operations"]!["MetadataDefaults"] = 1; source["capabilities"] = new JsonArray("operations.execute:MetadataDefaults");
        var receipt = await PublishAndRun(workspace, source);
        Assert.Equal("succeeded", receipt["status"]!.GetValue<string>()); Assert.Equal("9223372036854775807|3|true", receipt["result"]!["value"]!.GetValue<string>());
        source["name"] = "LiteralMetadata"; source["steps"]![0]!["args"]!["serial"] = Literal(4);
        receipt = await PublishAndRun(workspace, source);
        Assert.Equal("succeeded", receipt["status"]!.GetValue<string>()); Assert.Equal("9223372036854775807|4|true", receipt["result"]!["value"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caught_sdk_limit_poisoning_rolls_back_staged_business_writes(bool flow)
    {
        var operation = new CatchSdkLimit();
        await using var workspace = await TestWorkspace.CreateAsync(r => { r.Register(operation); r.Register(new BudgetNoop()); });
        await workspace.PublishTypeAsync(ThingDefinition());
        var record = await workspace.CreateRecordAsync("Thing", new JsonObject { ["quantity"] = "12" });
        string id = record["id"]!.GetValue<string>();
        var before = await workspace.BusinessSnapshotAsync(); var beforeAudit = await workspace.RecordAuditSnapshotAsync(id);
        if (flow)
        {
            var source = Source(new JsonObject { ["id"] = "probe", ["call"] = "CatchSdkLimit", ["args"] = new JsonObject { ["id"] = Literal(id) } }, Return(Object(("value", Literal("unreachable")))));
            source["mode"] = "command"; source["transaction"] = "atomic";
            source["dependencies"]!["operations"]!["CatchSdkLimit"] = 1; source["dependencies"]!["operations"]!["BudgetNoop"] = 1;
            source["dependencies"]!["types"]!["Thing"] = new JsonArray(1);
            source["capabilities"] = new JsonArray("operations.execute:CatchSdkLimit", "operations.execute:BudgetNoop", "record.read:Thing", "record.update:Thing");
            var receipt = await PublishAndRun(workspace, source);
            Assert.Equal("failed", receipt["status"]!.GetValue<string>()); Assert.Equal("OPERATION_FAILED", receipt["error"]!["code"]!.GetValue<string>());
            Assert.Equal("probe", receipt["error"]!["stepId"]!.GetValue<string>());
        }
        else Assert.Equal("INTERNAL_ERROR", (await Assert.ThrowsAsync<BosException>(() => workspace.OperationAsync("CatchSdkLimit", new JsonObject { ["id"] = id }))).Code);
        Assert.True(operation.StagedWrite); Assert.True(operation.PoisonedAtCatch); Assert.Equal(201, operation.CallsAtCatch);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync()); Assert.Equal(beforeAudit, await workspace.RecordAuditSnapshotAsync(id));
    }

    [Fact]
    public async Task One_builtin_record_dispatch_consumes_one_sdk_call()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.PublishTypeAsync(ThingDefinition());
        var record = await workspace.CreateRecordAsync("Thing", new JsonObject { ["quantity"] = "12" });
        await using var uow = await workspace.Factory.BeginReadAsync();
        var context = new InvocationContext { Caller = workspace.Admin, Budget = new() { Deadline = DateTimeOffset.UtcNow.AddSeconds(30) } };
        await new Bos.Identity.Authorization().ReloadAsync(uow, context);
        var result = await workspace.Api.CallAsync("records.get", 1, new JsonObject { ["type"] = "Thing", ["id"] = record["id"]!.DeepClone() }, context, uow);
        Assert.Equal(record["id"]!.GetValue<string>(), result!["id"]!.GetValue<string>()); Assert.Equal(1, context.Budget.Calls);
    }

    [Fact]
    public async Task Sealed_builtin_versions_cannot_replace_or_bypass_the_registered_contract()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.PublishTypeAsync(ThingDefinition());
        var record = await workspace.CreateRecordAsync("Thing", new JsonObject { ["quantity"] = "12" });
        var before = await workspace.BusinessSnapshotAsync();
        var original = workspace.Registry.Resolve("records.get", 1).Descriptor;
        Assert.Throws<InvalidOperationException>(() => workspace.Registry.Register(new Operation(original with { ContractVersion = 2 })));
        Assert.Throws<InvalidOperationException>(() => workspace.Registry.Register(new Operation(original)));
        Assert.Equal("DEPENDENCY_UNAVAILABLE", (await Assert.ThrowsAsync<BosException>(() => workspace.CallAsync("operations.call", new JsonObject
        { ["name"] = "records.get", ["contractVersion"] = 2, ["input"] = new JsonObject { ["type"] = "Thing", ["id"] = record["id"]!.DeepClone() } }))).Code);
        Assert.Equal(1, workspace.Registry.Resolve("records.get", 1).Descriptor.ContractVersion);
        Assert.Equal(before, await workspace.BusinessSnapshotAsync());
    }

    private static JsonObject MetadataWire(bool annotated) => new()
    {
        ["version"] = long.MaxValue.ToString(), ["revision"] = "-12", ["expectedRevision"] = "-7", ["data"] = new JsonObject { ["version"] = "1000000000000", ["revision"] = "-13" },
        ["request"] = new JsonObject { ["expectedRevision"] = annotated ? JsonValue.Create(3) : JsonValue.Create("3") }, ["response"] = new JsonObject { ["expectedRevision"] = "8" },
        ["lineChanges"] = new JsonArray(new JsonObject { ["expectedRevision"] = annotated ? JsonValue.Create(4) : JsonValue.Create("4"), ["version"] = "21" },
            new JsonObject { ["expectedRevision"] = annotated ? JsonValue.Create(5) : JsonValue.Create("5"), ["version"] = "22" }),
        ["serials"] = new JsonArray(annotated ? JsonValue.Create(6) : JsonValue.Create("6"), annotated ? JsonValue.Create(7) : JsonValue.Create("7")),
        ["snapshot"] = new JsonObject { ["id"] = "business id", ["type"] = "business type", ["typeVersion"] = "2000000000000", ["revision"] = "-5", ["data"] = new JsonObject { ["version"] = "31" } }
    };
    private sealed class MetadataProbe(bool annotated) : IOperation
    {
        internal bool NumericBusinessOutput;
        internal bool StringMetadataOutput;
        private static JsonObject DataSchema() => JsonNode.Parse("""{"fields":{"version":{"kind":"integer","required":true},"revision":{"kind":"integer","required":true},"expectedRevision":{"kind":"integer","required":true},"data":{"kind":"object","required":true,"fields":{"version":{"kind":"integer","required":true},"revision":{"kind":"integer","required":true}}},"request":{"kind":"object","required":true,"fields":{"expectedRevision":{"kind":"integer","required":true}}},"response":{"kind":"object","required":true,"fields":{"expectedRevision":{"kind":"integer","required":true}}},"lineChanges":{"kind":"array","required":true,"maxItems":10,"items":{"kind":"object","fields":{"expectedRevision":{"kind":"integer","required":true},"version":{"kind":"integer","required":true}}}},"serials":{"kind":"array","required":true,"maxItems":10,"items":{"kind":"integer"}},"snapshot":{"kind":"object","required":true,"fields":{"id":{"kind":"string","required":true},"type":{"kind":"string","required":true},"typeVersion":{"kind":"integer","required":true},"revision":{"kind":"integer","required":true},"data":{"kind":"object","required":true,"fields":{"version":{"kind":"integer","required":true}}}}}}}""")!.AsObject();
        public OperationDescriptor Descriptor => new("MetadataProbe", 1, "Business integers beside explicit metadata paths", DataSchema(), DataSchema(), "read", true, [], [], annotated ?
            ["input.request.expectedRevision", "input.lineChanges[].expectedRevision", "input.serials[]", "output.response.expectedRevision", "output.lineChanges[].expectedRevision", "output.serials[]"] : null);
        public Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct)
        {
            var result = input.DeepClone().AsObject();
            if (annotated)
            {
                result["request"]!["expectedRevision"] = input["request"]!["expectedRevision"]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture);
                result["response"]!["expectedRevision"] = int.Parse(input["response"]!["expectedRevision"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            }
            if (NumericBusinessOutput) result["version"] = 7;
            if (StringMetadataOutput) result["response"]!["expectedRevision"] = "8";
            return Task.FromResult<JsonNode?>(result);
        }
    }
    private sealed class MetadataDefaults : IOperation
    {
        private static JsonObject DataSchema() => JsonNode.Parse("""{"fields":{"serial":{"kind":"integer","required":true,"default":"3"},"version":{"kind":"integer","required":true,"default":"9223372036854775807"},"revision":{"kind":"integer","default":"-2"}}}""")!.AsObject();
        public OperationDescriptor Descriptor => new("MetadataDefaults", 1, "Explicit metadata defaults", DataSchema(), DataSchema(), "read", true, [], [], ["input.serial", "output.serial"]);
        public Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct) =>
            Task.FromResult<JsonNode?>(new JsonObject { ["serial"] = input["serial"]!.DeepClone(), ["version"] = input["version"]!.DeepClone() });
    }
    private sealed class CatchSdkLimit : IOperation
    {
        internal bool StagedWrite;
        internal bool PoisonedAtCatch;
        internal int CallsAtCatch;
        public OperationDescriptor Descriptor => new("CatchSdkLimit", 1, "Caught SDK failure rollback probe",
            JsonNode.Parse("""{"fields":{"id":{"kind":"string","required":true}}}""")!.AsObject(), JsonNode.Parse("""{"fields":{"caught":{"kind":"boolean","required":true}}}""")!.AsObject(),
            "write", true, [], [new("record.read", "Thing"), new("record.update", "Thing"), new("operations.execute", "BudgetNoop")]);
        public async Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct)
        {
            string id = input["id"]!.GetValue<string>();
            await dispatcher.CallAsync("BudgetNoop", 1, new JsonObject(), context, uow, ct);
            await dispatcher.CallAsync("records.update", 1, new JsonObject { ["type"] = "Thing", ["id"] = id, ["expectedRevision"] = 1, ["patch"] = new JsonObject { ["title"] = "must roll back" } }, context, uow, ct);
            StagedWrite = true;
            try { for (int i = 0; i < 210; i++) await dispatcher.CallAsync("records.get", 1, new JsonObject { ["type"] = "Thing", ["id"] = id }, context, uow, ct); }
            catch (BosException e) when (e.Code == "EXECUTION_LIMIT")
            { PoisonedAtCatch = context.Poisoned; CallsAtCatch = context.Budget.Calls; return new JsonObject { ["caught"] = true }; }
            throw new InvalidOperationException("The SDK call limit was not reached.");
        }
    }
    private sealed class BudgetNoop : IOperation
    {
        public OperationDescriptor Descriptor => new("BudgetNoop", 1, "Nested SDK call", new JsonObject { ["fields"] = new JsonObject() }, new JsonObject { ["fields"] = new JsonObject() }, "read", true, [], []);
        public Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct) => Task.FromResult<JsonNode?>(new JsonObject());
    }

    private static JsonObject ThingDefinition() => JsonNode.Parse("""{"name":"Thing","accessMode":"direct","fields":{"quantity":{"kind":"integer","required":true},"title":{"kind":"string","required":true,"default":"default title"}}}""")!.AsObject();
    private static JsonObject RecordSource(string operation, JsonObject args, JsonObject value, params JsonObject[] between)
    {
        var steps = new List<JsonObject> { new() { ["id"] = "invoke", ["call"] = operation, ["args"] = args.DeepClone(), ["as"] = "result" } };
        steps.AddRange(between); steps.Add(Return(Object(("value", value))));
        var source = Source(steps.ToArray()); source["dependencies"]!["operations"]![operation] = 1;
        source["dependencies"]!["types"]!["Thing"] = new JsonArray(1); source["capabilities"] = new JsonArray("record.read:Thing");
        if (operation is "records.create" or "records.update" or "records.delete")
        { source["mode"] = "command"; source["transaction"] = "atomic"; source["capabilities"]!.AsArray().Add("record." + operation[8..] + ":Thing"); }
        return source;
    }
    private static async Task<JsonObject> PublishAndRun(TestWorkspace workspace, JsonObject source)
    {
        string name = source["name"]!.GetValue<string>();
        await workspace.PublishFlowAsync(name, source.ToJsonString());
        return await workspace.CallAsync("flows.run", new JsonObject { ["name"] = name, ["version"] = 1, ["input"] = new JsonObject() });
    }
    private sealed class VersionProbe(int version) : IOperation
    {
        internal int Calls;
        public OperationDescriptor Descriptor => new("VersionProbe", version, "Metadata and dynamic-slot boundary probe",
            JsonNode.Parse("""{"fields":{"request":{"kind":"object","required":true,"fields":{"version":{"kind":"integer","required":true},"payload":{}}}}}""")!.AsObject(),
            JsonNode.Parse("""{"fields":{"response":{"kind":"object","required":true,"fields":{"version":{"kind":"integer","required":true}}}}}""")!.AsObject(), "read", true, [], [], ["input.request.version", "output.response.version"]);
        public Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct)
        {
            Calls++; Assert.Equal("12", input["request"]!["payload"]!["quantity"]!.GetValue<string>());
            return Task.FromResult<JsonNode?>(new JsonObject { ["response"] = new JsonObject { ["version"] = input["request"]!["version"]!.GetValue<int>() + version } });
        }
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "") => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file)!, "../.."));

    private static JsonObject Source(params JsonObject[] steps) => new()
    {
        ["format"] = "bos.flow/1.0", ["name"] = "Test", ["mode"] = "query", ["transaction"] = "readOnly",
        ["inputSchema"] = new JsonObject { ["fields"] = new JsonObject() },
        ["outputSchema"] = new JsonObject { ["fields"] = new JsonObject { ["value"] = new JsonObject { ["kind"] = "string", ["required"] = true } } },
        ["dependencies"] = new JsonObject { ["types"] = new JsonObject(), ["operations"] = new JsonObject() },
        ["capabilities"] = new JsonArray(), ["steps"] = new JsonArray(steps.Select(x => (JsonNode)x.DeepClone()).ToArray())
    };
    private static JsonObject Return(JsonObject expression) => new() { ["id"] = "output", ["return"] = expression };
    private static JsonObject Literal(object? value) => new() { ["literal"] = value is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(value) };
    private static JsonObject Integer(string value) => new() { ["integer"] = value };
    private static JsonObject Decimal(string value) => new() { ["decimal"] = value };
    private static JsonObject Path(string value) => new() { ["path"] = value };
    private static JsonObject Fn(string name, params JsonObject[] args) => new() { ["fn"] = name, ["args"] = new JsonArray(args.Select(x => (JsonNode)x.DeepClone()).ToArray()) };
    private static JsonObject Array(params JsonObject[] items) => new() { ["array"] = new JsonArray(items.Select(x => (JsonNode)x.DeepClone()).ToArray()) };
    private static JsonObject Object(params (string Name, JsonObject Value)[] fields) => new() { ["object"] = new JsonObject(fields.Select(x => KeyValuePair.Create<string, JsonNode?>(x.Name, x.Value.DeepClone()))) };
    private static InvocationContext Context() => new() { Caller = new("principal"), RunId = "run", StartedAt = DateTimeOffset.Parse("2026-11-03T23:00:00Z"), Budget = new() { Deadline = DateTimeOffset.UtcNow.AddMinutes(1) } };
    private static async Task<JsonNode?> Run(JsonObject source, JsonObject? input = null, Registry? registry = null, Dispatcher? dispatcher = null, InvocationContext? context = null, UnitOfWork? uow = null)
    {
        registry ??= new();
        var ast = new FlowCompiler(registry).Compile(source.ToJsonString()).Ast;
        return await new FlowInterpreter(registry).ExecuteAsync(ast, input ?? new(), context ?? Context(), uow ?? new(), dispatcher ?? new());
    }
    private sealed class Registry : IOperationRegistry
    {
        private readonly Dictionary<string, IOperation> operations = new(StringComparer.Ordinal);
        public IReadOnlyList<OperationDescriptor> Descriptors => operations.Values.Select(x => x.Descriptor).ToArray();
        public void Register(IOperation operation) => operations.Add(operation.Descriptor.Name, operation);
        public IOperation Resolve(string name, int? version = null) => operations.TryGetValue(name, out var op) && (version is null || version == op.Descriptor.ContractVersion) ? op : throw new BosException("NOT_FOUND", "Operation not found");
        internal void Add(string name, JsonObject input, JsonObject output, IReadOnlyList<string>? metadataPaths = null) => Register(new Operation(new(name, 1, name, input, output, "read", true, [], [], metadataPaths)));
    }
    private sealed class Operation(OperationDescriptor descriptor) : IOperation
    {
        public OperationDescriptor Descriptor => descriptor;
        public Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Dispatcher : IDispatcher
    {
        internal int Calls;
        internal Func<JsonObject, JsonNode?> Handler = _ => new JsonObject();
        internal Func<string, JsonObject, JsonNode?>? NamedHandler;
        public Task<JsonNode?> CallAsync(string name, int? version, JsonObject input, InvocationContext context, IUnitOfWork uow, CancellationToken ct = default)
        { Calls++; return Task.FromResult(NamedHandler is null ? Handler(input) : NamedHandler(name, input)); }
    }
    private sealed class Documents : IDocumentRepository
    {
        internal readonly Dictionary<string, JsonObject> Values = new(StringComparer.Ordinal);
        internal readonly List<string> Reads = [];
        public Task<JsonObject?> GetAsync(string collection, string key, CancellationToken ct = default)
        { Reads.Add(collection + "/" + key); return Task.FromResult(Values.TryGetValue(key, out var v) ? (JsonObject?)v.DeepClone() : null); }
        public Task PutAsync(string collection, string key, JsonObject value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string collection, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> ListAsync(string collection, int limit = 1000, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AppendAuditAsync(JsonObject entry, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class UnitOfWork : IUnitOfWork
    {
        internal Documents Docs = new();
        public DbConnection Connection => throw new NotSupportedException();
        public DbTransaction Transaction => throw new NotSupportedException();
        public bool IsWrite => true;
        public IDocumentRepository Documents => Docs;
        public IRecordRepository Records => throw new NotSupportedException("Interpreter must not use the business record repository.");
        public Task CommitAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task RollbackAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
