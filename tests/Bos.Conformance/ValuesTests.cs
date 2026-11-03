using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;
using Xunit;

namespace Bos.Conformance;

public sealed class ValuesTests
{
    private static JsonObject Obj(string json) => (JsonObject)CanonicalJson.Parse(json)!;

    [Theory]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("{\"nested\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"x\":1,\"\\u0078\":2}")]
    [InlineData("{\"é\":1,\"e\\u0301\":2}")]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uDC00\"")]
    [InlineData("\"\\uD800x\"")]
    [InlineData("{\"\\uD800\":1}")]
    [InlineData("[1,]")]
    [InlineData("/* comment */null")]
    [InlineData("null true")]
    [InlineData("1e309")]
    [InlineData("NaN")]
    public void Strict_json_rejects_ambiguous_or_malformed_input(string json)
    {
        Assert.Equal("INVALID_REQUEST", Assert.Throws<BosException>(() => CanonicalJson.Parse(json)).Code);
        Assert.Equal("INVALID_REQUEST", Assert.Throws<BosException>(() => CanonicalJson.Parse(json, preserveSecrets: true)).Code);
        Assert.Equal("INVALID_REQUEST", Assert.Throws<BosException>(() => CanonicalJson.Parse(json, preserveSource: true)).Code);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Endpoint_and_document_parses_preserve_raw_flow_source(bool preserveSecrets, bool preserveSource)
    {
        const string raw = "# e\u0301 😀\r\nname: Test\r\n  \n";
        const string json = """{"source":"# e\u0301 😀\r\nname: Test\r\n  \n","business":"e\u0301","e\u0301":"e\u0301","nested":[{"source":"# e\u0301 😀\r\nname: Test\r\n  \n"}]}""";
        var parsed = (JsonObject)CanonicalJson.Parse(json, preserveSecrets, preserveSource)!;
        Assert.Equal(raw, parsed["source"]!.GetValue<string>());
        Assert.Equal(raw, parsed["nested"]![0]!["source"]!.GetValue<string>());
        Assert.Equal("é", parsed["business"]!.GetValue<string>());
        Assert.Equal("é", parsed["é"]!.GetValue<string>());
        Assert.Equal(raw.Normalize(), CanonicalJson.Parse(json)!["source"]!.GetValue<string>());
    }

    [Fact]
    public void Source_document_roundtrip_preserves_original_utf8_and_normalizes_other_values()
    {
        const string raw = "# e\u0301 😀\r\nname: Test\n  \n";
        var document = new JsonObject
        {
            ["definition"] = new JsonObject { ["source"] = raw },
            ["source"] = raw,
            ["description"] = "e\u0301",
            ["password"] = "e\u0301",
            ["e\u0301"] = "e\u0301"
        };
        var stored = CanonicalJson.Serialize(document, preserveSource: true);
        var loaded = CanonicalJson.Parse(stored, preserveSource: true)!;
        Assert.Equal(raw, loaded["source"]!.GetValue<string>());
        Assert.Equal(raw, loaded["definition"]!["source"]!.GetValue<string>());
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(raw), System.Text.Encoding.UTF8.GetBytes(loaded["source"]!.GetValue<string>()));
        Assert.Equal("é", loaded["description"]!.GetValue<string>());
        Assert.Equal("é", loaded["password"]!.GetValue<string>());
        Assert.Equal("é", loaded["é"]!.GetValue<string>());
        Assert.Equal(raw, document["source"]!.GetValue<string>());
        Assert.Equal(raw.Normalize(), CanonicalJson.Parse(CanonicalJson.Serialize(document), preserveSource: true)!["source"]!.GetValue<string>());
        Assert.Equal(raw.Normalize(), CanonicalJson.Normalize(document)!["source"]!.GetValue<string>());
        Assert.Equal(CanonicalJson.Hash(document), CanonicalJson.Hash(CanonicalJson.Normalize(document)));
        Assert.NotEqual(CanonicalJson.Serialize(document), stored);
    }

    [Fact]
    public void Source_preservation_matches_only_exact_scalar_properties_and_does_not_preserve_secrets()
    {
        var json = """{"Source":"e\u0301","sourceHash":"e\u0301","source":["e\u0301",{"source":"e\u0301"}],"definition":{"sou\u0072ce":"e\u0301"},"password":"e\u0301"}""";
        var parsed = CanonicalJson.Parse(json, preserveSource: true)!;
        Assert.Equal("é", parsed["Source"]!.GetValue<string>());
        Assert.Equal("é", parsed["sourceHash"]!.GetValue<string>());
        Assert.Equal("é", parsed["source"]![0]!.GetValue<string>());
        Assert.Equal("e\u0301", parsed["source"]![1]!["source"]!.GetValue<string>());
        Assert.Equal("e\u0301", parsed["definition"]!["source"]!.GetValue<string>());
        Assert.Equal("é", parsed["password"]!.GetValue<string>());
        var roundtrip = CanonicalJson.Parse(CanonicalJson.Serialize(parsed, preserveSource: true), preserveSource: true)!;
        Assert.Equal(CanonicalJson.Serialize(parsed, preserveSource: true), CanonicalJson.Serialize(roundtrip, preserveSource: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Source_preservation_keeps_unicode_duplicate_and_body_validation(bool preserveSecrets, bool preserveSource)
    {
        Assert.Throws<BosException>(() => CanonicalJson.Parse("""{"source":"\uD800"}""", preserveSecrets, preserveSource));
        Assert.Throws<BosException>(() => CanonicalJson.Parse("{\"source\":\"" + '\ud800' + "\"}", preserveSecrets, preserveSource));
        Assert.Throws<BosException>(() => CanonicalJson.Parse("""{"source":"first","sou\u0072ce":"second"}""", preserveSecrets, preserveSource));
        Assert.Equal(413, Assert.Throws<BosException>(() => CanonicalJson.Parse("{\"source\":\"" + new string('é', CanonicalJson.MaxBytes / 2) + "\"}", preserveSecrets, preserveSource)).Status);
        Assert.Throws<BosException>(() => CanonicalJson.Parse(new string('[', 65) + "0" + new string(']', 65), preserveSecrets, preserveSource));
        Assert.Throws<BosException>(() => CanonicalJson.Serialize(new JsonObject { ["source"] = new string('\ud800', 1) }, preserveSource: true));
        Assert.Throws<BosException>(() => CanonicalJson.Serialize(new JsonObject { ["source"] = JsonNode.Parse("\"\\uD800\"") }, preserveSource: true));
        Assert.Throws<BosException>(() => CanonicalJson.Serialize(new JsonObject { ["é"] = 1, ["e\u0301"] = 2 }, preserveSource: true));
        Assert.Equal(413, Assert.Throws<BosException>(() => CanonicalJson.Serialize(new JsonObject { ["source"] = new string('é', CanonicalJson.MaxBytes / 2) }, preserveSource: true)).Status);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("currentPassword")]
    [InlineData("newPassword")]
    public void Authentication_parse_preserves_secret_values_but_normalizes_business_strings(string property)
    {
        var json = "{\"" + property + "\":\" e\\u0301 😀 \",\"business\":\"e\\u0301\",\"e\\u0301\":\"e\\u0301\",\"nested\":[{\"" + property + "\":\" e\\u0301 😀 \"}]}";
        var parsed = (JsonObject)CanonicalJson.Parse(json, preserveSecrets: true)!;
        Assert.Equal(" e\u0301 😀 ", JsonArgs.Secret(parsed, property));
        Assert.Equal(" e\u0301 😀 ", JsonArgs.Secret((JsonObject)parsed["nested"]![0]!, property));
        Assert.Equal("é", parsed["business"]!.GetValue<string>());
        Assert.Equal("é", parsed["é"]!.GetValue<string>());
        Assert.Equal(" é 😀 ", JsonArgs.Secret((JsonObject)CanonicalJson.Parse(json)!, property));
        // Canonical serialization still normalizes every string without mutating parsed secrets.
        Assert.Equal(" é 😀 ", JsonArgs.Secret((JsonObject)CanonicalJson.Parse(CanonicalJson.Serialize(parsed), preserveSecrets: true)!, property));
        Assert.Equal(" e\u0301 😀 ", JsonArgs.Secret(parsed, property));
    }

    [Fact]
    public void Secret_preservation_matches_exact_scalar_property_names_only()
    {
        var parsed = (JsonObject)CanonicalJson.Parse("""{"Password":"e\u0301","passwordHint":"e\u0301","password":["e\u0301"],"currentPassword":{"text":"e\u0301"},"new\u0050assword":"e\u0301"}""", preserveSecrets: true)!;
        Assert.Equal("é", parsed["Password"]!.GetValue<string>());
        Assert.Equal("é", parsed["passwordHint"]!.GetValue<string>());
        Assert.Equal("é", parsed["password"]![0]!.GetValue<string>());
        Assert.Equal("é", parsed["currentPassword"]!["text"]!.GetValue<string>());
        Assert.Equal("e\u0301", JsonArgs.Secret(parsed, "newPassword"));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("currentPassword")]
    [InlineData("newPassword")]
    public void Secret_preservation_still_rejects_invalid_unicode_and_duplicate_keys(string property)
    {
        Assert.Throws<BosException>(() => CanonicalJson.Parse("{\"" + property + "\":\"\\uD800\"}", preserveSecrets: true));
        Assert.Throws<BosException>(() => CanonicalJson.Parse("{\"" + property + "\":\"" + '\ud800' + "\"}", preserveSecrets: true));
        Assert.Throws<BosException>(() => CanonicalJson.Parse("{\"" + property + "\":\"first\",\"" + property + "\":\"second\"}", preserveSecrets: true));
    }

    [Fact]
    public void Secret_binder_requires_a_raw_unicode_valid_string_and_leaves_length_policy_to_callers()
    {
        var args = new JsonObject { ["raw"] = " e\u0301 😀 ", ["empty"] = "", ["long"] = new string('x', 129), ["invalid"] = new string('\ud800', 1), ["invalidEscaped"] = JsonNode.Parse("\"\\uD800\""), ["nil"] = null, ["number"] = 1 };
        Assert.Equal(" e\u0301 😀 ", JsonArgs.Secret(args, "raw"));
        Assert.Equal("", JsonArgs.Secret(args, "empty"));
        Assert.Equal(129, JsonArgs.Secret(args, "long").Length);
        foreach (var name in new[] { "missing", "nil", "number", "invalid", "invalidEscaped" })
        {
            var error = Assert.Throws<BosException>(() => JsonArgs.Secret(args, name));
            Assert.Equal("INVALID_REQUEST", error.Code);
            Assert.Equal(name, error.Path);
        }
    }

    [Fact]
    public void Strict_json_checks_bytes_depth_and_raw_unicode()
    {
        Assert.Equal(413, Assert.Throws<BosException>(() => CanonicalJson.Parse("\"" + new string('é', CanonicalJson.MaxBytes / 2) + "\"")).Status);
        Assert.Throws<BosException>(() => CanonicalJson.Parse(new string('[', 65) + "0" + new string(']', 65)));
        Assert.Throws<BosException>(() => CanonicalJson.Parse("\"" + '\ud800' + "\""));
        Assert.Equal("😀", CanonicalJson.Parse("\"\\uD83D\\uDE00\"")!.GetValue<string>());
        Assert.Equal("\\uD800", CanonicalJson.Parse("\"\\\\uD800\"")!.GetValue<string>());
    }

    [Fact]
    public void Canonical_json_sorts_normalizes_and_hashes_without_mutating_input()
    {
        var value = new JsonObject { ["z"] = "e\u0301", ["a"] = new JsonArray(2, 1), ["empty"] = null };
        Assert.Equal("{\"a\":[2,1],\"empty\":null,\"z\":\"é\"}", CanonicalJson.Serialize(value));
        Assert.Equal("e\u0301", value["z"]!.GetValue<string>());
        Assert.Equal(CanonicalJson.Hash(value), CanonicalJson.Hash(Obj("{\"empty\":null,\"z\":\"é\",\"a\":[2,1]}")));
        Assert.NotEqual(CanonicalJson.Hash(Obj("{}")), CanonicalJson.Hash(Obj("{\"x\":null}")));
        Assert.Equal("[1,0,125]", CanonicalJson.Serialize(CanonicalJson.Parse("[1.00,-0,1.25e2]")));
        Assert.Throws<BosException>(() => CanonicalJson.Serialize(new JsonObject { ["é"] = 1, ["e\u0301"] = 2 }));
    }

    [Fact]
    public void Numeric_values_use_exact_scaled_integers_and_literal_defaults()
    {
        var schema = Obj("""{"fields":{"amount":{"kind":"decimal","precision":28,"scale":2,"required":true},"units":{"kind":"integer","default":"+00012"}}}""");
        var input = Obj("""{"amount":"+00099999999999999999999999999.99"}""");
        var normalized = Schema.Normalize(schema, input);
        Assert.Equal("99999999999999999999999999.99", normalized["amount"]!.GetValue<string>());
        Assert.Equal("12", normalized["units"]!.GetValue<string>());
        Assert.False(input.ContainsKey("units"));
        Assert.False(Schema.ValidateData(schema, Obj("""{"amount":"100000000000000000000000000.00"}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"amount":"1.000"}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"amount":1.00}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"amount":"1e2"}""")).Valid);
        Assert.Equal("0.00", Schema.Normalize(schema, Obj("""{"amount":"-0"}"""))["amount"]!.GetValue<string>());
        Assert.Equal("-9223372036854775808", TypedIndex.Canonical("integer", JsonValue.Create("-09223372036854775808")!));
        Assert.Throws<BosException>(() => TypedIndex.Key("integer", JsonValue.Create("9223372036854775808")!));
    }

    [Fact]
    public void Numeric_bounds_are_exact_at_the_twenty_eighth_digit()
    {
        var schema = Obj("""{"fields":{"v":{"kind":"decimal","precision":28,"scale":0,"minimum":"9999999999999999999999999998","maximum":"9999999999999999999999999998"}}}""");
        Assert.True(Schema.ValidateData(schema, Obj("""{"v":"9999999999999999999999999998"}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"v":"9999999999999999999999999999"}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"v":"9999999999999999999999999997"}""")).Valid);
    }

    [Fact]
    public void Required_nullable_and_default_are_independent()
    {
        var schema = Obj("""{"fields":{"requiredNull":{"kind":"string","required":true,"nullable":true},"optional":{"kind":"string"},"defaults":{"kind":"object","default":{},"fields":{"flag":{"kind":"boolean","default":true}}}}}""");
        Assert.False(Schema.ValidateData(schema, Obj("{}")).Valid);
        var result = Schema.Normalize(schema, Obj("""{"requiredNull":null}"""));
        Assert.True(result.ContainsKey("requiredNull"));
        Assert.Null(result["requiredNull"]);
        Assert.False(result.ContainsKey("optional"));
        Assert.True(result["defaults"]!["flag"]!.GetValue<bool>());
        Assert.False(Schema.ValidateData(schema, Obj("""{"requiredNull":null,"optional":null}""")).Valid);
        Assert.False(Schema.Normalize(schema, Obj("""{"requiredNull":null}"""), false).ContainsKey("defaults"));
        result["defaults"]!["flag"] = false;
        Assert.True(Schema.Normalize(schema, Obj("""{"requiredNull":null}"""))["defaults"]!["flag"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("{\"fields\":{\"x\":{\"kind\":\"integer\",\"maxLength\":2}}}")]
    [InlineData("{\"fields\":{\"x\":{\"kind\":\"decimal\",\"precision\":2,\"scale\":3}}}")]
    [InlineData("{\"fields\":{\"x\":{\"kind\":\"string\",\"required\":null}}}")]
    [InlineData("{\"fields\":{\"x\":{\"kind\":\"integer\",\"minimum\":2}}}")]
    [InlineData("{\"fields\":{\"x\":{\"kind\":\"integer\",\"minimum\":\"2\",\"maximum\":\"1\"}}}")]
    [InlineData("{\"fields\":{\"x\":{\"kind\":\"array\",\"maxItems\":201,\"items\":{\"kind\":\"string\"}}}}")]
    [InlineData("{\"fields\":{\"x\":{\"kind\":\"enum\",\"values\":[\"é\",\"e\\u0301\"]}}}")]
    [InlineData("{\"fields\":{\"x\":{\"kind\":\"integer\",\"default\":null}}}")]
    [InlineData("{\"fields\":{\"_private\":{\"kind\":\"string\"}}}")]
    public void Field_grammar_is_closed_and_defaults_use_the_same_codecs(string definition)
    {
        Assert.False(Schema.ValidateDefinition(Obj(definition), true).Valid);
    }

    [Fact]
    public void Persistence_properties_are_forbidden_in_data_schemas_and_nested_indexes()
    {
        var record = Obj("""{"name":"Thing","accessMode":"operationsOnly","allowedOperations":["CreateThing"],"fields":{"value":{"kind":"string","unique":true,"managedBy":["CreateThing"]}}}""");
        Assert.True(Schema.ValidateDefinition(record).Valid);
        Assert.False(Schema.ValidateDefinition(Obj("""{"fields":{"x":{"kind":"string","indexed":false}}}"""), true).Valid);
        Assert.False(Schema.ValidateDefinition(Obj("""{"fields":{"x":{"kind":"string","managedBy":[]}}}"""), true).Valid);
        ((JsonObject)record["fields"]!["value"]!)["indexed"] = false;
        Assert.False(Schema.ValidateDefinition(record).Valid);
        Assert.False(Schema.ValidateDefinition(Obj("""{"name":"Thing","fields":{"x":{"kind":"object","fields":{"y":{"kind":"string","indexed":true}}}}}""")).Valid);
        Assert.False(Schema.ValidateDefinition(Obj("""{"name":"Thing","fields":{"x":{"kind":"reference","targetType":"Thing","unique":true}}}""")).Valid);
    }

    [Fact]
    public void Nested_values_report_canonical_paths_without_echoing_payloads()
    {
        var schema = Obj("""{"fields":{"lines":{"kind":"array","maxItems":2,"items":{"kind":"object","fields":{"quantity":{"kind":"integer","required":true}}}}}}""");
        var report = Schema.ValidateData(schema, Obj("""{"lines":[{"quantity":"secret-value"}],"unknown":"secret-value"}"""));
        Assert.Contains(report.Errors, e => e.Path == "lines[0].quantity");
        Assert.Contains(report.Errors, e => e.Path == "unknown" && e.Code == "UNKNOWN_FIELD");
        Assert.All(report.Errors, e => Assert.DoesNotContain("secret-value", e.Message));
    }

    [Fact]
    public void Dates_instants_references_and_nfc_are_normalized_strictly()
    {
        var schema = Obj("""{"fields":{"date":{"kind":"date"},"instant":{"kind":"instant"},"ref":{"kind":"reference","targetType":"Thing"},"text":{"kind":"string","maxLength":1}}}""");
        var input = Obj("""{"date":"2024-02-29","instant":"2026-11-03T12:30:00.12+05:30","ref":"a4d7b42d-a26b-4f47-a890-1a3c3889fc26","text":"e\u0301"}""");
        var result = Schema.Normalize(schema, input);
        Assert.Equal("2026-11-03T07:00:00.120Z", result["instant"]!.GetValue<string>());
        Assert.Equal("é", result["text"]!.GetValue<string>());
        Assert.False(Schema.ValidateData(schema, Obj("""{"date":"2023-02-29"}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"instant":"2026-11-03T00:00:00.0000Z"}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"instant":"2026-11-03T00:00:00"}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"ref":"A4D7B42D-A26B-4F47-A890-1A3C3889FC26"}""")).Valid);
    }

    public static IEnumerable<object[]> StringFormatCases()
    {
        yield return ["email", "person@example.com", "person@example.com", "person@localhost"];
        yield return ["phone", "+14155552671", "+14155552671", "14155552671"];
        yield return ["url", "https://example.com/a", "https://example.com/a", "https://user@example.com"];
        yield return ["uuid", "A4D7B42D-A26B-4F47-A890-1A3C3889FC26", "a4d7b42d-a26b-4f47-a890-1a3c3889fc26", "not-a-uuid"];
        yield return ["uri", "mailto:person@example.com", "mailto:person@example.com", "relative/path"];
        yield return ["slug", "lower-case-1", "lower-case-1", "Upper"];
        yield return ["ipAddress", "2001:db8::1", "2001:db8::1", "999.1.1.1"];
        yield return ["hostname", "api.example.com", "api.example.com", "-api.example.com"];
        yield return ["countryCode", "us", "US", "USA"];
        yield return ["currencyCode", "usd", "USD", "US"];
        yield return ["locale", "en-US", "en-US", "not_a_locale"];
        yield return ["timeZone", "Etc/UTC", "Etc/UTC", "Not/AZone"];
    }

    [Theory]
    [MemberData(nameof(StringFormatCases))]
    public void String_formats_validate_and_only_documented_formats_canonicalize(string format, string input, string expected, string invalid)
    {
        var schema = new JsonObject { ["fields"] = new JsonObject { ["v"] = new JsonObject { ["kind"] = "string", ["minLength"] = 1, ["maxLength"] = 255, ["format"] = format } } };
        Assert.Equal(expected, Schema.Normalize(schema, new JsonObject { ["v"] = input })["v"]!.GetValue<string>());
        Assert.False(Schema.ValidateData(schema, new JsonObject { ["v"] = invalid }).Valid);
    }

    [Fact]
    public void String_patterns_are_bounded_safe_and_applied_after_normalization()
    {
        var schema = Obj("""{"fields":{"v":{"kind":"string","minLength":2,"maxLength":3,"pattern":"^[A-Z]+$"}}}""");
        Assert.True(Schema.ValidateData(schema, Obj("""{"v":"AB"}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"v":"A"}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"v":"Ab"}""")).Valid);
        Assert.False(Schema.ValidateDefinition(Obj("""{"fields":{"v":{"kind":"string","pattern":"^(a)\\1$"}}}"""), true).Valid);
        var longPattern = new string('a', 257);
        Assert.False(Schema.ValidateDefinition(new JsonObject { ["fields"] = new JsonObject { ["v"] = new JsonObject { ["kind"] = "string", ["pattern"] = longPattern } } }, true).Valid);
    }

    [Fact]
    public void Exclusive_bounds_and_multiple_of_use_exact_arithmetic()
    {
        var schema = Obj("""{"fields":{"i":{"kind":"integer","exclusiveMinimum":"0","exclusiveMaximum":"10","multipleOf":"3"},"d":{"kind":"decimal","precision":8,"scale":3,"minimum":"0.001","maximum":"9.999","multipleOf":"0.125"}}}""");
        var normalized = Schema.Normalize(schema, Obj("""{"i":"+0009","d":"1.250"}"""));
        Assert.Equal("9", normalized["i"]!.GetValue<string>());
        Assert.Equal("1.250", normalized["d"]!.GetValue<string>());
        foreach (var invalid in new[] { "0", "10", "4" }) Assert.False(Schema.ValidateData(schema, new JsonObject { ["i"] = invalid, ["d"] = "1.250" }).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"i":"3","d":"1.251"}""")).Valid);
        Assert.False(Schema.ValidateDefinition(Obj("""{"fields":{"v":{"kind":"integer","minimum":"0","exclusiveMinimum":"0"}}}"""), true).Valid);
        Assert.False(Schema.ValidateDefinition(Obj("""{"fields":{"v":{"kind":"integer","multipleOf":"0"}}}"""), true).Valid);
    }

    public static IEnumerable<object[]> TemporalCases()
    {
        yield return ["time", "12:03:04.5", "12:03:04.500", "24:00:00"];
        yield return ["localDateTime", "2024-02-29T12:03:04.12", "2024-02-29T12:03:04.120", "2023-02-29T12:03:04"];
        yield return ["duration", "P1DT25H2M3.4S", "P2DT1H2M3.4S", "P1Y"];
        yield return ["calendarPeriod", "P01Y02M00D", "P1Y2M", "P1Y1H"];
        yield return ["offsetDateTime", "2024-02-29T12:03:04Z", "2024-02-29T12:03:04.000+00:00", "2024-02-29T12:03:04"];
    }

    [Theory]
    [MemberData(nameof(TemporalCases))]
    public void New_temporal_scalars_have_invariant_millisecond_canonical_forms(string kind, string input, string expected, string invalid)
    {
        var schema = new JsonObject { ["name"] = "Thing", ["fields"] = new JsonObject { ["v"] = new JsonObject { ["kind"] = kind, ["indexed"] = true } } };
        Assert.Equal(expected, Schema.Normalize(schema, new JsonObject { ["v"] = input })["v"]!.GetValue<string>());
        Assert.False(Schema.ValidateData(schema, new JsonObject { ["v"] = invalid }).Valid);
        Assert.Equal(expected, TypedIndex.Canonical(kind, JsonValue.Create(input)!));
    }

    [Fact]
    public void Arrays_maps_and_tagged_unions_normalize_recursively_and_enforce_structure()
    {
        var schema = Obj("""
        {"name":"Thing","fields":{
            "items":{"kind":"array","minItems":1,"maxItems":3,"uniqueItems":true,"items":{"kind":"string"}},
            "map":{"kind":"map","minProperties":1,"maxProperties":2,"maxKeyLength":3,"keyPattern":"^[a-z]+$","values":{"kind":"integer"}},
            "choice":{"kind":"taggedUnion","variants":{"Text":{"kind":"string"},"Count":{"kind":"integer"}}}
        }}
        """);
        var normalized = Schema.Normalize(schema, Obj("""{"items":["e\u0301"],"map":{"a":"+01"},"choice":{"tag":"Count","value":"+02"}}"""));
        Assert.Equal("é", normalized["items"]![0]!.GetValue<string>());
        Assert.Equal("1", normalized["map"]!["a"]!.GetValue<string>());
        Assert.Equal("2", normalized["choice"]!["value"]!.GetValue<string>());
        Assert.False(Schema.ValidateData(schema, Obj("""{"items":["é","e\u0301"],"map":{"a":"1"},"choice":{"tag":"Text","value":"ok"}}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"items":["x"],"map":{"TOOLONG":"1"},"choice":{"tag":"Other","value":"x"}}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"items":["x"],"map":{"a":"1"},"choice":{"tag":"Text","value":"x","extra":true}}""")).Valid);
    }

    [Fact]
    public void Business_values_are_exact_bounded_and_canonical()
    {
        var schema = Obj("""
        {"name":"Thing","fields":{
            "money":{"kind":"money","precision":8,"scale":2,"minimum":"0","currencies":["USD","EUR"]},
            "percentage":{"kind":"percentage","precision":5,"scale":2,"indexed":true},
            "quantity":{"kind":"quantity","precision":8,"scale":3,"minimum":"0","units":["kg","lb"]},
            "point":{"kind":"geopoint"},
            "dates":{"kind":"dateRange"},
            "instants":{"kind":"instantRange"}
        }}
        """);
        var value = Obj("""{"money":{"amount":"1.2","currency":"usd"},"percentage":"12.5","quantity":{"value":"2","unit":"kg"},"point":{"latitude":"1.2","longitude":"-2.3"},"dates":{"start":"2024-01-01","end":"2024-01-02"},"instants":{"start":"2024-01-01T01:00:00+01:00","end":"2024-01-01T00:00:01Z"}}""");
        var normalized = Schema.Normalize(schema, value);
        Assert.Equal("1.20", normalized["money"]!["amount"]!.GetValue<string>()); Assert.Equal("USD", normalized["money"]!["currency"]!.GetValue<string>());
        Assert.Equal("12.50", normalized["percentage"]!.GetValue<string>()); Assert.Equal("2.000", normalized["quantity"]!["value"]!.GetValue<string>());
        Assert.Equal("1.200000", normalized["point"]!["latitude"]!.GetValue<string>());
        Assert.Equal(TypedIndex.Key("decimal", JsonValue.Create("12.50")!), TypedIndex.Key("percentage", normalized["percentage"]!));
        Assert.False(Schema.ValidateData(schema, Obj("""{"percentage":"100.01"}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"money":{"amount":"1","currency":"JPY"}}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"point":{"latitude":"90.000001","longitude":"0"}}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"dates":{"start":"2024-01-02","end":"2024-01-01"}}""")).Valid);
    }

    [Fact]
    public void Multi_enums_and_new_references_use_exact_wire_forms()
    {
        const string id = "a4d7b42d-a26b-4f47-a890-1a3c3889fc26";
        var schema = Obj("""
        {"fields":{
            "set":{"kind":"multiEnum","values":["B","A","C"],"minItems":1,"maxItems":2},
            "poly":{"kind":"polymorphicReference","targetTypes":["Thing","Other"]},
            "versioned":{"kind":"versionedReference","targetType":"Thing"},
            "user":{"kind":"userReference"},"file":{"kind":"attachment"}
        }}
        """);
        var normalized = Schema.Normalize(schema, Obj("""{"set":["B","A"],"poly":{"type":"Thing","id":"A4D7B42D-A26B-4F47-A890-1A3C3889FC26"},"versioned":{"id":"A4D7B42D-A26B-4F47-A890-1A3C3889FC26","revision":2},"user":"A4D7B42D-A26B-4F47-A890-1A3C3889FC26","file":"A4D7B42D-A26B-4F47-A890-1A3C3889FC26"}"""));
        Assert.Equal(new[] { "A", "B" }, normalized["set"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(id, normalized["poly"]!["id"]!.GetValue<string>()); Assert.Equal(id, normalized["versioned"]!["id"]!.GetValue<string>());
        Assert.Equal(id, normalized["user"]!.GetValue<string>()); Assert.Equal(id, normalized["file"]!.GetValue<string>());
        Assert.False(Schema.ValidateData(schema, Obj("""{"set":["A","A"]}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"poly":{"type":"Missing","id":"a4d7b42d-a26b-4f47-a890-1a3c3889fc26"}}""")).Valid);
        Assert.False(Schema.ValidateData(schema, Obj("""{"versioned":{"id":"a4d7b42d-a26b-4f47-a890-1a3c3889fc26","revision":"2"}}""")).Valid);
    }

    [Theory]
    [InlineData("{\"fields\":{\"v\":{\"kind\":\"string\",\"format\":\"unknown\"}}}")]
    [InlineData("{\"fields\":{\"v\":{\"kind\":\"string\",\"minLength\":2,\"maxLength\":1}}}")]
    [InlineData("{\"fields\":{\"v\":{\"kind\":\"array\",\"minItems\":2,\"maxItems\":1,\"items\":{\"kind\":\"string\"}}}}")]
    [InlineData("{\"fields\":{\"v\":{\"kind\":\"map\",\"values\":{\"kind\":\"string\"}}}}")]
    [InlineData("{\"fields\":{\"v\":{\"kind\":\"taggedUnion\",\"variants\":{}}}}")]
    [InlineData("{\"fields\":{\"v\":{\"kind\":\"money\",\"precision\":8,\"scale\":2,\"currencies\":[]}}}")]
    [InlineData("{\"fields\":{\"v\":{\"kind\":\"percentage\",\"precision\":5}}}")]
    [InlineData("{\"fields\":{\"v\":{\"kind\":\"quantity\",\"precision\":8,\"scale\":2,\"units\":[]}}}")]
    [InlineData("{\"fields\":{\"v\":{\"kind\":\"multiEnum\",\"values\":[\"A\",\"A\"],\"maxItems\":2}}}")]
    [InlineData("{\"fields\":{\"v\":{\"kind\":\"polymorphicReference\",\"targetTypes\":[\"Thing\",\"Thing\"]}}}")]
    [InlineData("{\"fields\":{\"v\":{\"kind\":\"versionedReference\"}}}")]
    public void Extended_field_definitions_are_closed_and_bounded(string definition)
        => Assert.False(Schema.ValidateDefinition(Obj(definition), true).Valid);

    [Fact]
    public void Extended_defaults_use_the_same_recursive_canonicalization()
    {
        var schema = Obj("""
        {"fields":{
            "map":{"kind":"map","maxProperties":2,"values":{"kind":"integer"},"default":{"a":"+01"}},
            "choice":{"kind":"taggedUnion","variants":{"Count":{"kind":"integer"}},"default":{"tag":"Count","value":"+02"}},
            "set":{"kind":"multiEnum","values":["B","A"],"maxItems":2,"default":["B","A"]},
            "versioned":{"kind":"versionedReference","targetType":"Thing","default":{"id":"A4D7B42D-A26B-4F47-A890-1A3C3889FC26","revision":1}}
        }}
        """);
        Assert.True(Schema.ValidateDefinition(schema, true).Valid);
        var normalized = Schema.Normalize(schema, new JsonObject());
        Assert.Equal("1", normalized["map"]!["a"]!.GetValue<string>());
        Assert.Equal("2", normalized["choice"]!["value"]!.GetValue<string>());
        Assert.Equal(new[] { "A", "B" }, normalized["set"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("a4d7b42d-a26b-4f47-a890-1a3c3889fc26", normalized["versioned"]!["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("map", "\"values\":{\"kind\":\"string\"},\"maxProperties\":2")]
    [InlineData("taggedUnion", "\"variants\":{\"A\":{\"kind\":\"string\"}}")]
    [InlineData("money", "\"precision\":8,\"scale\":2")]
    [InlineData("multiEnum", "\"values\":[\"A\"],\"maxItems\":1")]
    [InlineData("polymorphicReference", "\"targetTypes\":[\"Thing\"]")]
    [InlineData("versionedReference", "\"targetType\":\"Thing\"")]
    [InlineData("userReference", "")]
    [InlineData("attachment", "")]
    public void Composite_and_new_reference_kinds_reject_indexes(string kind, string properties)
    {
        var separator = properties.Length == 0 ? "" : "," + properties;
        Assert.False(Schema.ValidateDefinition(Obj("{\"name\":\"Thing\",\"fields\":{\"v\":{\"kind\":\"" + kind + "\",\"indexed\":true" + separator + "}}}"), false).Valid);
    }

    [Fact]
    public void Numeric_index_keys_sort_by_exact_numeric_value_in_both_directions()
    {
        string[] numbers = ["-9999999999999999999999999999", "-100", "-2.01", "-2", "-0.000000000001", "0", "0.000000000001", "2", "2.01", "10", "9999999999999999999999999999"];
        var keyed = numbers.Select(n => (Value: n, Key: TypedIndex.Key("decimal", JsonValue.Create(n)!))).ToArray();
        Assert.All(keyed, n => Assert.Equal(42, n.Key.Length));
        Assert.Equal(numbers, keyed.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => n.Value));
        Assert.Equal(numbers.Reverse(), keyed.OrderByDescending(n => n.Key, StringComparer.Ordinal).Select(n => n.Value));
        Assert.Equal(TypedIndex.Key("decimal", JsonValue.Create("+0002.000")!), TypedIndex.Key("integer", JsonValue.Create("2")!));
        Assert.Equal(TypedIndex.Key("decimal", JsonValue.Create("-0.000")!), TypedIndex.Key("decimal", JsonValue.Create("0")!));
        Assert.Throws<BosException>(() => TypedIndex.Key("decimal", null!));
    }

    [Fact]
    public void Text_keys_preserve_utf16_ordinal_order_even_with_supplementary_text()
    {
        string[] values = ["", "\0", "a", "aa", "é", "😀", "\ue000"];
        Assert.Equal(values.Order(StringComparer.Ordinal), values.OrderBy(v => TypedIndex.Key("string", JsonValue.Create(v)!), StringComparer.Ordinal));
        Assert.Equal(TypedIndex.Key("string", JsonValue.Create("é")!), TypedIndex.Key("string", JsonValue.Create("e\u0301")!));
        Assert.Equal("é", TypedIndex.Canonical("string", JsonValue.Create("e\u0301")!));
    }

    [Fact]
    public void Evolution_allows_only_safe_additions_and_nonindexed_relaxation()
    {
        var old = Obj("""{"name":"Thing","fields":{"v":{"kind":"integer","minimum":"1","maximum":"10","required":true},"choice":{"kind":"enum","values":["One"]}}}""");
        var next = (JsonObject)old.DeepClone();
        next["fields"]!["v"]!["minimum"] = "0";
        next["fields"]!["v"]!["maximum"] = "11";
        next["fields"]!["v"]!["required"] = false;
        next["fields"]!["v"]!["nullable"] = true;
        ((JsonArray)next["fields"]!["choice"]!["values"]!).Add("Two");
        next["fields"]!["extra"] = Obj("""{"kind":"string"}""");
        Schema.CheckEvolution(old, next);
        Assert.Throws<BosException>(() => Schema.CheckEvolution(next, old));
        next["fields"]!["extra"]!["required"] = true;
        Assert.Throws<BosException>(() => Schema.CheckEvolution(old, next));
    }

    [Fact]
    public void Evolution_relaxes_new_constraints_but_keeps_indexes_and_representations_immutable()
    {
        var old = Obj("""
        {"name":"Thing","fields":{
            "text":{"kind":"string","minLength":2,"maxLength":10,"format":"slug","pattern":"^[a-z-]+$"},
            "number":{"kind":"decimal","precision":8,"scale":2,"exclusiveMinimum":"0","maximum":"10","multipleOf":"0.50"},
            "list":{"kind":"array","minItems":1,"maxItems":2,"uniqueItems":true,"items":{"kind":"string","minLength":2}},
            "map":{"kind":"map","minProperties":1,"maxProperties":2,"values":{"kind":"enum","values":["A"]}},
            "union":{"kind":"taggedUnion","variants":{"A":{"kind":"string"}}},
            "indexed":{"kind":"string","maxLength":10,"indexed":true}
        }}
        """);
        var next = (JsonObject)old.DeepClone();
        next["fields"]!["text"]!["minLength"] = 1; next["fields"]!["text"]!["maxLength"] = 20; next["fields"]!["text"]!.AsObject().Remove("format"); next["fields"]!["text"]!.AsObject().Remove("pattern");
        next["fields"]!["number"]!["exclusiveMinimum"] = "-1"; next["fields"]!["number"]!["maximum"] = "11"; next["fields"]!["number"]!["multipleOf"] = "0.25";
        next["fields"]!["list"]!["minItems"] = 0; next["fields"]!["list"]!["maxItems"] = 3; next["fields"]!["list"]!["uniqueItems"] = false; next["fields"]!["list"]!["items"]!["minLength"] = 1;
        next["fields"]!["map"]!["minProperties"] = 0; next["fields"]!["map"]!["maxProperties"] = 3; ((JsonArray)next["fields"]!["map"]!["values"]!["values"]!).Add("B");
        next["fields"]!["union"]!["variants"]!["B"] = Obj("""{"kind":"integer"}""");
        Schema.CheckEvolution(old, next);
        next["fields"]!["indexed"]!["maxLength"] = 11;
        Assert.Throws<BosException>(() => Schema.CheckEvolution(old, next));
        next = (JsonObject)old.DeepClone(); next["fields"]!["number"]!["scale"] = 3;
        Assert.Throws<BosException>(() => Schema.CheckEvolution(old, next));
    }

    [Theory]
    [InlineData("{\"kind\":\"decimal\",\"precision\":18,\"scale\":3}")]
    [InlineData("{\"kind\":\"decimal\",\"precision\":19,\"scale\":2}")]
    [InlineData("{\"kind\":\"string\"}")]
    [InlineData("{\"kind\":\"decimal\",\"precision\":18,\"scale\":2,\"indexed\":true}")]
    [InlineData("{\"kind\":\"decimal\",\"precision\":18,\"scale\":2,\"managedBy\":[\"UpdateThing\"]}")]
    [InlineData("{\"kind\":\"decimal\",\"precision\":18,\"scale\":2,\"minimum\":\"0\"}")]
    public void Evolution_rejects_contract_breaking_field_changes(string replacement)
    {
        var old = Obj("""{"name":"Thing","fields":{"v":{"kind":"decimal","precision":18,"scale":2}}}""");
        var next = (JsonObject)old.DeepClone();
        next["fields"]!["v"] = Obj(replacement);
        Assert.Equal("SCHEMA_VERSION_MISMATCH", Assert.Throws<BosException>(() => Schema.CheckEvolution(old, next)).Code);
    }

    [Fact]
    public void Evolution_checks_nested_fields_policy_reference_targets_and_index_constraints()
    {
        string[] fields =
        [
            "{\"v\":{\"kind\":\"object\",\"fields\":{\"child\":{\"kind\":\"string\"}}}}",
            "{\"v\":{\"kind\":\"reference\",\"targetType\":\"Thing\"}}",
            "{\"v\":{\"kind\":\"string\",\"maxLength\":10,\"indexed\":true}}"
        ];
        foreach (var definition in fields)
        {
            var old = new JsonObject { ["name"] = "Thing", ["fields"] = Obj(definition) };
            var next = (JsonObject)old.DeepClone();
            if (next["fields"]!["v"]!["fields"] is JsonObject nested) nested.Remove("child");
            else if (next["fields"]!["v"]!["targetType"] is not null) next["fields"]!["v"]!["targetType"] = "Other";
            else next["fields"]!["v"]!["maxLength"] = 11;
            Assert.Throws<BosException>(() => Schema.CheckEvolution(old, next));
        }
        var policy = Obj("""{"name":"Thing","fields":{}}""");
        var changed = Obj("""{"name":"Thing","fields":{},"accessMode":"operationsOnly","allowedOperations":["UpdateThing"]}""");
        Assert.Throws<BosException>(() => Schema.CheckEvolution(policy, changed));
    }

    [Fact]
    public void Schema_limits_and_error_count_are_enforced()
    {
        var fields = new JsonObject();
        for (var i = 0; i < 201; i++) fields["f" + i] = Obj("""{"kind":"integer","required":true}""");
        var schema = new JsonObject { ["fields"] = fields };
        Assert.False(Schema.ValidateDefinition(schema, true).Valid);
        fields.Remove("f200");
        Assert.Equal(100, Schema.ValidateData(schema, Obj("{}")).Errors.Count);
        var field = Obj("""{"kind":"string"}""");
        for (var i = 0; i < 8; i++) field = new JsonObject { ["kind"] = "array", ["maxItems"] = 1, ["items"] = field };
        Assert.False(Schema.ValidateDefinition(new JsonObject { ["fields"] = new JsonObject { ["deep"] = field } }, true).Valid);
        var scalarSchema = Obj("""{"fields":{"outer":{"kind":"array","maxItems":200,"items":{"kind":"array","maxItems":200,"items":{"kind":"boolean"}}}}}""");
        var outer = new JsonArray();
        for (var i = 0; i < 11; i++) outer.Add(new JsonArray(Enumerable.Range(0, 200).Select(_ => (JsonNode?)JsonValue.Create(true)).ToArray()));
        Assert.False(Schema.ValidateData(scalarSchema, new JsonObject { ["outer"] = outer }).Valid);
    }

    [Fact]
    public void Strict_arguments_distinguish_omission_null_and_metadata_from_business_numbers()
    {
        var args = Obj("""{"s":"text","i":1,"b":true,"o":{},"a":[],"nil":null}""");
        Assert.Equal("text", JsonArgs.String(args, "s"));
        Assert.Equal(1, JsonArgs.Int(args, "i"));
        Assert.Equal(1, JsonArgs.Int(new JsonObject { ["i"] = 1L }, "i"));
        Assert.True(JsonArgs.Bool(args, "b"));
        Assert.Empty(JsonArgs.Object(args, "o"));
        Assert.Empty(JsonArgs.Array(args, "a"));
        Assert.Equal("fallback", JsonArgs.String(args, "absent", "fallback"));
        Assert.Throws<BosException>(() => JsonArgs.String(args, "nil", "fallback"));
        Assert.Throws<BosException>(() => JsonArgs.Int(Obj("""{"v":"1"}"""), "v"));
        Assert.Throws<BosException>(() => JsonArgs.Int(Obj("""{"v":2147483648}"""), "v"));
        Assert.Throws<BosException>(() => JsonArgs.Only(args, "s"));
        Assert.Throws<BosException>(() => JsonArgs.Name("_system"));
        Assert.Equal("Thing_1", JsonArgs.Name("Thing_1"));
        Assert.Throws<BosException>(() => JsonArgs.Uuid("{a4d7b42d-a26b-4f47-a890-1a3c3889fc26}"));
    }
}
