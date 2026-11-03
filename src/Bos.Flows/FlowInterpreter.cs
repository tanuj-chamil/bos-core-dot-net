using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;

namespace Bos.Flows;

public sealed class FlowInterpreter(IOperationRegistry registry) : IFlowInterpreter
{
    public async Task<JsonNode?> ExecuteAsync(JsonObject ast, JsonObject input, InvocationContext context, IUnitOfWork uow, IDispatcher dispatcher, CancellationToken ct = default)
    {
        // A detached execution image prevents a caller changing code while a call is awaited.
        ast = (JsonObject)ast.DeepClone();
        new FlowCompiler(registry).Validate(ast, true);
        if (Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(ast)) > 262144) throw Limit("AST exceeds 256 KiB.");
        var execution = new Execution(registry, ast, context, uow, dispatcher, ct);
        try { return await execution.Run(input); }
        catch (OperationCanceledException)
        {
            context.Poisoned = true;
            throw new BosException("DEADLINE_EXCEEDED", "Execution cancelled or deadline exceeded.", path: context.StepId ?? "")
            { Details = [new("stepId", "DEADLINE_EXCEEDED", context.StepId ?? ""), new("iterationPath", "DEADLINE_EXCEEDED", context.IterationPath)] };
        }
    }

    private sealed class Execution(IOperationRegistry registry, JsonObject ast, InvocationContext context, IUnitOfWork uow, IDispatcher dispatcher, CancellationToken ct)
    {
        private readonly Dictionary<string, JsonObject> typeSchemas = new(StringComparer.Ordinal);
        private int logBytes;
        private int stepBytes = 64;
        private bool returning;
        private JsonNode? result;
        private readonly JsonObject typeDependencies = FlowCompiler.Obj(FlowCompiler.Obj(ast, "dependencies"), "types");

        internal async Task<JsonNode?> Run(JsonObject input)
        {
            context.Budget.Check(ct);
            if (context.Poisoned) throw new BosException("OPERATION_FAILED", "Execution transaction is already poisoned.");
            var normalized = Schema.Normalize(FlowCompiler.Obj(ast, "inputSchema"), input);
            var scope = new Dictionary<string, FlowValue>(StringComparer.Ordinal)
            {
                ["input"] = Decode(normalized, FlowCompiler.Obj(ast, "inputSchema")),
                ["ctx"] = FlowValue.Object(new Dictionary<string, FlowValue>
                {
                    ["principalId"] = FlowValue.Text(context.PrincipalId),
                    ["runId"] = context.RunId is null ? FlowValue.Null : FlowValue.Text(context.RunId),
                    ["startedAt"] = FlowValue.Instant(context.StartedAt),
                    ["workspaceTimezone"] = FlowValue.Text(context.WorkspaceTimezone)
                })
            };
            foreach (var existing in context.Logs) logBytes += Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(existing));
            foreach (var existing in context.Steps) stepBytes += Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(existing)) + 129;
            if (logBytes > 16384 || stepBytes > 262144 - 1024) throw Limit("Existing diagnostics exceed their budget.");
            try
            {
                await Steps(FlowCompiler.Arr(ast, "steps"), scope, "");
                if (!returning) throw new BosException("MISSING_RETURN", "Flow reached the end without returning.");
                context.Budget.Check(ct);
                return result;
            }
            catch (BosException error) { context.Poisoned = true; throw Failure(error); }
            catch { context.Poisoned = true; throw; }
        }

        private async Task Steps(JsonArray steps, Dictionary<string, FlowValue> scope, string iterationPath)
        {
            foreach (var stepNode in steps)
            {
                var step = stepNode!.AsObject();
                if (returning) return;
                context.StepId = FlowCompiler.Text(step, "id"); context.IterationPath = iterationPath;
                Tick();
                if (context.Steps.Count >= 500) throw Failure(Limit("More than 500 step results."));
                long started = Stopwatch.GetTimestamp();
                var entry = new JsonObject { ["stepId"] = context.StepId, ["iterationPath"] = iterationPath, ["ordinal"] = context.Steps.Count + 1,
                    ["startedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), ["status"] = "running" };
                // Reserve the largest final status, bounded error code and duration
                // before executing the step, including every open enclosing block.
                int entryBytes = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(entry)) + 129;
                if (entryBytes > 262144 - 1024 - stepBytes) throw Limit("Step metadata exceeds 256 KiB.");
                stepBytes += entryBytes;
                context.Steps.Add(entry);
                try
                {
                    if (step.ContainsKey("let")) Bind(scope, FlowCompiler.Text(step, "let"), Eval(step["value"], scope));
                    else if (step.ContainsKey("call"))
                    {
                        string name = FlowCompiler.Text(step, "call");
                        int version = FlowCompiler.PositiveInt(FlowCompiler.Obj(FlowCompiler.Obj(ast, "dependencies"), "operations")[name]);
                        var descriptor = registry.Resolve(name, version).Descriptor;
                        if (!descriptor.FlowCallable) throw new BosException("DEPENDENCY_UNAVAILABLE", "Operation is no longer flow callable.");
                        if ((context.ReadOnly || FlowCompiler.Text(ast, "mode") == "query") && descriptor.Mode == "write")
                            throw new BosException("MODE_VIOLATION", "Write operation in read-only execution.");
                        var args = FlowValue.Object(FlowCompiler.Obj(step, "args").Select(x => KeyValuePair.Create(x.Key, Eval(x.Value, scope))));
                        var metadataPaths = new HashSet<string>(descriptor.MetadataPaths ?? [], StringComparer.Ordinal);
                        var bound = BindInput(args, descriptor.InputSchema, name, metadataPaths);
                        JsonNode? output;
                        try { output = await dispatcher.CallAsync(name, version, bound, context, uow, ct); }
                        catch { context.Poisoned = true; throw; }
                        Tick();
                        if (context.Poisoned) throw new BosException("OPERATION_FAILED", "Operation poisoned the execution transaction.");
                        if (Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(output)) > 262144) throw Limit("Operation output exceeds 256 KiB.");
                        bool projected = name == "records.list" && bound["query"] is JsonObject query && query["select"] is JsonArray;
                        var decoded = await DecodeOutput(output, descriptor.OutputSchema, metadataPaths, projectedRecords: projected);
                        if (step.ContainsKey("as")) Bind(scope, FlowCompiler.Text(step, "as"), decoded);
                    }
                    else if (step.ContainsKey("if"))
                    {
                        bool condition = Eval(step["if"], scope).Require<bool>(ValueKind.Boolean);
                        string branch = condition ? "then" : "else";
                        if (step.ContainsKey(branch)) await Steps(FlowCompiler.Arr(step, branch), new(scope, StringComparer.Ordinal), iterationPath);
                    }
                    else if (step.ContainsKey("forEach"))
                    {
                        var items = Eval(step["forEach"], scope).Items;
                        int max = FlowCompiler.PositiveInt(step["maxItems"]);
                        if (items.Length > max || items.Length > 100) throw Limit("Loop source exceeds its maximum before iteration.");
                        for (int i = 0; i < items.Length && !returning; i++)
                        {
                            string path = (iterationPath.Length == 0 ? "" : iterationPath + "/") + FlowCompiler.Text(step, "id") + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                            context.StepId = FlowCompiler.Text(step, "id"); context.IterationPath = path;
                            Tick(); var child = new Dictionary<string, FlowValue>(scope, StringComparer.Ordinal);
                            Bind(child, FlowCompiler.Text(step, "as"), items[i]);
                            await Steps(FlowCompiler.Arr(step, "steps"), child, path);
                        }
                    }
                    else if (step.ContainsKey("require"))
                    {
                        if (!Eval(step["require"], scope).Require<bool>(ValueKind.Boolean))
                            throw new BosException(FlowCompiler.Text(step, "code"), FlowCompiler.Text(step, "message"));
                    }
                    else if (step.ContainsKey("log"))
                    {
                        var fields = new JsonObject();
                        if (step["fields"] is JsonObject f) foreach (var (name, expression) in f)
                        {
                            var value = Eval(expression, scope);
                            if (value.Kind is ValueKind.Array or ValueKind.Object) throw new BosException("TYPE_MISMATCH", "Log fields must be scalar metadata.");
                            fields[name] = value.Json();
                        }
                        var log = new JsonObject { ["stepId"] = context.StepId, ["iterationPath"] = iterationPath,
                            ["level"] = step.ContainsKey("level") ? FlowCompiler.Text(step, "level") : "info", ["message"] = FlowCompiler.Text(step, "log"), ["fields"] = fields };
                        int bytes = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(log));
                        if (bytes > 16384 - logBytes) throw Limit("Diagnostics exceed 16 KiB.");
                        logBytes += bytes; context.Logs.Add(log);
                    }
                    else if (step.ContainsKey("return"))
                    {
                        var value = Eval(step["return"], scope);
                        if (value.Kind != ValueKind.Object) throw new BosException("OUTPUT_INVALID", "Flow output must be an object.");
                        try
                        {
                            CheckBinding(value, FlowCompiler.Obj(ast, "outputSchema"));
                            result = Schema.Normalize(FlowCompiler.Obj(ast, "outputSchema"), value.Json()!.AsObject(), false);
                        }
                        catch (BosException e) when (e.Code != "MISSING_VALUE")
                        { throw new BosException("OUTPUT_INVALID", "Flow output does not match its schema.") { Details = e.Details }; }
                        if (Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(result)) > 65536) throw Limit("Output exceeds 64 KiB.");
                        returning = true;
                    }
                    entry["status"] = "succeeded";
                }
                catch (BosException error)
                {
                    entry["status"] = "failed"; entry["code"] = StepCode(error.Code);
                    throw Failure(error);
                }
                catch (OperationCanceledException)
                {
                    entry["status"] = "failed"; entry["code"] = "DEADLINE_EXCEEDED";
                    throw Failure(new BosException("DEADLINE_EXCEEDED", "Execution cancelled or deadline exceeded."));
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    entry["status"] = "failed"; entry["code"] = "INTERNAL_ERROR";
                    throw Failure(new BosException("INTERNAL_ERROR", "Flow operation failed.", 500));
                }
                finally { entry["durationMs"] = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
                // Nested blocks leave their last location in context. Restore the parent
                // for the next instruction; a failure deliberately retains the deepest one.
                context.StepId = FlowCompiler.Text(step, "id"); context.IterationPath = iterationPath;
            }
        }

        private BosException Failure(BosException error)
        {
            var details = error.Details.ToList();
            if (!details.Any(x => x.Path == "stepId")) details.Add(new("stepId", error.Code, context.StepId ?? ""));
            if (!details.Any(x => x.Path == "iterationPath")) details.Add(new("iterationPath", error.Code, context.IterationPath));
            return new(error.Code, error.Message, error.Status, error.Path.Length == 0 ? context.StepId ?? "" : error.Path, error.Retryable) { Details = details };
        }
        private static string StepCode(string code) => code.Length is > 0 and <= 64 && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') ? code : "INTERNAL_ERROR";
        private void Tick() { context.Budget.Check(ct); context.Budget.Node(); }
        private static void Bind(Dictionary<string, FlowValue> scope, string name, FlowValue value)
        { if (!scope.TryAdd(name, value)) throw new BosException("SCOPE_VIOLATION", "Cannot overwrite visible binding: " + name); }

        private FlowValue Eval(JsonNode? expression, Dictionary<string, FlowValue> scope, int depth = 1)
        {
            Tick(); if (depth > 16) throw Limit("Expression depth exceeds 16.");
            var e = expression!.AsObject();
            FlowValue Sub(JsonNode? n) => Eval(n, scope, depth + 1);
            if (e.ContainsKey("fn"))
            {
                string name = FlowCompiler.Text(e, "fn"); var nodes = FlowCompiler.Arr(e, "args");
                var values = new List<FlowValue>();
                foreach (var node in nodes)
                {
                    var value = Sub(node); values.Add(value);
                    if (name == "and" && !value.Require<bool>(ValueKind.Boolean)) return FlowValue.Bool(false);
                    if (name == "or" && value.Require<bool>(ValueKind.Boolean)) return FlowValue.Bool(true);
                    if (name == "coalesce" && value.Kind is not (ValueKind.Missing or ValueKind.Null)) return value;
                }
                return FlowHelpers.Invoke(name, values, context);
            }
            var (kind, nodeValue) = e.First();
            return kind switch
            {
                "literal" => Literal(nodeValue),
                "integer" => FlowValue.Integer(FlowValue.ParseInteger(FlowCompiler.String(nodeValue))),
                "decimal" => FlowValue.Decimal(ExactDecimal.Parse(FlowCompiler.String(nodeValue))),
                "path" => Path(FlowCompiler.String(nodeValue), scope),
                "object" => FlowValue.Object(nodeValue!.AsObject().Select(x => KeyValuePair.Create(x.Key, Sub(x.Value)))),
                "array" => FlowValue.Array(nodeValue!.AsArray().Select(Sub)),
                _ => throw new BosException("DEPENDENCY_UNAVAILABLE", "Unsupported compiled expression.")
            };
        }
        private FlowValue Literal(JsonNode? node)
        {
            Tick();
            if (node is JsonObject obj) return FlowValue.Object(obj.Select(x => KeyValuePair.Create(x.Key, Literal(x.Value))));
            if (node is JsonArray array) return FlowValue.Array(array.Select(Literal));
            return FlowValue.Literal(node);
        }
        private static FlowValue Path(string path, Dictionary<string, FlowValue> scope)
        {
            int dot = path.IndexOf('.'); string root = dot < 0 ? path : path[..dot];
            if (!scope.TryGetValue(root, out var value)) throw new BosException("SCOPE_VIOLATION", "Unknown binding.");
            return dot < 0 ? value : value.Path(path[(dot + 1)..]);
        }

        // Data schemas own conversion of strings. Outside this boundary a literal
        // "12" is always String, never Integer or Decimal.
        private FlowValue Decode(JsonNode? node, JsonObject schema)
        {
            Tick();
            if (node is null) return FlowValue.Null;
            string kind = schema["kind"]?.GetValue<string>() ?? "object";
            return kind switch
            {
                "object" => FlowValue.Object(node.AsObject().Select(x => KeyValuePair.Create(x.Key, Decode(x.Value, schema["fields"]![x.Key]!.AsObject())))),
                "array" => FlowValue.Array(node.AsArray().Select(x => Decode(x, schema["items"]!.AsObject()))),
                "map" => FlowValue.Object(node.AsObject().Select(x => KeyValuePair.Create(x.Key, Decode(x.Value, schema["values"]!.AsObject())))),
                "taggedUnion" => DecodeTaggedUnion(node.AsObject(), schema),
                "money" => FlowValue.Object(new Dictionary<string, FlowValue>(StringComparer.Ordinal)
                    { ["amount"] = FlowValue.Decimal(ExactDecimal.Parse(FlowCompiler.String(node["amount"]))), ["currency"] = FlowValue.Text(FlowCompiler.String(node["currency"])) }),
                "percentage" => FlowValue.Decimal(ExactDecimal.Parse(FlowCompiler.String(node))),
                "quantity" => FlowValue.Object(new Dictionary<string, FlowValue>(StringComparer.Ordinal)
                    { ["value"] = FlowValue.Decimal(ExactDecimal.Parse(FlowCompiler.String(node["value"]))), ["unit"] = FlowValue.Text(FlowCompiler.String(node["unit"])) }),
                "geopoint" => FlowValue.Object(new Dictionary<string, FlowValue>(StringComparer.Ordinal)
                    { ["latitude"] = FlowValue.Decimal(ExactDecimal.Parse(FlowCompiler.String(node["latitude"]))), ["longitude"] = FlowValue.Decimal(ExactDecimal.Parse(FlowCompiler.String(node["longitude"]))) }),
                "dateRange" => FlowValue.Object(new Dictionary<string, FlowValue>(StringComparer.Ordinal)
                    { ["start"] = DecodeDate(node["start"]), ["end"] = DecodeDate(node["end"]) }),
                "instantRange" => FlowValue.Object(new Dictionary<string, FlowValue>(StringComparer.Ordinal)
                    { ["start"] = DecodeInstant(node["start"]), ["end"] = DecodeInstant(node["end"]) }),
                "multiEnum" => FlowValue.Array(node.AsArray().Select(x => FlowValue.Text(FlowCompiler.String(x)))),
                "polymorphicReference" => FlowValue.Object(new Dictionary<string, FlowValue>(StringComparer.Ordinal)
                    { ["type"] = FlowValue.Text(FlowCompiler.String(node["type"])), ["id"] = FlowValue.Text(FlowCompiler.String(node["id"])) }),
                "versionedReference" => FlowValue.Object(new Dictionary<string, FlowValue>(StringComparer.Ordinal)
                    { ["id"] = FlowValue.Text(FlowCompiler.String(node["id"])), ["revision"] = FlowValue.Integer(node["revision"]!.GetValue<int>()) }),
                "integer" => FlowValue.Integer(FlowValue.ParseInteger(FlowCompiler.String(node))),
                "decimal" => FlowValue.Decimal(ExactDecimal.Parse(FlowCompiler.String(node))),
                "date" => DecodeDate(node),
                "instant" => DecodeInstant(node),
                "boolean" => FlowValue.Bool(node.GetValue<bool>()),
                _ => FlowValue.Text(FlowCompiler.String(node))
            };
        }

        private FlowValue DecodeTaggedUnion(JsonObject node, JsonObject schema)
        {
            var tag = FlowCompiler.String(node["tag"]);
            return FlowValue.Object(new Dictionary<string, FlowValue>(StringComparer.Ordinal)
            {
                ["tag"] = FlowValue.Text(tag),
                ["value"] = Decode(node["value"], schema["variants"]![tag]!.AsObject())
            });
        }

        private static FlowValue DecodeDate(JsonNode? node) => FlowValue.Date(DateOnly.ParseExact(FlowCompiler.String(node), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        private static FlowValue DecodeInstant(JsonNode? node) => FlowValue.Instant(DateTimeOffset.Parse(FlowCompiler.String(node), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

        private static JsonObject BindInput(FlowValue value, JsonObject schema, string operation, HashSet<string> metadataPaths)
        {
            CheckBinding(value, schema);
            var bound = BindOperationValue(value, schema, metadataPaths, "input")!.AsObject();
            // The opaque query owner requires a numeric page limit. Filter values
            // and opaque record data retain the business-number string wire form.
            if (operation == "records.list" && value.Fields.TryGetValue("query", out var query) && query.Kind == ValueKind.Object && bound["query"] is JsonObject queryJson &&
                query.Fields.TryGetValue("limit", out var limit)) queryJson["limit"] = limit.Json(true);
            return bound;
        }

        private static JsonNode? BindOperationValue(FlowValue value, JsonObject schema, HashSet<string> metadataPaths, string path)
        {
            if (value.Kind == ValueKind.Missing) throw new BosException("MISSING_VALUE", "Missing cannot cross an operation boundary.");
            if (schema.Count == 0) return value.Json();
            if (value.Kind == ValueKind.Null)
            {
                if (schema["nullable"]?.GetValue<bool>() != true) throw new BosException("CONVERSION_FAILED", "Null is not allowed.", path: path);
                return null;
            }
            if (schema["fields"] is JsonObject fields)
            {
                var result = new JsonObject();
                foreach (var key in value.Fields.Keys)
                    if (!fields.ContainsKey(key)) throw new BosException("CONVERSION_FAILED", "Unknown operation field: " + key);
                foreach (var (key, field) in fields)
                {
                    var definition = field!.AsObject();
                    if (value.Fields.TryGetValue(key, out var child)) result[key] = BindOperationValue(child, definition, metadataPaths, path + "." + key);
                    else if (definition.ContainsKey("default")) result[key] = BindOperationValue(FlowValue.Literal(definition["default"]), definition, metadataPaths, path + "." + key);
                    else if (definition["required"]?.GetValue<bool>() == true) throw new BosException("CONVERSION_FAILED", "Missing required operation field: " + key);
                }
                return result;
            }
            if (schema["items"] is JsonObject item)
            {
                if (value.Items.Length > schema["maxItems"]!.GetValue<int>()) throw Limit("Operation array exceeds its item bound.");
                return new JsonArray(value.Items.Select(child => BindOperationValue(child, item, metadataPaths, path + "[]")).ToArray());
            }
            var scalarSchema = new JsonObject { ["fields"] = new JsonObject { ["value"] = schema.DeepClone() } };
            var normalized = Schema.Normalize(scalarSchema, new JsonObject { ["value"] = value.Json() });
            if (schema["kind"]?.GetValue<string>() == "integer" && metadataPaths.Contains(path))
            {
                long number = FlowValue.ParseInteger(FlowCompiler.String(normalized["value"]));
                if (number is < 1 or > int.MaxValue) throw new BosException("CONVERSION_FAILED", "Metadata revision/version out of range.", path: path);
                return JsonValue.Create((int)number);
            }
            return normalized["value"]?.DeepClone();
        }

        private static void CheckBinding(FlowValue value, JsonObject schema)
        {
            if (value.Kind == ValueKind.Missing) throw new BosException("MISSING_VALUE", "Missing cannot cross a schema boundary.");
            if (value.Kind == ValueKind.Null) return; // Schema owns nullability.
            string kind = schema["kind"]?.GetValue<string>() ?? (schema.ContainsKey("fields") ? "object" : "unknown");
            if (kind == "unknown") return;
            kind = kind switch
            {
                "reference" or "enum" or "userReference" or "attachment" or "time" or "localDateTime" or "duration" or "calendarPeriod" or "offsetDateTime" => "string",
                "percentage" => "decimal",
                "map" or "taggedUnion" or "money" or "quantity" or "geopoint" or "dateRange" or "instantRange" or "polymorphicReference" or "versionedReference" => "object",
                "multiEnum" => "array",
                _ => kind
            };
            string actual = value.Kind.ToString().ToLowerInvariant();
            if (actual != kind && !(actual == "string" && kind is "integer" or "decimal" or "date" or "instant"))
                throw new BosException("TYPE_MISMATCH", "Value kind does not match receiving schema.");
            if (value.Kind == ValueKind.Object && schema["fields"] is JsonObject fields)
                foreach (var (name, field) in value.Fields)
                {
                    if (!fields.ContainsKey(name)) throw new BosException("CONVERSION_FAILED", "Unknown schema field: " + name);
                    CheckBinding(field, fields[name]!.AsObject());
                }
            if (value.Kind == ValueKind.Array && schema["items"] is JsonObject item)
                foreach (var field in value.Items) CheckBinding(field, item);
        }

        private async Task<FlowValue> DecodeOutput(JsonNode? output, JsonObject schema, HashSet<string> metadataPaths, int depth = 0, string path = "output", bool projectedRecords = false)
        {
            Tick(); if (depth > 32) throw Limit("Operation output nesting exceeds limit.");
            if (output is null)
            {
                if (schema.Count != 0 && schema["nullable"]?.GetValue<bool>() != true) throw new BosException("OUTPUT_INVALID", "Unexpected null operation output.");
                return FlowValue.Null;
            }
            // A record is a metadata envelope; data must be decoded using the
            // precise typeVersion, never guessed from string contents or active type.
            if (output is JsonObject record && metadataPaths.Contains(path + ".typeVersion") && metadataPaths.Contains(path + ".revision") &&
                record.ContainsKey("id") && record.ContainsKey("type") && record.ContainsKey("typeVersion") && record.ContainsKey("revision") && record.ContainsKey("data"))
            {
                if (schema["fields"] is JsonObject declared)
                {
                    foreach (var (name, _) in record)
                        if (!declared.ContainsKey(name)) throw new BosException("OUTPUT_INVALID", "Unknown operation output field: " + name);
                    foreach (var (name, field) in declared)
                        if (field?["required"]?.GetValue<bool>() == true && !record.ContainsKey(name) &&
                            !(projectedRecords && name is "deleted" or "createdAt" or "createdBy" or "updatedAt" or "updatedBy"))
                            throw new BosException("OUTPUT_INVALID", "Missing operation output field: " + name);
                }
                string type = FlowCompiler.Text(record, "type");
                FlowCompiler.Only(record, "id", "type", "typeVersion", "revision", "deleted", "data", "createdAt", "createdBy", "updatedAt", "updatedBy");
                int version = FlowCompiler.PositiveInt(record["typeVersion"]);
                if (typeDependencies[type] is not JsonArray accepted || !accepted.Any(x => FlowCompiler.PositiveInt(x) == version))
                    throw new BosException("DEPENDENCY_UNAVAILABLE", "Returned record type version is not pinned by the flow.");
                string key = type + ":" + version;
                if (!typeSchemas.TryGetValue(key, out var definition))
                {
                    var published = await uow.Documents.GetAsync("typeVersions", key, ct) ?? throw new BosException("DEPENDENCY_UNAVAILABLE", "Record type version is unavailable.");
                    definition = published["definition"]?.AsObject() ?? throw new BosException("DEPENDENCY_UNAVAILABLE", "Record type definition is unavailable.");
                    typeSchemas[key] = (JsonObject)definition.DeepClone();
                }
                var dataSchema = definition;
                if (projectedRecords)
                {
                    dataSchema = (JsonObject)definition.DeepClone();
                    // select is top-level only. Validate each provided value against
                    // its exact version, but neither require nor default absent fields.
                    foreach (var field in dataSchema["fields"]!.AsObject()) field.Value!["required"] = false;
                }
                var data = Schema.Normalize(dataSchema, FlowCompiler.Obj(record, "data"), false);
                var fields = new Dictionary<string, FlowValue>(StringComparer.Ordinal);
                foreach (var (name, node) in record)
                {
                    Tick();
                    fields[name] = name switch
                    {
                        "data" => Decode(data, dataSchema),
                        "revision" or "typeVersion" => FlowValue.Integer(FlowCompiler.PositiveInt(node)),
                        "createdAt" or "updatedAt" => DecodeMetadataInstant(node),
                        _ => FlowValue.Literal(node)
                    };
                }
                return FlowValue.Object(fields);
            }
            string kind = schema["kind"]?.GetValue<string>() ?? (schema.ContainsKey("fields") ? "object" : "unknown");
            if (output is JsonObject && kind is "map" or "taggedUnion" or "money" or "quantity" or "geopoint" or "dateRange" or "instantRange" or "polymorphicReference" or "versionedReference"
                || output is JsonArray && kind == "multiEnum")
            {
                var compositeSchema = new JsonObject { ["fields"] = new JsonObject { ["value"] = schema.DeepClone() } };
                var normalizedComposite = Schema.Normalize(compositeSchema, new JsonObject { ["value"] = output.DeepClone() }, false);
                return Decode(normalizedComposite["value"], schema);
            }
            if (output is JsonObject obj)
            {
                if (kind is not ("object" or "unknown")) throw new BosException("OUTPUT_INVALID", "Operation output kind mismatch.");
                var fields = schema["fields"] as JsonObject;
                var decoded = new Dictionary<string, FlowValue>(StringComparer.Ordinal);
                foreach (var (name, value) in obj)
                {
                    if (fields is not null && !fields.ContainsKey(name)) throw new BosException("OUTPUT_INVALID", "Unknown operation output field: " + name);
                    decoded[name] = await DecodeOutput(value, fields?[name] as JsonObject ?? new JsonObject(), metadataPaths, depth + 1, path + "." + name, projectedRecords);
                }
                if (fields is not null) foreach (var (name, field) in fields)
                    if (field?["required"]?.GetValue<bool>() == true && !obj.ContainsKey(name)) throw new BosException("OUTPUT_INVALID", "Missing required output field: " + name);
                return FlowValue.Object(decoded);
            }
            if (output is JsonArray array)
            {
                if (kind is not ("array" or "unknown")) throw new BosException("OUTPUT_INVALID", "Operation output kind mismatch.");
                if (schema["maxItems"] is JsonValue max && array.Count > max.GetValue<int>()) throw new BosException("OUTPUT_INVALID", "Operation array exceeds schema bound.");
                var items = new List<FlowValue>(); int bytes = 2;
                foreach (var value in array)
                {
                    var decoded = await DecodeOutput(value, schema["items"] as JsonObject ?? new JsonObject(), metadataPaths, depth + 1, path + "[]", projectedRecords);
                    bytes += decoded.Bytes + 1; if (bytes > 262144) throw Limit("Operation array exceeds allocation bound.");
                    items.Add(decoded);
                }
                return FlowValue.Array(items);
            }
            if (kind == "integer" && metadataPaths.Contains(path))
            {
                if (output.GetValueKind() != System.Text.Json.JsonValueKind.Number ||
                    !int.TryParse(output.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int integer) || integer < 1)
                    throw new BosException("OUTPUT_INVALID", "Metadata integer is out of range.");
                output = JsonValue.Create(integer.ToString(CultureInfo.InvariantCulture));
            }
            if (kind == "unknown") return FlowValue.Literal(output);
            // Use the same authoritative codec for scalar constraints (precision,
            // scale, enum choices, string bounds and nullability).
            var scalarSchema = new JsonObject { ["fields"] = new JsonObject { ["value"] = schema.DeepClone() } };
            var normalized = Schema.Normalize(scalarSchema, new JsonObject { ["value"] = output.DeepClone() }, false);
            return Decode(normalized["value"], schema);
        }
        private FlowValue DecodeMetadataInstant(JsonNode? node)
        {
            var field = new JsonObject { ["kind"] = "instant", ["required"] = true };
            var schema = new JsonObject { ["fields"] = new JsonObject { ["value"] = field } };
            var normalized = Schema.Normalize(schema, new JsonObject { ["value"] = node?.DeepClone() }, false);
            return Decode(normalized["value"], field);
        }
    }
    private static BosException Limit(string message) => new("EXECUTION_LIMIT", message);
}
