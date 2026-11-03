using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bos.Contracts;
using Bos.Core;

namespace Bos.Flows;

public sealed class FlowCompiler(IOperationRegistry registry) : IFlowCompiler
{
    public const string AstFormat = "bos.ast/1.0";
    public CompiledFlow Compile(string source)
    {
        var ast = FlowYaml.Parse(source);
        Validate(ast, false);
        ast["astFormat"] = AstFormat;
        if (Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(ast)) > 262144) throw FlowYaml.Error("AST exceeds 256 KiB.");
        return new(ast, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), CanonicalJson.Hash(ast));
    }

    internal void Validate(JsonObject ast, bool compiled)
    {
        Only(ast, compiled ? ["format", "astFormat", "name", "mode", "transaction", "inputSchema", "outputSchema", "dependencies", "capabilities", "steps"] :
            ["format", "name", "mode", "transaction", "inputSchema", "outputSchema", "dependencies", "capabilities", "steps"]);
        if (compiled && Text(ast, "astFormat") != AstFormat) throw new BosException("DEPENDENCY_UNAVAILABLE", "Unsupported compiled AST format.");
        if (Text(ast, "format") != "bos.flow/1.0") throw FlowYaml.Error("Unsupported flow format.");
        Name(Text(ast, "name"));
        string mode = Text(ast, "mode"), transaction = Text(ast, "transaction");
        if (!(mode == "command" && transaction == "atomic" || mode == "query" && transaction == "readOnly"))
            throw FlowYaml.Error("Invalid mode/transaction combination.");
        var input = Obj(ast, "inputSchema"); var output = Obj(ast, "outputSchema");
        foreach (var schema in new[] { input, output })
        {
            var report = Schema.ValidateDefinition(schema, true);
            if (!report.Valid) throw new BosException("FLOW_INVALID", "Invalid flow data schema.") { Details = report.Errors };
        }
        var dependencies = Obj(ast, "dependencies"); Only(dependencies, "types", "operations");
        var types = Obj(dependencies, "types"); var operations = Obj(dependencies, "operations");
        foreach (var (name, value) in types)
        {
            Name(name);
            if (value is not JsonArray a || a.Count == 0) throw FlowYaml.Error("Type dependencies require exact version arrays.");
            var versions = a.Select(PositiveInt).ToArray();
            if (versions.Distinct().Count() != versions.Length) throw FlowYaml.Error("Duplicate type version.");
        }
        foreach (var (name, version) in operations)
        {
            CheckOperationName(name);
            var descriptor = Resolve(name, PositiveInt(version));
            if (name.StartsWith("flows.", StringComparison.Ordinal) || name.StartsWith("types.", StringComparison.Ordinal) ||
                name.StartsWith("identity.", StringComparison.Ordinal) || name.StartsWith("system.", StringComparison.Ordinal) || name == "operations.call")
                throw FlowYaml.Error("Administrative and nested flow methods cannot be called by a flow.");
            if (descriptor.Mode is not ("read" or "write")) throw FlowYaml.Error("Invalid operation mode.");
            if (!descriptor.FlowCallable) throw FlowYaml.Error("Operation is not flow callable: " + name);
            if (mode == "query" && descriptor.Mode == "write") throw new BosException("MODE_VIOLATION", "Query depends on a write operation.");
        }
        var capabilities = Arr(ast, "capabilities"); var capabilitySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            string cap = String(capability); var parts = cap.Split(':');
            if (parts.Length != 2 || cap.Contains('*') || !Regex.IsMatch(parts[0], @"^[a-zA-Z][a-zA-Z.]*$") ||
                !Regex.IsMatch(parts[1], @"^[a-zA-Z][a-zA-Z0-9_.]{0,127}$") || !capabilitySet.Add(cap))
                throw FlowYaml.Error("Capabilities must be unique, concrete action:resource grants.");
            if (parts[0] is not ("record.read" or "record.create" or "record.update" or "record.delete" or "operations.execute"))
                throw FlowYaml.Error("Unsupported flow capability action: " + parts[0]);
            if (parts[0].StartsWith("record.", StringComparison.Ordinal) && !types.ContainsKey(parts[1]))
                throw FlowYaml.Error("Record capability requires a type dependency: " + parts[1]);
        }
        // Admission resolves all declared dependencies, including unused ones. Match
        // its requirements and the dispatcher's implicit custom-operation grant.
        foreach (var (name, version) in operations)
        {
            var descriptor = Resolve(name, PositiveInt(version));
            if (!name.StartsWith("records.", StringComparison.Ordinal) && !capabilitySet.Contains("operations.execute:" + descriptor.Name))
                throw FlowYaml.Error("Missing operation capability: operations.execute:" + descriptor.Name);
            foreach (var grant in descriptor.Capabilities.Concat(descriptor.RequiredGrants))
            {
                if (grant.Resource is "*" or "{type}" or "")
                {
                    if (!name.StartsWith("records.", StringComparison.Ordinal)) throw FlowYaml.Error("Operation requires a non-concrete capability.");
                    continue;
                }
                if (!capabilitySet.Contains(grant.Action + ":" + grant.Resource)) throw FlowYaml.Error("Missing operation capability: " + grant.Action + ":" + grant.Resource);
                if (grant.Action.StartsWith("record.", StringComparison.Ordinal) && !types.ContainsKey(grant.Resource))
                    throw FlowYaml.Error("Missing operation type dependency: " + grant.Resource);
                if (grant.Action == "record.create" && types[grant.Resource] is JsonArray createVersions && createVersions.Count != 1)
                    throw FlowYaml.Error("Create capability requires one exact type version; multi-version create selection is not defined.");
            }
        }
        var scope = new Dictionary<string, Shape>(StringComparer.Ordinal)
        {
            ["input"] = Shape.Schema(input),
            ["ctx"] = Shape.Object(new Dictionary<string, Shape>(StringComparer.Ordinal)
            { ["principalId"] = new("string"), ["runId"] = new("string"), ["startedAt"] = new("instant"), ["workspaceTimezone"] = new("string") })
        };
        var ids = new HashSet<string>(StringComparer.Ordinal); int count = 0;
        if (!Steps(Arr(ast, "steps"), scope, 0)) throw FlowYaml.Error("A return is required on every control path.");

        bool Steps(JsonArray steps, Dictionary<string, Shape> visible, int depth)
        {
            if (depth > 32) throw FlowYaml.Error("Instruction nesting exceeds 32.");
            bool returned = false;
            foreach (var node in steps)
            {
                if (++count > 500) throw FlowYaml.Error("More than 500 static steps.");
                var step = node as JsonObject ?? throw FlowYaml.Error("A step must be a mapping.");
                string id = Text(step, "id"); Name(id);
                if (!ids.Add(id)) throw FlowYaml.Error("Step ids must be unique throughout a flow: " + id);
                var instructions = new[] { "let", "call", "if", "forEach", "require", "log", "return" }.Where(step.ContainsKey).ToArray();
                if (instructions.Length != 1) throw FlowYaml.Error("A step requires exactly one instruction.", id);
                switch (instructions[0])
                {
                    case "let":
                        Only(step, "id", "let", "value"); Bind(visible, Text(step, "let"), Expr(step["value"], visible)); break;
                    case "call":
                    {
                        Only(step, "id", "call", "args", "as");
                        string name = Text(step, "call");
                        if (!operations.ContainsKey(name)) throw FlowYaml.Error("Missing operation dependency: " + name, id);
                        var descriptor = Resolve(name, PositiveInt(operations[name]));
                        var args = Obj(step, "args");
                        var argTypes = Shape.Object(args.ToDictionary(x => x.Key, x => Expr(x.Value, visible), StringComparer.Ordinal));
                        var inputType = Shape.Schema(descriptor.InputSchema, descriptor.MetadataPaths, "input");
                        if (name.StartsWith("records.", StringComparison.Ordinal) && inputType.Fields is { } parameters)
                            foreach (string slot in new[] { "data", "patch", "query", "filter" })
                                if (descriptor.InputSchema["fields"]?[slot] is JsonObject { Count: 0 })
                                    parameters[slot] = new("object", Required: slot is "data" or "patch");
                        CheckAssignable(argTypes, inputType, true, operationInput: true);
                        string? recordType = null;
                        if (name.StartsWith("records.", StringComparison.Ordinal))
                        {
                            if (args["type"] is not JsonObject typeExpr || typeExpr.Count != 1 || typeExpr["literal"] is not JsonValue)
                                throw FlowYaml.Error("Record type must be a literal pinned type name.", id);
                            recordType = String(typeExpr["literal"]);
                            if (!types.ContainsKey(recordType)) throw FlowYaml.Error("Missing type dependency: " + recordType, id);
                            if (!capabilitySet.Contains("record.read:" + recordType)) throw FlowYaml.Error("Missing record read capability.", id);
                            string action = name switch { "records.get" or "records.list" or "records.count" => "record.read", "records.create" => "record.create", "records.update" => "record.update", "records.delete" => "record.delete", _ => "" };
                            if (action.Length > 0 && !capabilitySet.Contains(action + ":" + recordType)) throw FlowYaml.Error("Missing record capability.", id);
                            if (name == "records.validate" && !capabilitySet.Contains("record.create:" + recordType) && !capabilitySet.Contains("record.update:" + recordType))
                                throw FlowYaml.Error("Record validation requires a create or update capability.", id);
                            if (name == "records.validate" && args["typeVersion"] is JsonObject versionExpression)
                            {
                                long? exact = versionExpression["integer"] is JsonValue integer ? FlowValue.ParseInteger(String(integer)) :
                                    versionExpression["literal"] is JsonValue literal ? FlowValue.ParseInteger(literal.ToJsonString().Trim('"')) : null;
                                if (exact.HasValue && !((JsonArray)types[recordType]!).Any(v => PositiveInt(v) == exact.Value))
                                    throw FlowYaml.Error("Explicit type version is not pinned by the flow.", id);
                            }
                            if (name == "records.create" && ((JsonArray)types[recordType]!).Count != 1)
                                throw FlowYaml.Error("Create requires one exact type version; multi-version create selection is not defined.", id);
                        }
                        Shape result = Shape.Schema(descriptor.OutputSchema, descriptor.MetadataPaths, "output");
                        if (name is "records.get" or "records.create" or "records.update") result = RecordShape();
                        if (name == "records.list") result = Shape.Object(new Dictionary<string, Shape> { ["items"] = new("array", Item: RecordShape()), ["nextCursor"] = new("string", Nullable: true) });
                        if (name == "records.validate") result = Shape.Object(new Dictionary<string, Shape>
                        {
                            ["valid"] = new("boolean"), ["errors"] = new("array", Item: Shape.Object(new Dictionary<string, Shape>
                            { ["path"] = new("string"), ["code"] = new("string"), ["message"] = new("string") }))
                        });
                        if (step.ContainsKey("as")) Bind(visible, Text(step, "as"), result);
                        break;
                    }
                    case "if":
                        Only(step, "id", "if", "then", "else"); RequireKind(Expr(step["if"], visible), "boolean");
                        bool yes = Steps(Arr(step, "then"), new(visible, StringComparer.Ordinal), depth + 1);
                        bool no = step.ContainsKey("else") && Steps(Arr(step, "else"), new(visible, StringComparer.Ordinal), depth + 1);
                        returned |= yes && no; break;
                    case "forEach":
                    {
                        Only(step, "id", "forEach", "as", "maxItems", "steps");
                        var source = Expr(step["forEach"], visible); RequireKind(source, "array");
                        int max = PositiveInt(step["maxItems"]); if (max > 100) throw FlowYaml.Error("Loop maximum exceeds 100.");
                        var child = new Dictionary<string, Shape>(visible, StringComparer.Ordinal);
                        Bind(child, Text(step, "as"), source.Item ?? Shape.Unknown);
                        Steps(Arr(step, "steps"), child, depth + 1); break;
                    }
                    case "require":
                        Only(step, "id", "require", "code", "message"); RequireKind(Expr(step["require"], visible), "boolean");
                        if (!Regex.IsMatch(Text(step, "code"), @"^[A-Z][A-Z0-9_]{0,63}$")) throw FlowYaml.Error("Invalid require error code.");
                        if (Encoding.UTF8.GetByteCount(Text(step, "message")) > 16000) throw FlowYaml.Error("Require message exceeds the diagnostic bound.");
                        break;
                    case "log":
                        Only(step, "id", "log", "fields", "level"); Text(step, "log");
                        if (step.ContainsKey("level") && Text(step, "level") is not ("info" or "warn")) throw FlowYaml.Error("Invalid log level.");
                        if (step.ContainsKey("fields")) foreach (var (key, value) in Obj(step, "fields"))
                        {
                            Name(key);
                            if (Expr(value, visible).Kind is "object" or "array") throw FlowYaml.Error("Log fields must be scalar metadata.");
                        }
                        break;
                    case "return":
                        Only(step, "id", "return"); CheckAssignable(Expr(step["return"], visible), Shape.Schema(output), true); returned = true; break;
                }
            }
            return returned;
        }
    }

    private OperationDescriptor Resolve(string name, int version)
    {
        try { return registry.Resolve(name, version).Descriptor; }
        catch (BosException e) { throw new BosException("DEPENDENCY_UNAVAILABLE", "Unavailable operation dependency: " + name + ". " + e.Message); }
    }
    private static Shape RecordShape() => Shape.Object(new Dictionary<string, Shape>
    {
        ["id"] = new("string"), ["type"] = new("string"), ["typeVersion"] = new("integer"), ["revision"] = new("integer"),
        ["deleted"] = new("boolean"), ["data"] = Shape.Unknown, ["createdAt"] = new("instant"), ["updatedAt"] = new("instant"),
        ["createdBy"] = new("string"), ["updatedBy"] = new("string")
    });
    private static void Bind(Dictionary<string, Shape> scope, string name, Shape shape)
    {
        Name(name);
        if (!scope.TryAdd(name, shape)) throw FlowYaml.Error("Cannot overwrite a visible binding: " + name);
    }
    private static Shape Expr(JsonNode? node, Dictionary<string, Shape> scope, int depth = 1)
    {
        if (depth > 16) throw FlowYaml.Error("Expression depth exceeds 16.");
        var e = node as JsonObject ?? throw FlowYaml.Error("An expression must be a mapping.");
        Shape Sub(JsonNode? n) => Expr(n, scope, depth + 1);
        if (e.ContainsKey("fn"))
        {
            Only(e, "fn", "args"); string name = Text(e, "fn"); var args = Arr(e, "args");
            FlowHelpers.Get(name, args.Count);
            var shapes = args.Select(Sub).ToArray();
            if (name == "pluck")
            {
                if (args[1] is not JsonObject p || p.Count != 1 || !p.ContainsKey("literal")) throw FlowYaml.Error("pluck path must be literal text.");
                string path = String(p["literal"]); CheckPath(path);
                RequireKind(shapes[0], "array");
                var item = Traverse(shapes[0].Item ?? Shape.Unknown, path.Split('.'));
                if (item.Kind is "array" or "object") throw FlowYaml.Error("pluck selects scalars only.");
                return new("array", Item: item);
            }
            return HelperShape(name, shapes);
        }
        if (e.Count != 1) throw FlowYaml.Error("Expression requires exactly one discriminator.");
        var (key, value) = e.First();
        switch (key)
        {
            case "literal": return LiteralShape(value);
            case "integer": FlowValue.ParseInteger(String(value)); return new("integer");
            case "decimal": ExactDecimal.Parse(String(value)); return new("decimal");
            case "path":
                string path = String(value); CheckPath(path); var parts = path.Split('.');
                if (!scope.TryGetValue(parts[0], out var root)) throw FlowYaml.Error("Unknown or out-of-scope variable: " + parts[0]);
                return Traverse(root, parts.Skip(1));
            case "object":
                if (value is not JsonObject obj) throw FlowYaml.Error("object requires a field mapping.");
                foreach (var field in obj) Name(field.Key);
                return Shape.Object(obj.ToDictionary(x => x.Key, x => Sub(x.Value), StringComparer.Ordinal));
            case "array":
                if (value is not JsonArray a) throw FlowYaml.Error("array requires a sequence.");
                var items = a.Select(Sub).ToArray();
                return new("array", Item: items.Length == 0 ? Shape.Unknown : items.All(x => x.Kind == items[0].Kind) ? items[0] : Shape.Unknown, Elements: items);
            default: throw FlowYaml.Error("Unknown expression discriminator: " + key);
        }
    }
    private static Shape Traverse(Shape root, IEnumerable<string> segments)
    {
        foreach (string segment in segments)
        {
            if (root.Kind == "unknown") return root;
            if (root.Kind != "object" || root.Fields is null || !root.Fields.TryGetValue(segment, out var next))
                throw FlowYaml.Error("Unknown field or invalid scalar traversal: " + segment);
            root = next;
        }
        return root;
    }
    private static Shape LiteralShape(JsonNode? value)
    {
        if (value is null) return new("null");
        if (value is JsonObject o) return Shape.Object(o.ToDictionary(x => x.Key, x => LiteralShape(x.Value), StringComparer.Ordinal));
        if (value is JsonArray a)
        {
            var items = a.Select(LiteralShape).ToArray();
            return new("array", Item: items.Length > 0 && items.All(x => x.Kind == items[0].Kind) ? items[0] : Shape.Unknown, Elements: items);
        }
        var literal = FlowValue.Literal(value);
        return new(literal.Kind.ToString().ToLowerInvariant(), Literal: value.DeepClone());
    }
    private static Shape HelperShape(string name, Shape[] args)
    {
        void K(int i, params string[] kinds) { if (args[i].Kind != "unknown" && !kinds.Contains(args[i].Kind)) throw FlowYaml.Error("Invalid operand type for " + name); }
        void Same(int a, int b) { if (args[a].Kind != "unknown" && args[b].Kind != "unknown" && args[a].Kind != args[b].Kind) throw FlowYaml.Error("Mixed operand types for " + name); }
        switch (name)
        {
            case "equal": case "notEqual":
                if (args.All(x => x.Kind != "null")) Same(0, 1);
                foreach (var a in args) if (a.Kind is "object" or "array") throw FlowYaml.Error("Structural equality is unsupported.");
                return new("boolean");
            case "greaterThan": case "greaterOrEqual": case "lessThan": case "lessOrEqual":
                K(0, "string", "integer", "decimal", "date", "instant"); K(1, "string", "integer", "decimal", "date", "instant"); Same(0, 1); return new("boolean");
            case "and": case "or": case "not": for (int i = 0; i < args.Length; i++) K(i, "boolean"); return new("boolean");
            case "isNull": case "isMissing": case "isEmpty": return new("boolean");
            case "coalesce":
            {
                var possible = args.Where(x => x.Kind != "null").ToArray();
                if (possible.Length == 0) return new("null");
                var first = possible[0];
                if (first.Literal is not null || possible.Length == 1) return first;
                // Optional/nullable values may select a differently typed fallback.
                // There is no union/refinement type in this AST; defer that boundary.
                return first.Kind is not ("object" or "array") && possible.All(x => x.Kind == first.Kind) ? new(first.Kind) : Shape.Unknown;
            }
            case "add": case "subtract": case "multiply": K(0, "integer", "decimal"); K(1, "integer", "decimal"); Same(0, 1); return new(args[0].Kind);
            case "divide": K(0, "decimal"); K(1, "decimal"); K(2, "integer"); K(3, "string"); return new("decimal");
            case "round": K(0, "integer", "decimal"); K(1, "integer"); K(2, "string"); return new(args[0].Kind);
            case "abs": K(0, "integer", "decimal"); return new(args[0].Kind);
            case "length": K(0, "array"); return new("integer");
            case "contains":
                K(0, "array");
                if (args[0].Item?.Kind is "object" or "array" || args[1].Kind is "object" or "array") throw FlowYaml.Error("contains requires scalar equality.");
                if (args[0].Item is { Kind: not "unknown" } item) SameShapes(item, args[1]);
                return new("boolean");
            case "sum": case "min": case "max":
                K(0, "array"); if (args[0].Item is { Kind: not ("unknown" or "integer" or "decimal") }) throw FlowYaml.Error("Aggregation requires numeric items.");
                if (args[0].Elements is { } elements)
                {
                    var known = elements.Where(x => x.Kind != "unknown").Select(x => x.Kind).Distinct().ToArray();
                    if (known.Any(x => x is not ("integer" or "decimal")) || known.Length > 1) throw FlowYaml.Error("Aggregation requires homogeneous numeric items.");
                }
                return args[0].Item is { } numericItem ? new(numericItem.Kind) : Shape.Unknown;
            case "trim": case "lower": case "upper": case "parseInteger": case "parseDecimal": case "parseDate":
                K(0, "string"); return new(name switch { "parseInteger" => "integer", "parseDecimal" => "decimal", "parseDate" => "date", _ => "string" });
            case "concat": for (int i = 0; i < args.Length; i++) K(i, "string"); return new("string");
            case "startsWith": case "endsWith": K(0, "string"); K(1, "string"); return new("boolean");
            case "now": return new("instant");
            case "today": if (args.Length > 0) K(0, "string"); return new("date");
            case "addDays": K(0, "date"); K(1, "integer"); return new("date");
            case "daysBetween": K(0, "date"); K(1, "date"); return new("integer");
            case "toString": if (args[0].Kind is "array" or "object") throw FlowYaml.Error("toString requires a scalar."); return new("string");
            default: return Shape.Unknown;
        }
    }
    private static void SameShapes(Shape a, Shape b)
    {
        if (a.Kind != "unknown" && b.Kind != "unknown" && a.Kind != b.Kind && a.Kind != "null" && b.Kind != "null") throw FlowYaml.Error("Incompatible scalar kinds.");
    }
    private static void CheckAssignable(Shape actual, Shape expected, bool binding, bool operationInput = false)
    {
        if (actual.Kind == "unknown" || expected.Kind == "unknown") return;
        if (actual.Kind == "null") { if (!expected.Nullable) throw FlowYaml.Error("Null is not allowed by the receiving schema."); return; }
        if (actual.Kind == "integer" && actual.Literal is not null && expected.Kind == "integer" &&
            !(operationInput && expected.Metadata))
            throw FlowYaml.Error("Use a typed integer expression or a quoted business integer.");
        if (actual.Kind != expected.Kind)
        {
            if (binding && actual.Kind == "string" && expected.Kind is "integer" or "decimal" or "date" or "instant")
            {
                if (actual.Literal is JsonValue literal)
                {
                    string s = literal.GetValue<string>();
                    if (expected.Kind == "integer") FlowValue.ParseInteger(s);
                    if (expected.Kind == "decimal") ExactDecimal.Parse(s);
                    if (expected.Kind == "date" && !DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw FlowYaml.Error("Invalid date literal.");
                }
                return;
            }
            throw FlowYaml.Error("Type mismatch: " + actual.Kind + " cannot bind to " + expected.Kind);
        }
        if (actual.Kind == "object" && actual.Fields is not null && expected.Fields is not null)
        {
            foreach (var (name, value) in actual.Fields)
            {
                if (!expected.Fields.TryGetValue(name, out var field)) throw FlowYaml.Error("Unknown schema field: " + name);
                CheckAssignable(value, field, binding, operationInput);
            }
            foreach (var (name, value) in expected.Fields)
                if (value.Required && !(operationInput && value.HasDefault) && !actual.Fields.ContainsKey(name)) throw FlowYaml.Error("Missing required field: " + name);
        }
        if (actual.Kind == "array" && expected.Item is not null)
        {
            if (actual.Elements is { } elements) foreach (var element in elements) CheckAssignable(element, expected.Item, binding, operationInput);
            else if (actual.Item is not null) CheckAssignable(actual.Item, expected.Item, binding, operationInput);
        }
    }
    private static void RequireKind(Shape actual, string expected)
    {
        if (actual.Kind != "unknown" && actual.Kind != expected) throw FlowYaml.Error("Expected " + expected + ", received " + actual.Kind);
    }
    internal static void CheckPath(string path)
    {
        if (path.Length > 1024 || !Regex.IsMatch(path, @"^[A-Za-z][A-Za-z0-9_]{0,63}(\.[A-Za-z][A-Za-z0-9_]{0,63})*$")) throw FlowYaml.Error("Invalid literal field path.");
    }
    internal static void Name(string name)
    { if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]{0,63}$")) throw FlowYaml.Error("Invalid identifier: " + name); }
    private static void CheckOperationName(string name)
    { if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_.]{0,127}$")) throw FlowYaml.Error("Invalid operation name."); }
    internal static void Only(JsonObject obj, params string[] names)
    { foreach (var (name, _) in obj) if (!names.Contains(name, StringComparer.Ordinal)) throw FlowYaml.Error("Unknown property: " + name); }
    internal static JsonObject Obj(JsonObject obj, string name) => obj[name] as JsonObject ?? throw FlowYaml.Error("Expected mapping: " + name);
    internal static JsonArray Arr(JsonObject obj, string name) => obj[name] as JsonArray ?? throw FlowYaml.Error("Expected sequence: " + name);
    internal static string Text(JsonObject obj, string name) => String(obj[name]);
    internal static string String(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw FlowYaml.Error("Expected string.");
    internal static int PositiveInt(JsonNode? value)
    {
        if (value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
            (v.TryGetValue<int>(out int n) || int.TryParse(v.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) && n > 0) return n;
        throw FlowYaml.Error("Expected positive metadata integer.");
    }
    private sealed record Shape(string Kind, Dictionary<string, Shape>? Fields = null, Shape? Item = null, bool Required = false, bool Nullable = false, bool HasDefault = false, JsonNode? Literal = null, IReadOnlyList<Shape>? Elements = null, bool Metadata = false)
    {
        internal static readonly Shape Unknown = new("unknown");
        internal static Shape Object(Dictionary<string, Shape> fields) => new("object", fields);
        internal static Shape Schema(JsonObject schema, IReadOnlyList<string>? metadataPaths = null, string path = "")
        {
            string kind = schema["kind"]?.GetValue<string>() ?? (schema.ContainsKey("fields") ? "object" : "unknown");
            if (kind is "enum" or "reference") kind = "string";
            return new(kind, schema["fields"] is JsonObject f ? f.ToDictionary(x => x.Key, x => Schema((JsonObject)x.Value!, metadataPaths, path + "." + x.Key), StringComparer.Ordinal) : null,
                schema["items"] is JsonObject i ? Schema(i, metadataPaths, path + "[]") : null, schema["required"]?.GetValue<bool>() ?? false,
                schema["nullable"]?.GetValue<bool>() ?? false, schema.ContainsKey("default"), Metadata: metadataPaths?.Contains(path, StringComparer.Ordinal) == true);
        }
    }
}
