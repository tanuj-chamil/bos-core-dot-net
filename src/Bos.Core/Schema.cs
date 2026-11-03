using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bos.Contracts;

namespace Bos.Core;

/// <summary>Closed field grammar, pure local value validation and migration-free evolution checks.</summary>
public static class Schema
{
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    { "string", "integer", "decimal", "boolean", "date", "instant", "enum", "reference", "object", "array" };

    public static ValidationReport ValidateDefinition(JsonObject definition, bool dataSchema = false)
    {
        var errors = new Errors("INVALID_SCHEMA");
        try { CanonicalJson.Serialize(definition); }
        catch (BosException ex) { errors.Add("", ex.Message); return errors.Report(); }
        Allowed(definition, dataSchema ? ["fields"] : ["name", "description", "fields", "accessMode", "allowedOperations"], "", errors);
        if (!dataSchema)
        {
            NameProperty(definition, "name", "name", errors);
            if (definition.ContainsKey("description")) TextProperty(definition, "description", "description", errors);
            var mode = OptionalText(definition, "accessMode", "direct", "accessMode", errors);
            if (mode is not ("direct" or "operationsOnly")) errors.Add("accessMode", "Expected direct or operationsOnly.");
            if (definition.ContainsKey("allowedOperations")) Names(definition["allowedOperations"], "allowedOperations", errors);
            if (mode == "operationsOnly" && definition["allowedOperations"] is not JsonArray { Count: > 0 })
                errors.Add("allowedOperations", "An operationsOnly type requires allowed operations.");
        }
        var count = 0;
        Fields(definition["fields"], "fields", 1, true, dataSchema, ref count, errors);
        // Defaults are checked only after the complete structural grammar is known to be valid.
        if (errors.Count == 0) ValidateDefaults((JsonObject)definition["fields"]!, "fields", errors);
        return errors.Report();
    }

    public static JsonObject Normalize(JsonObject schema, JsonObject data, bool applyDefaults = true)
    {
        var definition = ValidateDefinition(schema, !schema.ContainsKey("name"));
        if (!definition.Valid) throw Failure("INVALID_SCHEMA", definition);
        // Bound all supplied data, including unknown fields, before walking or cloning it.
        CanonicalJson.Serialize(data);
        var errors = new Errors("CONVERSION_FAILED");
        var count = 0;
        var result = ObjectValue((JsonObject)schema["fields"]!, data, "", applyDefaults, ref count, errors);
        try { CanonicalJson.Serialize(result); }
        catch (BosException ex) { errors.Add("", ex.Message); }
        if (errors.Count != 0) throw Failure("CONVERSION_FAILED", errors.Report());
        return (JsonObject)CanonicalJson.Normalize(result)!;
    }

    public static ValidationReport ValidateData(JsonObject schema, JsonObject data, bool applyDefaults = true)
    {
        try { Normalize(schema, data, applyDefaults); return new(true, []); }
        catch (BosException ex)
        {
            return new(false, ex.Details.Count > 0 ? ex.Details : [new(ex.Path, ex.Code, ex.Message)]);
        }
    }

    public static void CheckEvolution(JsonObject old, JsonObject next)
    {
        foreach (var schema in new[] { old, next })
        {
            var report = ValidateDefinition(schema, !schema.ContainsKey("name"));
            if (!report.Valid) throw Failure("INVALID_SCHEMA", report);
        }
        if (Text(old, "name") != Text(next, "name")) Incompatible("name");
        if (Text(old, "accessMode", "direct") != Text(next, "accessMode", "direct")) Incompatible("accessMode");
        if (!SameNames(old["allowedOperations"], next["allowedOperations"])) Incompatible("allowedOperations");
        EvolveFields((JsonObject)old["fields"]!, (JsonObject)next["fields"]!, "fields", false);
    }

    private static void Fields(JsonNode? node, string path, int depth, bool top, bool dataSchema, ref int count, Errors errors)
    {
        if (depth > 8) { errors.Add(path, "Schema nesting exceeds 8."); return; }
        if (node is not JsonObject fields) { errors.Add(path, "Expected a fields object."); return; }
        foreach (var pair in fields)
        {
            if (++count > 200) { errors.Add(path, "Schema exceeds 200 total fields."); return; }
            var fieldPath = Join(path, pair.Key);
            CheckName(pair.Key, fieldPath, errors);
            if (pair.Value is not JsonObject field) { errors.Add(fieldPath, "Expected a field definition object."); continue; }
            Field(field, fieldPath, depth, top, dataSchema, ref count, errors);
        }
    }

