using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;

namespace Bos.Application;

/// <summary>A closed startup registry. No request-controlled reflection or assembly loading.</summary>
public sealed class OperationRegistry : IOperationRegistry
{
    private readonly Dictionary<(string, int), IOperation> operations = new();
    public OperationRegistry()
    {
        foreach (var name in new[] { "create", "get", "update", "delete", "validate", "list", "count", "getRevision", "getHistory" })
            Register(new RecordOperation(name));
    }
    public IReadOnlyList<OperationDescriptor> Descriptors => operations.Values.Select(x => Clone(x.Descriptor))
        .OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.ContractVersion).ToArray();
    public void Register(IOperation operation)
    {
        var descriptor = operation.Descriptor;
        if (descriptor.Name.StartsWith("records.", StringComparison.Ordinal) && operation is not RecordOperation)
            throw new InvalidOperationException("Built-in record contracts are sealed; register a separate business operation.");
        if (descriptor.ContractVersion <= 0 || descriptor.Mode is not ("read" or "write") || string.IsNullOrEmpty(descriptor.Name))
            throw new InvalidOperationException("Invalid operation descriptor.");
        OperationBinding.ValidateSchema(descriptor.InputSchema);
        OperationBinding.ValidateSchema(descriptor.OutputSchema);
        if (!operations.TryAdd((descriptor.Name, descriptor.ContractVersion), operation))
            throw new InvalidOperationException($"Duplicate operation contract: {descriptor.Name}/{descriptor.ContractVersion}.");
    }
    public IOperation Resolve(string name, int? version = null)
    {
        var exact = version ?? operations.Keys.Where(x => x.Item1 == name).Select(x => x.Item2).DefaultIfEmpty(0).Max();
        return operations.GetValueOrDefault((name, exact)) ?? throw new BosException("DEPENDENCY_UNAVAILABLE", "Operation contract is unavailable.", 409);
    }
    private static OperationDescriptor Clone(OperationDescriptor d) => d with
    {
        InputSchema = (JsonObject)d.InputSchema.DeepClone(), OutputSchema = (JsonObject)d.OutputSchema.DeepClone(),
        RequiredGrants = d.RequiredGrants.ToArray(), Capabilities = d.Capabilities.ToArray(), MetadataPaths = d.MetadataPaths?.ToArray()
    };
    private sealed class RecordOperation(string suffix) : IOperation
    {
        public OperationDescriptor Descriptor
        {
            get
            {
                var input = new JsonObject { ["type"] = Text() };
                if (suffix is "get" or "update" or "delete" or "getRevision" or "getHistory") input["id"] = Text();
                if (suffix is "update" or "delete") input["expectedRevision"] = Integer();
                if (suffix == "getRevision") input["revision"] = Integer();
                if (suffix is "create" or "validate") input["data"] = new JsonObject();
                if (suffix == "update") input["patch"] = new JsonObject();
                if (suffix == "validate") input["typeVersion"] = Integer(false);
                if (suffix == "list") input["query"] = new JsonObject();
                if (suffix == "count") input["filter"] = new JsonObject();
                if (suffix == "getHistory") input["query"] = new JsonObject();
                JsonObject output = suffix switch
                {
                    "list" => new() { ["fields"] = new JsonObject { ["items"] = new JsonObject { ["kind"] = "array", ["maxItems"] = 200, ["required"] = true, ["items"] = RecordSchema() }, ["nextCursor"] = new JsonObject { ["kind"] = "string", ["nullable"] = true, ["required"] = true } } },
                    "count" => new() { ["fields"] = new JsonObject { ["count"] = Text() } },
                    "getHistory" => new() { ["fields"] = new JsonObject { ["items"] = new JsonObject { ["kind"] = "array", ["maxItems"] = 200, ["required"] = true, ["items"] = RecordSchema() }, ["nextCursor"] = new JsonObject { ["kind"] = "string", ["nullable"] = true, ["required"] = true } } },
                    "validate" => new(),
                    "delete" => new() { ["fields"] = new JsonObject { ["id"] = Text(), ["type"] = Text(), ["revision"] = Integer(), ["deleted"] = new JsonObject { ["kind"] = "boolean", ["required"] = true } } },
                    _ => RecordSchema()
                };
                return new("records." + suffix, 1, "Typed record " + suffix, new JsonObject { ["fields"] = input }, output,
                    suffix is "create" or "update" or "delete" ? "write" : "read", suffix is not ("getRevision" or "getHistory"), [], [],
                    ["input.expectedRevision", "input.typeVersion", "input.revision", "output.typeVersion", "output.revision", "output.items[].typeVersion", "output.items[].revision"]);
            }
        }
        public Task<JsonNode?> ExecuteAsync(InvocationContext context, IUnitOfWork uow, JsonObject input, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.CallAsync("records." + suffix, 1, input, context, uow, ct);
    }
    internal static JsonObject Text(bool required = true) => new() { ["kind"] = "string", ["required"] = required };
    internal static JsonObject Integer(bool required = true) => new() { ["kind"] = "integer", ["required"] = required, ["minimum"] = "1", ["maximum"] = "2147483647" };
    internal static JsonObject RecordSchema() => new()
    {
        ["kind"] = "object", ["fields"] = new JsonObject
        {
            ["id"] = Text(), ["type"] = Text(), ["typeVersion"] = Integer(), ["revision"] = Integer(),
            ["deleted"] = new JsonObject { ["kind"] = "boolean", ["required"] = true }, ["data"] = new JsonObject(),
            ["createdAt"] = Text(), ["createdBy"] = Text(), ["updatedAt"] = Text(), ["updatedBy"] = Text()
        }
    };
}