    private static void Field(JsonObject field, string path, int depth, bool top, bool dataSchema, ref int count, Errors errors)
    {
        if (depth > 8) { errors.Add(path, "Schema nesting exceeds 8."); return; }
        var kind = TextProperty(field, "kind", Join(path, "kind"), errors);
        if (!Kinds.Contains(kind)) { errors.Add(Join(path, "kind"), "Unknown field kind."); return; }
        var properties = new HashSet<string>(["kind", "required", "nullable", "default"], StringComparer.Ordinal);
        if (!dataSchema)
        {
            properties.Add("managedBy");
            if (top && kind is not ("object" or "array")) properties.Add("indexed");
            if (top && kind is not ("object" or "array" or "reference")) properties.Add("unique");
        }
        foreach (var property in kind switch
        {
            "string" => new[] { "maxLength" },
            "integer" => ["minimum", "maximum"],
            "decimal" => ["minimum", "maximum", "precision", "scale"],
            "enum" => ["values"],
            "reference" => ["targetType"],
            "object" => ["fields"],
            "array" => ["items", "maxItems"],
            _ => []
        }) properties.Add(property);
        Allowed(field, properties, path, errors);
        foreach (var flag in new[] { "required", "nullable", "indexed", "unique" })
            if (field.ContainsKey(flag)) BooleanProperty(field, flag, Join(path, flag), errors);
        if (Flag(field, "unique") && field.ContainsKey("indexed") && !Flag(field, "indexed"))
            errors.Add(Join(path, "indexed"), "Unique implies indexed.");
        if (field.ContainsKey("managedBy")) Names(field["managedBy"], Join(path, "managedBy"), errors);
        switch (kind)
        {
            case "string":
                if (field.ContainsKey("maxLength")) IntProperty(field, "maxLength", 0, 16384, path, errors);
                break;
            case "decimal":
                var precision = IntProperty(field, "precision", 1, 28, path, errors);
                var scale = IntProperty(field, "scale", 0, 12, path, errors);
                if (scale > precision) errors.Add(Join(path, "scale"), "Scale cannot exceed precision.");
                NumericBounds(field, kind, path, errors);
                break;
            case "integer": NumericBounds(field, kind, path, errors); break;
            case "enum":
                if (field["values"] is not JsonArray { Count: > 0 } values) errors.Add(Join(path, "values"), "Expected a nonempty enum value list.");
                else
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < values.Count; i++)
                    {
                        try
                        {
                            if (values[i] is null) throw ValueCodec.Invalid("Enum choices must be strings.");
                            if (!seen.Add(ValueCodec.Scalar("enum", values[i]!))) errors.Add($"{path}.values[{i}]", "Duplicate normalized enum choice.");
                        }
                        catch (BosException ex) { errors.Add($"{path}.values[{i}]", ex.Message); }
                    }
                }
                break;
            case "reference": NameProperty(field, "targetType", Join(path, "targetType"), errors); break;
            case "object": Fields(field["fields"], Join(path, "fields"), depth + 1, false, dataSchema, ref count, errors); break;
            case "array":
                IntProperty(field, "maxItems", 0, 200, path, errors);
                if (field["items"] is JsonObject items) Field(items, Join(path, "items"), depth + 1, false, dataSchema, ref count, errors);
                else errors.Add(Join(path, "items"), "Expected an item field definition.");
                break;
        }
    }

    private static void NumericBounds(JsonObject field, string kind, string path, Errors errors)
    {
        BigInteger? minimum = null;
        BigInteger? maximum = null;
        foreach (var name in new[] { "minimum", "maximum" })
        {
            if (!field.ContainsKey(name)) continue;
            try
            {
                if (field[name] is null) throw ValueCodec.Invalid("A bound cannot be null.");
                var value = NumericValue(field, kind, field[name]!);
                if (name == "minimum") minimum = value; else maximum = value;
            }
            catch (BosException ex) { errors.Add(Join(path, name), ex.Message); }
        }
        if (minimum.HasValue && maximum.HasValue && minimum > maximum) errors.Add(path, "Minimum exceeds maximum.");
    }

    private static BigInteger NumericValue(JsonObject field, string kind, JsonNode node)
    {
        var text = ValueCodec.String(node);
        if (kind == "integer") return ValueCodec.Integer(text);
        var parsed = ValueCodec.Decimal(text);
        var precision = MetadataInt(field["precision"], 0);
        var scale = MetadataInt(field["scale"], -1);
        if (precision is < 1 or > 28 || scale is < 0 or > 12 || scale > precision) throw ValueCodec.Invalid("Invalid decimal precision/scale.");
        var value = ValueCodec.Rescale(parsed.Value, parsed.Scale, scale);
        if (BigInteger.Abs(value) >= BigInteger.Pow(10, precision)) throw ValueCodec.Invalid("Decimal exceeds declared precision.");
        return value;
    }

    private static void ValidateDefaults(JsonObject fields, string path, Errors errors)
    {
        foreach (var pair in fields) Default((JsonObject)pair.Value!, Join(path, pair.Key), errors);
    }

    private static void Default(JsonObject field, string path, Errors errors)
    {
        if (field.ContainsKey("default"))
        {
            var count = 0;
            Value(field, field["default"], Join(path, "default"), true, ref count, errors);
        }
        if (Text(field, "kind") == "object") ValidateDefaults((JsonObject)field["fields"]!, Join(path, "fields"), errors);
        if (Text(field, "kind") == "array") Default((JsonObject)field["items"]!, Join(path, "items"), errors);
    }

    private static JsonObject ObjectValue(JsonObject fields, JsonObject data, string path, bool applyDefaults, ref int count, Errors errors)
    {
        var result = new JsonObject();
        foreach (var pair in data)
            if (!fields.ContainsKey(pair.Key)) errors.Add(Join(path, pair.Key), "Unknown field.", "UNKNOWN_FIELD");
        foreach (var pair in fields)
        {
            var field = (JsonObject)pair.Value!;
            var fieldPath = Join(path, pair.Key);
            if (data.TryGetPropertyValue(pair.Key, out var value))
                result[pair.Key] = Value(field, value, fieldPath, applyDefaults, ref count, errors);
            else if (applyDefaults && field.ContainsKey("default"))
                result[pair.Key] = Value(field, field["default"], fieldPath, applyDefaults, ref count, errors);
            else if (Flag(field, "required")) errors.Add(fieldPath, "Required field is missing.");
        }
        return result;
    }

    private static JsonNode? Value(JsonObject field, JsonNode? value, string path, bool applyDefaults, ref int count, Errors errors)
    {
        var kind = Text(field, "kind");
        if (value is null || kind is not ("object" or "array"))
            if (++count > 2000) { errors.Add(path, "Record exceeds 2,000 scalars."); return null; }
        if (value is null)
        {
            if (!Flag(field, "nullable")) errors.Add(path, "Explicit null requires nullable.");
            return null;
        }
        try
        {
            switch (kind)
            {
                case "object":
                    if (value is not JsonObject obj) throw ValueCodec.Invalid("Expected an object.");
                    return ObjectValue((JsonObject)field["fields"]!, obj, path, applyDefaults, ref count, errors);
                case "array":
                    if (value is not JsonArray array) throw ValueCodec.Invalid("Expected an array.");
                    if (array.Count > MetadataInt(field["maxItems"], 200) || array.Count > 200) throw ValueCodec.Invalid("Array exceeds maxItems.");
                    var result = new JsonArray();
                    for (var i = 0; i < array.Count; i++) result.Add(Value((JsonObject)field["items"]!, array[i], $"{path}[{i}]", applyDefaults, ref count, errors));
                    return result;
                case "integer":
                case "decimal":
                    var number = NumericValue(field, kind, value);
                    foreach (var bound in new[] { "minimum", "maximum" })
                    {
                        if (!field.ContainsKey(bound)) continue;
                        var limit = NumericValue(field, kind, field[bound]!);
                        if (bound == "minimum" ? number < limit : number > limit) throw ValueCodec.Invalid("Value violates " + bound + ".");
                    }
                    return JsonValue.Create(kind == "integer" ? number.ToString(CultureInfo.InvariantCulture) : ValueCodec.Format(number, MetadataInt(field["scale"], 0)));
                default:
                    var canonical = ValueCodec.Scalar(kind, value);
                    if (kind == "string" && canonical.EnumerateRunes().Count() > MetadataInt(field["maxLength"], 16384)) throw ValueCodec.Invalid("String exceeds maxLength.");
                    if (kind == "enum" && !((JsonArray)field["values"]!).Any(v => ValueCodec.String(v!) == canonical)) throw ValueCodec.Invalid("Value is not an enum choice.");
                    return kind == "boolean" ? JsonValue.Create(canonical == "true") : JsonValue.Create(canonical);
            }
        }
        catch (BosException ex) { errors.Add(path, ex.Message); return null; }
    }

    private static void EvolveFields(JsonObject old, JsonObject next, string path, bool indexedParent)
    {
        foreach (var pair in old)
        {
            var fieldPath = Join(path, pair.Key);
            if (next[pair.Key] is not JsonObject newField) Incompatible(fieldPath);
            EvolveField((JsonObject)pair.Value!, (JsonObject)next[pair.Key]!, fieldPath, indexedParent);
        }
        foreach (var pair in next)
        {
            if (old.ContainsKey(pair.Key)) continue;
            var field = (JsonObject)pair.Value!;
            if (indexedParent || Flag(field, "required") || Indexed(field)) Incompatible(Join(path, pair.Key));
        }
    }

    private static void EvolveField(JsonObject old, JsonObject next, string path, bool indexedParent)
    {
        var kind = Text(old, "kind");
        if (kind != Text(next, "kind")) Incompatible(Join(path, "kind"));
        if (Indexed(old) != Indexed(next) || Flag(old, "unique") != Flag(next, "unique")) Incompatible(path);
        if (!SameNames(old["managedBy"], next["managedBy"])) Incompatible(Join(path, "managedBy"));
        var indexed = indexedParent || Indexed(old);
        if (Flag(old, "required") != Flag(next, "required") && (indexed || Flag(next, "required"))) Incompatible(Join(path, "required"));
        if (Flag(old, "nullable") != Flag(next, "nullable") && (indexed || !Flag(next, "nullable"))) Incompatible(Join(path, "nullable"));
        // Defaults influence future creates; they are not a relaxation of a published constraint.
        if (old.ContainsKey("default") != next.ContainsKey("default") || !SameDefault(old, next)) Incompatible(Join(path, "default"));
        switch (kind)
        {
            case "decimal":
                if (MetadataInt(old["precision"], 0) != MetadataInt(next["precision"], 0)
                    || MetadataInt(old["scale"], 0) != MetadataInt(next["scale"], 0)) Incompatible(path);
                goto case "integer";
            case "integer":
                foreach (var bound in new[] { "minimum", "maximum" })
                {
                    if (!old.ContainsKey(bound)) { if (next.ContainsKey(bound)) Incompatible(Join(path, bound)); continue; }
                    if (!next.ContainsKey(bound)) { if (indexed) Incompatible(Join(path, bound)); continue; }
                    var before = NumericValue(old, kind, old[bound]!);
                    var after = NumericValue(next, kind, next[bound]!);
                    if (indexed ? before != after : bound == "minimum" ? after > before : after < before) Incompatible(Join(path, bound));
                }
                break;
            case "string":
                var oldLength = MetadataInt(old["maxLength"], 16384);
                var newLength = MetadataInt(next["maxLength"], 16384);
                if (indexed ? oldLength != newLength : newLength < oldLength) Incompatible(Join(path, "maxLength"));
                break;
            case "enum":
                var choices = ((JsonArray)next["values"]!).Select(v => ValueCodec.String(v!)).ToHashSet(StringComparer.Ordinal);
                if (((JsonArray)old["values"]!).Any(v => !choices.Contains(ValueCodec.String(v!)))) Incompatible(Join(path, "values"));
                break;
            case "reference": if (Text(old, "targetType") != Text(next, "targetType")) Incompatible(Join(path, "targetType")); break;
            case "object": EvolveFields((JsonObject)old["fields"]!, (JsonObject)next["fields"]!, Join(path, "fields"), indexed); break;
            case "array":
                if (MetadataInt(next["maxItems"], 0) < MetadataInt(old["maxItems"], 0)) Incompatible(Join(path, "maxItems"));
                EvolveField((JsonObject)old["items"]!, (JsonObject)next["items"]!, Join(path, "items"), indexed);
                break;
        }
    }

    private static bool SameDefault(JsonObject old, JsonObject next)
    {
        if (!old.ContainsKey("default")) return true;
        var errors = new Errors("INVALID_SCHEMA");
        var count = 0;
        var before = Value(old, old["default"], "", true, ref count, errors);
        count = 0;
        var after = Value(next, next["default"], "", true, ref count, errors);
        return errors.Count == 0 && CanonicalJson.Serialize(before) == CanonicalJson.Serialize(after);
    }

    private static bool SameNames(JsonNode? left, JsonNode? right) =>
        (left as JsonArray ?? []).Select(v => v!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
        .SetEquals((right as JsonArray ?? []).Select(v => v!.GetValue<string>()));

    private static void Names(JsonNode? node, string path, Errors errors)
    {
        if (node is not JsonArray names) { errors.Add(path, "Expected an operation name array."); return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i] is null || names[i]!.GetValueKind() != JsonValueKind.String) { errors.Add($"{path}[{i}]", "Expected an operation name."); continue; }
            var name = names[i]!.GetValue<string>();
            CheckName(name, $"{path}[{i}]", errors);
            if (!seen.Add(name)) errors.Add($"{path}[{i}]", "Duplicate operation name.");
        }
    }

    private static void Allowed(JsonObject obj, IEnumerable<string> allowed, string path, Errors errors)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var pair in obj) if (!set.Contains(pair.Key)) errors.Add(Join(path, pair.Key), "Unknown definition property.");
    }

    private static string TextProperty(JsonObject obj, string property, string path, Errors errors)
    {
        if (obj[property] is null || obj[property]!.GetValueKind() != JsonValueKind.String) { errors.Add(path, "Expected a string."); return ""; }
        try { return ValueCodec.Scalar("string", obj[property]!); }
        catch (BosException ex) { errors.Add(path, ex.Message); return ""; }
    }

    private static string OptionalText(JsonObject obj, string name, string fallback, string path, Errors errors) =>
        obj.ContainsKey(name) ? TextProperty(obj, name, path, errors) : fallback;

    private static void NameProperty(JsonObject obj, string name, string path, Errors errors) => CheckName(TextProperty(obj, name, path, errors), path, errors);

    private static void CheckName(string name, string path, Errors errors)
    {
        try { JsonArgs.Name(name); }
        catch (BosException ex) { errors.Add(path, ex.Message); }
    }

    private static void BooleanProperty(JsonObject obj, string property, string path, Errors errors)
    {
        if (obj[property] is null || obj[property]!.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) errors.Add(path, "Expected a boolean.");
    }

    private static int IntProperty(JsonObject obj, string name, int minimum, int maximum, string path, Errors errors)
    {
        var result = MetadataInt(obj[name], int.MinValue);
        if (result < minimum || result > maximum) errors.Add(Join(path, name), $"Expected an integer from {minimum} to {maximum}.");
        return result;
    }

    private static int MetadataInt(JsonNode? node, int fallback) =>
        ValueCodec.TryMetadataInt(node, out var result) ? result : fallback;
    private static bool Flag(JsonObject obj, string name) => obj[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private static bool Indexed(JsonObject field) => Flag(field, "indexed") || Flag(field, "unique");
    private static string Text(JsonObject obj, string name, string fallback = "") => obj[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : fallback;
    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;
    private static BosException Failure(string code, ValidationReport report) => new(code, "Schema or data validation failed.") { Details = report.Errors };
    private static void Incompatible(string path) => throw new BosException("SCHEMA_VERSION_MISMATCH", "Incompatible schema evolution requires an explicit migration.", 409, path);

    private sealed class Errors(string code)
    {
        private readonly List<FieldError> _items = [];
        public int Count => _items.Count;
        public void Add(string path, string message, string? errorCode = null)
        {
            if (_items.Count < 100) _items.Add(new(path, errorCode ?? code, message));
        }
        public ValidationReport Report() => new(_items.Count == 0, _items.ToArray());
    }
}