/// <summary>Operation schemas share codecs while metadata integers retain their JSON-number wire form.</summary>
internal static class OperationBinding
{
    public static void ValidateSchema(JsonObject schema)
    {
        // Dynamic record/query slots are closed and validated by their owning application command.
        if (schema.Count == 0) return;
        var concrete = Concrete(schema);
        if (concrete["fields"] is JsonObject fields)
        {
            var report = Schema.ValidateDefinition(new JsonObject { ["fields"] = fields.DeepClone() }, true);
            if (!report.Valid) throw new InvalidOperationException("Invalid operation data schema: " + string.Join("; ", report.Errors.Select(x => x.Path + ": " + x.Message)));
        }
    }
    private static JsonObject Concrete(JsonObject schema)
    {
        if (schema.Count == 0) return new() { ["kind"] = "string", ["nullable"] = true };
        var result = (JsonObject)schema.DeepClone();
        if (result["fields"] is JsonObject fields)
            foreach (var pair in fields.ToArray()) fields[pair.Key] = Concrete(pair.Value!.AsObject());
        if (result["items"] is JsonObject item) result["items"] = Concrete(item);
        return result;
    }
    public static JsonObject Bind(JsonObject schema, JsonObject input, IReadOnlyList<string>? metadataPaths, string side)
    {
        if (side is not ("input" or "output")) throw new ArgumentException("Expected an input or output schema prefix.", nameof(side));
        return BindObject(schema, input, new HashSet<string>(metadataPaths ?? [], StringComparer.Ordinal), side, side == "output");
    }
    private static JsonObject BindObject(JsonObject schema, JsonObject input, HashSet<string> metadataPaths, string path, bool output, bool defaultValue = false)
    {
        if (schema["fields"] is not JsonObject fields) return (JsonObject)input.DeepClone();
        JsonArgs.Only(input, fields.Select(x => x.Key).ToArray());
        var result = new JsonObject();
        foreach (var (name, node) in fields)
        {
            var field = node!.AsObject();
            string fieldPath = path + "." + name;
            if (!input.TryGetPropertyValue(name, out var value))
            {
                if (!output && field.TryGetPropertyValue("default", out var fallback))
                {
                    result[name] = BindField(field, fallback, metadataPaths, fieldPath, false, true);
                    continue;
                }
                if (field["required"]?.GetValue<bool>() == true) throw new BosException(output ? "OUTPUT_INVALID" : "INVALID_REQUEST", "Required field is missing.", output ? 422 : 400, name);
                continue;
            }
            result[name] = BindField(field, value, metadataPaths, fieldPath, output, defaultValue);
        }
        return result;
    }
    private static JsonNode? BindField(JsonObject field, JsonNode? value, HashSet<string> metadataPaths, string path, bool output, bool defaultValue = false)
    {
        if (field.Count == 0) return value?.DeepClone();
        if (value is null)
        {
            if (field["nullable"]?.GetValue<bool>() == true) return null;
            throw new BosException("CONVERSION_FAILED", "Null is not allowed.", path: path);
        }
        if (field["kind"]?.GetValue<string>() == "integer" && metadataPaths.Contains(path))
        {
            // Defaults use the data-schema codec; supplied metadata must already be
            // a JSON number. Field names never select the metadata wire format.
            if (defaultValue)
            {
                var normalizedDefault = NormalizeScalar(field, value, output);
                if (!int.TryParse(normalizedDefault!.GetValue<string>(), System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out int metadataDefault))
                    throw new BosException("CONVERSION_FAILED", "Metadata must be a positive Int32.", path: path);
                value = JsonValue.Create(metadataDefault);
            }
            var number = JsonArgs.Int(new JsonObject { ["value"] = value.DeepClone() }, "value");
            if (number <= 0) throw new BosException("CONVERSION_FAILED", "Metadata must be a positive Int32.", path: path);
            _ = NormalizeScalar(field, JsonValue.Create(number.ToString(System.Globalization.CultureInfo.InvariantCulture)), output);
            return JsonValue.Create(number);
        }
        if (value is JsonObject obj && field["fields"] is JsonObject) return BindObject(field, obj, metadataPaths, path, output, defaultValue);
        if (value is JsonArray array && field["items"] is JsonObject item)
        {
            if (array.Count > (field["maxItems"]?.GetValue<int>() ?? 200)) throw new BosException("EXECUTION_LIMIT", "Array exceeds its item bound.", path: path);
            return new JsonArray(array.Select(x => BindField(item, x, metadataPaths, path + "[]", output, defaultValue)).ToArray());
        }
        return NormalizeScalar(field, value, output);
    }
    private static JsonNode? NormalizeScalar(JsonObject field, JsonNode? value, bool output)
    {
        var normalized = Schema.Normalize(new JsonObject { ["fields"] = new JsonObject { ["value"] = field.DeepClone() } },
            new JsonObject { ["value"] = value?.DeepClone() }, !output);
        return normalized["value"]?.DeepClone();
    }
}
