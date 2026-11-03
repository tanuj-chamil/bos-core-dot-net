using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bos.Contracts;

namespace Bos.Core;

/// <summary>Closed field grammar, pure local value validation and migration-free evolution checks.</summary>
public static class Schema
{
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "string", "integer", "decimal", "boolean", "date", "instant", "enum", "reference", "object", "array",
        "time", "localDateTime", "duration", "calendarPeriod", "offsetDateTime", "map", "taggedUnion", "money",
        "percentage", "quantity", "geopoint", "dateRange", "instantRange", "multiEnum", "polymorphicReference",
        "versionedReference", "userReference", "attachment"
    };
    private static readonly HashSet<string> StringFormats = new(StringComparer.Ordinal)
    { "email", "phone", "url", "uuid", "uri", "slug", "ipAddress", "hostname", "countryCode", "currencyCode", "locale", "timeZone" };

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
        if (errors.Count == 0) ValidateDefaults((JsonObject)definition["fields"]!, "fields", errors);
        return errors.Report();
    }

    public static JsonObject Normalize(JsonObject schema, JsonObject data, bool applyDefaults = true)
    {
        var definition = ValidateDefinition(schema, !schema.ContainsKey("name"));
        if (!definition.Valid) throw Failure("INVALID_SCHEMA", definition);
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
        catch (BosException ex) { return new(false, ex.Details.Count > 0 ? ex.Details : [new(ex.Path, ex.Code, ex.Message)]); }
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
            if (top && CanIndex(kind)) properties.Add("indexed");
            if (top && CanUnique(kind)) properties.Add("unique");
        }
        foreach (var property in KindProperties(kind)) properties.Add(property);
        Allowed(field, properties, path, errors);
        foreach (var flag in new[] { "required", "nullable", "indexed", "unique", "uniqueItems" })
            if (field.ContainsKey(flag)) BooleanProperty(field, flag, Join(path, flag), errors);
        if (Flag(field, "unique") && field.ContainsKey("indexed") && !Flag(field, "indexed")) errors.Add(Join(path, "indexed"), "Unique implies indexed.");
        if (field.ContainsKey("managedBy")) Names(field["managedBy"], Join(path, "managedBy"), errors);

        switch (kind)
        {
            case "string":
            {
                var min = field.ContainsKey("minLength") ? IntProperty(field, "minLength", 0, 16384, path, errors) : 0;
                var max = field.ContainsKey("maxLength") ? IntProperty(field, "maxLength", 0, 16384, path, errors) : 16384;
                if (min > max) errors.Add(path, "minLength exceeds maxLength.");
                if (field.ContainsKey("format"))
                {
                    var format = TextProperty(field, "format", Join(path, "format"), errors);
                    if (!StringFormats.Contains(format)) errors.Add(Join(path, "format"), "Unknown string format.");
                }
                ValidatePattern(field, "pattern", path, errors);
                break;
            }
            case "decimal":
            case "percentage":
            case "money":
            case "quantity":
                DecimalShape(field, path, errors);
                NumericBounds(field, "decimal", path, errors, kind == "percentage" ? "0" : null, kind == "percentage" ? "100" : null);
                if (kind == "money" && field.ContainsKey("currencies")) Codes(field["currencies"], Join(path, "currencies"), errors);
                if (kind == "quantity") Strings(field["units"], Join(path, "units"), errors, required: true);
                break;
            case "integer": NumericBounds(field, kind, path, errors); break;
            case "enum": Strings(field["values"], Join(path, "values"), errors, required: true, enumValues: true); break;
            case "multiEnum":
                Strings(field["values"], Join(path, "values"), errors, required: true, enumValues: true);
                CollectionBounds(field, path, errors, requiredMaximum: true);
                break;
            case "reference":
            case "versionedReference": NameProperty(field, "targetType", Join(path, "targetType"), errors); break;
            case "polymorphicReference": NamesRequired(field["targetTypes"], Join(path, "targetTypes"), errors); break;
            case "object": Fields(field["fields"], Join(path, "fields"), depth + 1, false, dataSchema, ref count, errors); break;
            case "array":
                CollectionBounds(field, path, errors, requiredMaximum: true);
                Child(field["items"], Join(path, "items"), depth, dataSchema, ref count, errors);
                break;
            case "map":
            {
                var min = field.ContainsKey("minProperties") ? IntProperty(field, "minProperties", 0, 200, path, errors) : 0;
                var max = IntProperty(field, "maxProperties", 0, 200, path, errors);
                if (min > max) errors.Add(path, "minProperties exceeds maxProperties.");
                if (field.ContainsKey("maxKeyLength")) IntProperty(field, "maxKeyLength", 1, 200, path, errors);
                ValidatePattern(field, "keyPattern", path, errors);
                Child(field["values"], Join(path, "values"), depth, dataSchema, ref count, errors);
                break;
            }
            case "taggedUnion":
                if (field["variants"] is not JsonObject { Count: >= 1 and <= 20 } variants) errors.Add(Join(path, "variants"), "Expected 1 to 20 named variants.");
                else foreach (var variant in variants)
                {
                    CheckName(variant.Key, Join(Join(path, "variants"), variant.Key), errors);
                    Child(variant.Value, Join(Join(path, "variants"), variant.Key), depth, dataSchema, ref count, errors);
                }
                break;
        }
    }

    private static string[] KindProperties(string kind) => kind switch
    {
        "string" => ["minLength", "maxLength", "format", "pattern"],
        "integer" => ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf"],
        "decimal" => ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "precision", "scale"],
        "percentage" => ["minimum", "maximum", "precision", "scale"],
        "enum" => ["values"],
        "reference" or "versionedReference" => ["targetType"],
        "polymorphicReference" => ["targetTypes"],
        "object" => ["fields"],
        "array" => ["items", "minItems", "maxItems", "uniqueItems"],
        "map" => ["values", "minProperties", "maxProperties", "maxKeyLength", "keyPattern"],
        "taggedUnion" => ["variants"],
        "money" => ["precision", "scale", "minimum", "maximum", "currencies"],
        "quantity" => ["precision", "scale", "minimum", "maximum", "units"],
        "multiEnum" => ["values", "minItems", "maxItems"],
        _ => []
    };

    private static bool CanIndex(string kind) => kind is "string" or "integer" or "decimal" or "boolean" or "date" or "instant" or "enum" or "reference"
        or "time" or "localDateTime" or "duration" or "calendarPeriod" or "offsetDateTime" or "percentage";
    private static bool CanUnique(string kind) => CanIndex(kind) && kind != "reference";

    private static void Child(JsonNode? node, string path, int depth, bool dataSchema, ref int count, Errors errors)
    {
        if (node is not JsonObject child) { errors.Add(path, "Expected a child field definition."); return; }
        if (++count > 200) { errors.Add(path, "Schema exceeds 200 total fields."); return; }
        Field(child, path, depth + 1, false, dataSchema, ref count, errors);
    }

    private static void DecimalShape(JsonObject field, string path, Errors errors)
    {
        var precision = IntProperty(field, "precision", 1, 28, path, errors);
        var scale = IntProperty(field, "scale", 0, 12, path, errors);
        if (scale > precision) errors.Add(Join(path, "scale"), "Scale cannot exceed precision.");
    }

    private static void CollectionBounds(JsonObject field, string path, Errors errors, bool requiredMaximum)
    {
        var min = field.ContainsKey("minItems") ? IntProperty(field, "minItems", 0, 200, path, errors) : 0;
        var max = field.ContainsKey("maxItems") || requiredMaximum ? IntProperty(field, "maxItems", 0, 200, path, errors) : 200;
        if (min > max) errors.Add(path, "minItems exceeds maxItems.");
    }

    private static void ValidatePattern(JsonObject field, string name, string path, Errors errors)
    {
        if (!field.ContainsKey(name)) return;
        try { ValueCodec.Pattern(TextProperty(field, name, Join(path, name), errors)); }
        catch (BosException ex) { errors.Add(Join(path, name), ex.Message); }
    }

    private static void NumericBounds(JsonObject field, string kind, string path, Errors errors, string? defaultMinimum = null, string? defaultMaximum = null)
    {
        if (field.ContainsKey("minimum") && field.ContainsKey("exclusiveMinimum")) errors.Add(path, "Choose minimum or exclusiveMinimum.");
        if (field.ContainsKey("maximum") && field.ContainsKey("exclusiveMaximum")) errors.Add(path, "Choose maximum or exclusiveMaximum.");
        Bound? lower = null, upper = null;
        try
        {
            if (defaultMinimum is not null) lower = new(NumericValue(field, kind, JsonValue.Create(defaultMinimum)!), false);
            if (defaultMaximum is not null) upper = new(NumericValue(field, kind, JsonValue.Create(defaultMaximum)!), false);
        }
        catch (BosException ex) { errors.Add(path, ex.Message); }
        foreach (var name in new[] { "minimum", "exclusiveMinimum", "maximum", "exclusiveMaximum", "multipleOf" })
        {
            if (!field.ContainsKey(name)) continue;
            try
            {
                if (field[name] is null) throw ValueCodec.Invalid("A numeric constraint cannot be null.");
                var value = NumericValue(field, kind, field[name]!);
                if (name == "multipleOf")
                {
                    if (value <= 0) throw ValueCodec.Invalid("multipleOf must be positive.");
                }
                else if (name.Contains("Minimum", StringComparison.Ordinal) || name == "minimum") lower = new(value, name == "exclusiveMinimum");
                else upper = new(value, name == "exclusiveMaximum");
            }
            catch (BosException ex) { errors.Add(Join(path, name), ex.Message); }
        }
        if (lower is { } low && upper is { } high && (low.Value > high.Value || low.Value == high.Value && (low.Exclusive || high.Exclusive)))
            errors.Add(path, "Numeric lower bound exceeds upper bound.");
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
        switch (Text(field, "kind"))
        {
            case "object": ValidateDefaults((JsonObject)field["fields"]!, Join(path, "fields"), errors); break;
            case "array": Default((JsonObject)field["items"]!, Join(path, "items"), errors); break;
            case "map": Default((JsonObject)field["values"]!, Join(path, "values"), errors); break;
            case "taggedUnion": foreach (var variant in (JsonObject)field["variants"]!) Default((JsonObject)variant.Value!, Join(Join(path, "variants"), variant.Key), errors); break;
        }
    }

    private static JsonObject ObjectValue(JsonObject fields, JsonObject data, string path, bool applyDefaults, ref int count, Errors errors)
    {
        var result = new JsonObject();
        foreach (var pair in data) if (!fields.ContainsKey(pair.Key)) errors.Add(Join(path, pair.Key), "Unknown field.", "UNKNOWN_FIELD");
        foreach (var pair in fields)
        {
            var field = (JsonObject)pair.Value!;
            var fieldPath = Join(path, pair.Key);
            if (data.TryGetPropertyValue(pair.Key, out var value)) result[pair.Key] = Value(field, value, fieldPath, applyDefaults, ref count, errors);
            else if (applyDefaults && field.ContainsKey("default")) result[pair.Key] = Value(field, field["default"], fieldPath, applyDefaults, ref count, errors);
            else if (Flag(field, "required")) errors.Add(fieldPath, "Required field is missing.");
        }
        return result;
    }

    private static JsonNode? Value(JsonObject field, JsonNode? value, string path, bool applyDefaults, ref int count, Errors errors)
    {
        var kind = Text(field, "kind");
        var ownScalars = value is null ? 1 : kind switch
        {
            "object" or "array" or "map" => 0,
            "taggedUnion" => 1,
            "multiEnum" when value is JsonArray items => items.Count,
            "multiEnum" => 0,
            "money" or "quantity" or "geopoint" or "dateRange" or "instantRange" or "polymorphicReference" or "versionedReference" => 2,
            _ => 1
        };
        count += ownScalars;
        if (count > 2000) { errors.Add(path, "Record exceeds 2,000 scalars."); return null; }
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
                case "array": return ArrayValue(field, value, path, applyDefaults, ref count, errors, false);
                case "multiEnum": return MultiEnumValue(field, value);
                case "map": return MapValue(field, value, path, applyDefaults, ref count, errors);
                case "taggedUnion": return UnionValue(field, value, path, applyDefaults, ref count, errors);
                case "integer":
                case "decimal": return NumericResult(field, kind, value, null, null);
                case "percentage": return NumericResult(field, "decimal", value, "0", "100");
                case "money": return MoneyValue(field, value);
                case "quantity": return QuantityValue(field, value);
                case "geopoint": return GeopointValue(value);
                case "dateRange": return RangeValue(value, "date");
                case "instantRange": return RangeValue(value, "instant");
                case "polymorphicReference": return PolymorphicReferenceValue(field, value);
                case "versionedReference": return VersionedReferenceValue(value);
                default:
                {
                    var canonical = ValueCodec.Scalar(kind, value);
                    if (kind == "string")
                    {
                        if (field.ContainsKey("format")) canonical = ValueCodec.StringFormat(Text(field, "format"), canonical);
                        var length = canonical.EnumerateRunes().Count();
                        if (length < MetadataInt(field["minLength"], 0)) throw ValueCodec.Invalid("String is shorter than minLength.");
                        if (length > MetadataInt(field["maxLength"], 16384)) throw ValueCodec.Invalid("String exceeds maxLength.");
                        if (field.ContainsKey("pattern") && !ValueCodec.Matches(ValueCodec.Pattern(Text(field, "pattern")), canonical)) throw ValueCodec.Invalid("String does not match pattern.");
                    }
                    if (kind == "enum" && !((JsonArray)field["values"]!).Any(v => ValueCodec.String(v!) == canonical)) throw ValueCodec.Invalid("Value is not an enum choice.");
                    return kind == "boolean" ? JsonValue.Create(canonical == "true") : JsonValue.Create(canonical);
                }
            }
        }
        catch (BosException ex) { errors.Add(path, ex.Message); return null; }
    }

    private static JsonNode ArrayValue(JsonObject field, JsonNode value, string path, bool applyDefaults, ref int count, Errors errors, bool sort)
    {
        if (value is not JsonArray array) throw ValueCodec.Invalid("Expected an array.");
        var min = MetadataInt(field["minItems"], 0); var max = MetadataInt(field["maxItems"], 200);
        if (array.Count < min) throw ValueCodec.Invalid("Array is shorter than minItems.");
        if (array.Count > max || array.Count > 200) throw ValueCodec.Invalid("Array exceeds maxItems.");
        var result = new JsonArray(); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < array.Count; i++)
        {
            var item = Value((JsonObject)field["items"]!, array[i], $"{path}[{i}]", applyDefaults, ref count, errors);
            if (Flag(field, "uniqueItems") && !seen.Add(CanonicalJson.Serialize(item))) errors.Add($"{path}[{i}]", "Duplicate normalized array item.");
            result.Add(item);
        }
        if (sort) return new JsonArray(result.OrderBy(x => CanonicalJson.Serialize(x), StringComparer.Ordinal).Select(x => x?.DeepClone()).ToArray());
        return result;
    }

    private static JsonNode MapValue(JsonObject field, JsonNode value, string path, bool applyDefaults, ref int count, Errors errors)
    {
        if (value is not JsonObject map) throw ValueCodec.Invalid("Expected an object map.");
        var min = MetadataInt(field["minProperties"], 0); var max = MetadataInt(field["maxProperties"], 0);
        if (map.Count < min) throw ValueCodec.Invalid("Map is smaller than minProperties.");
        if (map.Count > max || map.Count > 200) throw ValueCodec.Invalid("Map exceeds maxProperties.");
        var result = new JsonObject(); var pattern = field.ContainsKey("keyPattern") ? ValueCodec.Pattern(Text(field, "keyPattern")) : null;
        foreach (var pair in map)
        {
            var key = CanonicalJson.Text(pair.Key);
            if (key.EnumerateRunes().Count() > MetadataInt(field["maxKeyLength"], 200)) errors.Add(Join(path, key), "Map key exceeds maxKeyLength.");
            if (pattern is not null && !ValueCodec.Matches(pattern, key)) errors.Add(Join(path, key), "Map key does not match keyPattern.");
            result[key] = Value((JsonObject)field["values"]!, pair.Value, Join(path, key), applyDefaults, ref count, errors);
        }
        return result;
    }

    private static JsonNode UnionValue(JsonObject field, JsonNode value, string path, bool applyDefaults, ref int count, Errors errors)
    {
        var obj = ExactObject(value, "tag", "value");
        if (obj["tag"] is null) throw ValueCodec.Invalid("Tagged union tag is required.");
        var tag = ValueCodec.String(obj["tag"]!);
        if (field["variants"]![tag] is not JsonObject variant) throw ValueCodec.Invalid("Unknown tagged union variant.");
        return new JsonObject { ["tag"] = tag, ["value"] = Value(variant, obj["value"], Join(path, "value"), applyDefaults, ref count, errors) };
    }

    private static JsonNode MultiEnumValue(JsonObject field, JsonNode value)
    {
        if (value is not JsonArray array) throw ValueCodec.Invalid("Expected a multiEnum array.");
        var min = MetadataInt(field["minItems"], 0); var max = MetadataInt(field["maxItems"], 0);
        if (array.Count < min || array.Count > max) throw ValueCodec.Invalid("multiEnum item count is outside its bounds.");
        var choices = ((JsonArray)field["values"]!).Select(v => ValueCodec.Scalar("enum", v!)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal); var items = new List<string>();
        foreach (var item in array)
        {
            if (item is null) throw ValueCodec.Invalid("multiEnum values must be strings.");
            var canonical = ValueCodec.Scalar("enum", item);
            if (!choices.Contains(canonical)) throw ValueCodec.Invalid("Value is not a multiEnum choice.");
            if (!seen.Add(canonical)) throw ValueCodec.Invalid("Duplicate multiEnum value.");
            items.Add(canonical);
        }
        items.Sort(StringComparer.Ordinal);
        return new JsonArray(items.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    }

    private static JsonNode NumericResult(JsonObject field, string kind, JsonNode value, string? defaultMinimum, string? defaultMaximum)
    {
        var number = NumericValue(field, kind, value);
        CheckNumeric(field, kind, number, defaultMinimum, defaultMaximum);
        return JsonValue.Create(kind == "integer" ? number.ToString(CultureInfo.InvariantCulture) : ValueCodec.Format(number, MetadataInt(field["scale"], 0)))!;
    }

    private static void CheckNumeric(JsonObject field, string kind, BigInteger number, string? defaultMinimum = null, string? defaultMaximum = null)
    {
        var constraints = new[] { "minimum", "exclusiveMinimum", "maximum", "exclusiveMaximum" };
        foreach (var name in constraints)
        {
            BigInteger limit;
            if (field.ContainsKey(name)) limit = NumericValue(field, kind, field[name]!);
            else if (name == "minimum" && defaultMinimum is not null && !field.ContainsKey("exclusiveMinimum")) limit = NumericValue(field, kind, JsonValue.Create(defaultMinimum)!);
            else if (name == "maximum" && defaultMaximum is not null && !field.ContainsKey("exclusiveMaximum")) limit = NumericValue(field, kind, JsonValue.Create(defaultMaximum)!);
            else continue;
            var invalid = name switch { "minimum" => number < limit, "exclusiveMinimum" => number <= limit, "maximum" => number > limit, _ => number >= limit };
            if (invalid) throw ValueCodec.Invalid("Value violates " + name + ".");
        }
        if (field.ContainsKey("multipleOf") && number % NumericValue(field, kind, field["multipleOf"]!) != 0) throw ValueCodec.Invalid("Value violates multipleOf.");
    }

    private static JsonNode MoneyValue(JsonObject field, JsonNode value)
    {
        var obj = ExactObject(value, "amount", "currency");
        var amount = NumericResult(field, "decimal", obj["amount"] ?? throw ValueCodec.Invalid("Money amount is required."), null, null);
        var currency = ValueCodec.StringFormat("currencyCode", ValueCodec.String(obj["currency"] ?? throw ValueCodec.Invalid("Money currency is required.")));
        if (field["currencies"] is JsonArray allowed && !allowed.Select(x => ValueCodec.StringFormat("currencyCode", ValueCodec.String(x!))).Contains(currency, StringComparer.Ordinal))
            throw ValueCodec.Invalid("Money currency is not allowed.");
        return new JsonObject { ["amount"] = amount, ["currency"] = currency };
    }

    private static JsonNode QuantityValue(JsonObject field, JsonNode value)
    {
        var obj = ExactObject(value, "value", "unit");
        var number = NumericResult(field, "decimal", obj["value"] ?? throw ValueCodec.Invalid("Quantity value is required."), null, null);
        var unit = ValueCodec.String(obj["unit"] ?? throw ValueCodec.Invalid("Quantity unit is required."));
        if (!((JsonArray)field["units"]!).Select(x => ValueCodec.String(x!)).Contains(unit, StringComparer.Ordinal)) throw ValueCodec.Invalid("Quantity unit is not allowed.");
        return new JsonObject { ["value"] = number, ["unit"] = unit };
    }

    private static JsonNode GeopointValue(JsonNode value)
    {
        var obj = ExactObject(value, "latitude", "longitude");
        BigInteger Coordinate(string name, int maximum)
        {
            var parsed = ValueCodec.Decimal(ValueCodec.String(obj[name] ?? throw ValueCodec.Invalid("Geopoint coordinate is required.")));
            var number = ValueCodec.Rescale(parsed.Value, parsed.Scale, 6);
            if (number < -maximum * 1_000_000L || number > maximum * 1_000_000L) throw ValueCodec.Invalid("Geopoint coordinate is outside its range.");
            return number;
        }
        return new JsonObject { ["latitude"] = ValueCodec.Format(Coordinate("latitude", 90), 6), ["longitude"] = ValueCodec.Format(Coordinate("longitude", 180), 6) };
    }

    private static JsonNode RangeValue(JsonNode value, string kind)
    {
        var obj = ExactObject(value, "start", "end");
        var start = ValueCodec.Scalar(kind, obj["start"] ?? throw ValueCodec.Invalid("Range start is required."));
        var end = ValueCodec.Scalar(kind, obj["end"] ?? throw ValueCodec.Invalid("Range end is required."));
        if (string.CompareOrdinal(start, end) > 0) throw ValueCodec.Invalid("Range start exceeds end.");
        return new JsonObject { ["start"] = start, ["end"] = end };
    }

    private static JsonNode PolymorphicReferenceValue(JsonObject field, JsonNode value)
    {
        var obj = ExactObject(value, "type", "id");
        var type = ValueCodec.String(obj["type"] ?? throw ValueCodec.Invalid("Reference type is required."));
        if (!((JsonArray)field["targetTypes"]!).Select(x => ValueCodec.String(x!)).Contains(type, StringComparer.Ordinal)) throw ValueCodec.Invalid("Reference type is not allowed.");
        var id = ValueCodec.Scalar("userReference", obj["id"] ?? throw ValueCodec.Invalid("Reference id is required."));
        return new JsonObject { ["type"] = type, ["id"] = id };
    }

    private static JsonNode VersionedReferenceValue(JsonNode value)
    {
        var obj = ExactObject(value, "id", "revision");
        var id = ValueCodec.Scalar("userReference", obj["id"] ?? throw ValueCodec.Invalid("Reference id is required."));
        if (!ValueCodec.TryMetadataInt(obj["revision"], out var revision) || revision <= 0) throw ValueCodec.Invalid("Reference revision must be a positive JSON integer.");
        return new JsonObject { ["id"] = id, ["revision"] = revision };
    }

    private static JsonObject ExactObject(JsonNode value, params string[] properties)
    {
        if (value is not JsonObject obj || obj.Count != properties.Length || properties.Any(x => !obj.ContainsKey(x))) throw ValueCodec.Invalid("Expected an exact object wire form.");
        return obj;
    }

    private static void EvolveFields(JsonObject old, JsonObject next, string path, bool indexedParent)
    {
        foreach (var pair in old)
        {
            var fieldPath = Join(path, pair.Key);
            if (next[pair.Key] is not JsonObject) Incompatible(fieldPath);
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
        if (old.ContainsKey("default") != next.ContainsKey("default") || !SameDefault(old, next)) Incompatible(Join(path, "default"));
        if (indexed)
        {
            if (!SameIndexedConstraints(old, next, kind)) Incompatible(path);
            return;
        }
        switch (kind)
        {
            case "string":
                RelaxMinimum(old, next, "minLength", 0, path);
                RelaxMaximum(old, next, "maxLength", 16384, path);
                RemovedOrEqual(old, next, "format", path);
                RemovedOrEqual(old, next, "pattern", path);
                break;
            case "decimal":
            case "percentage":
            case "money":
            case "quantity":
                EqualInt(old, next, "precision", path); EqualInt(old, next, "scale", path);
                EvolveNumeric(old, next, "decimal", path, kind == "percentage" ? "0" : null, kind == "percentage" ? "100" : null);
                if (kind == "money") AddedStrings(old, next, "currencies", path, optional: true, codes: true);
                if (kind == "quantity") AddedStrings(old, next, "units", path);
                break;
            case "integer": EvolveNumeric(old, next, kind, path); break;
            case "enum": AddedStrings(old, next, "values", path); break;
            case "multiEnum":
                AddedStrings(old, next, "values", path);
                RelaxMinimum(old, next, "minItems", 0, path); RelaxMaximum(old, next, "maxItems", 0, path);
                break;
            case "reference":
            case "versionedReference": if (Text(old, "targetType") != Text(next, "targetType")) Incompatible(Join(path, "targetType")); break;
            case "polymorphicReference": AddedStrings(old, next, "targetTypes", path); break;
            case "object": EvolveFields((JsonObject)old["fields"]!, (JsonObject)next["fields"]!, Join(path, "fields"), false); break;
            case "array":
                RelaxMinimum(old, next, "minItems", 0, path); RelaxMaximum(old, next, "maxItems", 0, path);
                RemovedBoolean(old, next, "uniqueItems", path);
                EvolveField((JsonObject)old["items"]!, (JsonObject)next["items"]!, Join(path, "items"), false);
                break;
            case "map":
                RelaxMinimum(old, next, "minProperties", 0, path); RelaxMaximum(old, next, "maxProperties", 0, path);
                RelaxMaximum(old, next, "maxKeyLength", 200, path); RemovedOrEqual(old, next, "keyPattern", path);
                EvolveField((JsonObject)old["values"]!, (JsonObject)next["values"]!, Join(path, "values"), false);
                break;
            case "taggedUnion":
            {
                var before = (JsonObject)old["variants"]!; var after = (JsonObject)next["variants"]!;
                foreach (var variant in before)
                {
                    if (after[variant.Key] is not JsonObject) Incompatible(Join(Join(path, "variants"), variant.Key));
                    EvolveField((JsonObject)variant.Value!, (JsonObject)after[variant.Key]!, Join(Join(path, "variants"), variant.Key), false);
                }
                break;
            }
        }
    }

    private static void EvolveNumeric(JsonObject old, JsonObject next, string kind, string path, string? defaultMinimum = null, string? defaultMaximum = null)
    {
        var oldLower = BoundOf(old, kind, true, defaultMinimum); var nextLower = BoundOf(next, kind, true, defaultMinimum);
        var oldUpper = BoundOf(old, kind, false, defaultMaximum); var nextUpper = BoundOf(next, kind, false, defaultMaximum);
        if (oldLower is null && nextLower is not null || oldLower is { } ol && nextLower is { } nl && (nl.Value > ol.Value || nl.Value == ol.Value && nl.Exclusive && !ol.Exclusive)) Incompatible(path);
        if (oldUpper is null && nextUpper is not null || oldUpper is { } ou && nextUpper is { } nu && (nu.Value < ou.Value || nu.Value == ou.Value && nu.Exclusive && !ou.Exclusive)) Incompatible(path);
        if (!old.ContainsKey("multipleOf")) { if (next.ContainsKey("multipleOf")) Incompatible(Join(path, "multipleOf")); }
        else if (next.ContainsKey("multipleOf"))
        {
            var before = NumericValue(old, kind, old["multipleOf"]!); var after = NumericValue(next, kind, next["multipleOf"]!);
            if (before % after != 0) Incompatible(Join(path, "multipleOf"));
        }
    }

    private static Bound? BoundOf(JsonObject field, string kind, bool lower, string? fallback = null)
    {
        var inclusive = lower ? "minimum" : "maximum"; var exclusive = lower ? "exclusiveMinimum" : "exclusiveMaximum";
        if (field.ContainsKey(exclusive)) return new(NumericValue(field, kind, field[exclusive]!), true);
        if (field.ContainsKey(inclusive)) return new(NumericValue(field, kind, field[inclusive]!), false);
        return fallback is null ? null : new(NumericValue(field, kind, JsonValue.Create(fallback)!), false);
    }

    private static bool SameIndexedConstraints(JsonObject old, JsonObject next, string kind)
    {
        switch (kind)
        {
            case "string":
                return MetadataInt(old["minLength"], 0) == MetadataInt(next["minLength"], 0)
                    && MetadataInt(old["maxLength"], 16384) == MetadataInt(next["maxLength"], 16384)
                    && OptionalConstraint(old, next, "format") && OptionalConstraint(old, next, "pattern");
            case "integer": return SameNumericConstraints(old, next, kind);
            case "decimal": return MetadataInt(old["precision"], -1) == MetadataInt(next["precision"], -1)
                    && MetadataInt(old["scale"], -1) == MetadataInt(next["scale"], -1) && SameNumericConstraints(old, next, kind);
            case "percentage": return MetadataInt(old["precision"], -1) == MetadataInt(next["precision"], -1)
                    && MetadataInt(old["scale"], -1) == MetadataInt(next["scale"], -1) && SameNumericConstraints(old, next, "decimal", "0", "100");
            case "enum": return StringSet(old["values"]).SetEquals(StringSet(next["values"]));
            case "reference": return Text(old, "targetType") == Text(next, "targetType");
            default: return true;
        }
    }

    private static bool SameNumericConstraints(JsonObject old, JsonObject next, string kind, string? defaultMinimum = null, string? defaultMaximum = null)
    {
        if (BoundOf(old, kind, true, defaultMinimum) != BoundOf(next, kind, true, defaultMinimum)
            || BoundOf(old, kind, false, defaultMaximum) != BoundOf(next, kind, false, defaultMaximum)) return false;
        if (old.ContainsKey("multipleOf") != next.ContainsKey("multipleOf")) return false;
        return !old.ContainsKey("multipleOf") || NumericValue(old, kind, old["multipleOf"]!) == NumericValue(next, kind, next["multipleOf"]!);
    }

    private static bool OptionalConstraint(JsonObject old, JsonObject next, string name) => old.ContainsKey(name) == next.ContainsKey(name)
        && (!old.ContainsKey(name) || Text(old, name) == Text(next, name));

    private static void RelaxMinimum(JsonObject old, JsonObject next, string name, int fallback, string path)
    { if (MetadataInt(next[name], fallback) > MetadataInt(old[name], fallback)) Incompatible(Join(path, name)); }
    private static void RelaxMaximum(JsonObject old, JsonObject next, string name, int fallback, string path)
    { if (MetadataInt(next[name], fallback) < MetadataInt(old[name], fallback)) Incompatible(Join(path, name)); }
    private static void EqualInt(JsonObject old, JsonObject next, string name, string path)
    { if (MetadataInt(old[name], int.MinValue) != MetadataInt(next[name], int.MinValue)) Incompatible(Join(path, name)); }
    private static void RemovedOrEqual(JsonObject old, JsonObject next, string name, string path)
    { if (!old.ContainsKey(name) && next.ContainsKey(name) || old.ContainsKey(name) && next.ContainsKey(name) && Text(old, name) != Text(next, name)) Incompatible(Join(path, name)); }
    private static void RemovedBoolean(JsonObject old, JsonObject next, string name, string path)
    { if (!Flag(old, name) && Flag(next, name)) Incompatible(Join(path, name)); }

    private static void AddedStrings(JsonObject old, JsonObject next, string name, string path, bool optional = false, bool codes = false)
    {
        if (optional && !old.ContainsKey(name)) { if (next.ContainsKey(name)) Incompatible(Join(path, name)); return; }
        if (old.ContainsKey(name) && !next.ContainsKey(name)) return;
        var choices = StringSet(next[name], codes);
        if (!StringSet(old[name], codes).IsSubsetOf(choices)) Incompatible(Join(path, name));
    }

    private static HashSet<string> StringSet(JsonNode? node, bool codes = false) => (node as JsonArray ?? [])
        .Select(v => codes ? ValueCodec.StringFormat("currencyCode", ValueCodec.String(v!)) : ValueCodec.String(v!)).ToHashSet(StringComparer.Ordinal);

    private static bool SameDefault(JsonObject old, JsonObject next)
    {
        if (!old.ContainsKey("default")) return true;
        var errors = new Errors("INVALID_SCHEMA"); var count = 0;
        var before = Value(old, old["default"], "", true, ref count, errors); count = 0;
        var after = Value(next, next["default"], "", true, ref count, errors);
        return errors.Count == 0 && CanonicalJson.Serialize(before) == CanonicalJson.Serialize(after);
    }

    private static bool SameNames(JsonNode? left, JsonNode? right) => StringSet(left).SetEquals(StringSet(right));

    private static void Strings(JsonNode? node, string path, Errors errors, bool required, bool enumValues = false)
    {
        if (node is not JsonArray values || required && values.Count == 0) { errors.Add(path, "Expected a nonempty string list."); return; }
        if (values.Count > 200) errors.Add(path, "String list exceeds 200 values.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < values.Count; i++)
        {
            try
            {
                if (values[i] is null) throw ValueCodec.Invalid("Expected a string.");
                var value = ValueCodec.Scalar(enumValues ? "enum" : "string", values[i]!);
                if (!seen.Add(value)) errors.Add($"{path}[{i}]", "Duplicate normalized value.");
            }
            catch (BosException ex) { errors.Add($"{path}[{i}]", ex.Message); }
        }
    }

    private static void Codes(JsonNode? node, string path, Errors errors)
    {
        if (node is not JsonArray { Count: > 0 } values) { errors.Add(path, "Expected a nonempty currency list."); return; }
        if (values.Count > 200) errors.Add(path, "Currency list exceeds 200 values.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < values.Count; i++)
        {
            try
            {
                if (values[i] is null) throw ValueCodec.Invalid("Expected a currency code.");
                var original = ValueCodec.String(values[i]!);
                var canonical = ValueCodec.StringFormat("currencyCode", original);
                if (original != canonical) errors.Add($"{path}[{i}]", "Currency definitions must be uppercase.");
                if (!seen.Add(canonical)) errors.Add($"{path}[{i}]", "Duplicate normalized currency.");
            }
            catch (BosException ex) { errors.Add($"{path}[{i}]", ex.Message); }
        }
    }

    private static void NamesRequired(JsonNode? node, string path, Errors errors)
    {
        if (node is not JsonArray { Count: > 0 } names) { errors.Add(path, "Expected a nonempty name list."); return; }
        Names(names, path, errors);
    }

    private static void Names(JsonNode? node, string path, Errors errors)
    {
        if (node is not JsonArray names) { errors.Add(path, "Expected a name array."); return; }
        if (names.Count > 200) errors.Add(path, "Name list exceeds 200 values.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i] is null || names[i]!.GetValueKind() != JsonValueKind.String) { errors.Add($"{path}[{i}]", "Expected a name."); continue; }
            var name = names[i]!.GetValue<string>(); CheckName(name, $"{path}[{i}]", errors);
            if (!seen.Add(name)) errors.Add($"{path}[{i}]", "Duplicate name.");
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

    private static string OptionalText(JsonObject obj, string name, string fallback, string path, Errors errors) => obj.ContainsKey(name) ? TextProperty(obj, name, path, errors) : fallback;
    private static void NameProperty(JsonObject obj, string name, string path, Errors errors) => CheckName(TextProperty(obj, name, path, errors), path, errors);
    private static void CheckName(string name, string path, Errors errors) { try { JsonArgs.Name(name); } catch (BosException ex) { errors.Add(path, ex.Message); } }
    private static void BooleanProperty(JsonObject obj, string property, string path, Errors errors)
    { if (obj[property] is null || obj[property]!.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) errors.Add(path, "Expected a boolean."); }
    private static int IntProperty(JsonObject obj, string name, int minimum, int maximum, string path, Errors errors)
    {
        var result = MetadataInt(obj[name], int.MinValue);
        if (result < minimum || result > maximum) errors.Add(Join(path, name), $"Expected an integer from {minimum} to {maximum}.");
        return result;
    }
    private static int MetadataInt(JsonNode? node, int fallback) => ValueCodec.TryMetadataInt(node, out var result) ? result : fallback;
    private static bool Flag(JsonObject obj, string name) => obj[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private static bool Indexed(JsonObject field) => Flag(field, "indexed") || Flag(field, "unique");
    private static string Text(JsonObject obj, string name, string fallback = "") => obj[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : fallback;
    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;
    private static BosException Failure(string code, ValidationReport report) => new(code, "Schema or data validation failed.") { Details = report.Errors };
    private static void Incompatible(string path) => throw new BosException("SCHEMA_VERSION_MISMATCH", "Incompatible schema evolution requires an explicit migration.", 409, path);
    private readonly record struct Bound(BigInteger Value, bool Exclusive);

    private sealed class Errors(string code)
    {
        private readonly List<FieldError> _items = [];
        public int Count => _items.Count;
        public void Add(string path, string message, string? errorCode = null) { if (_items.Count < 100) _items.Add(new(path, errorCode ?? code, message)); }
        public ValidationReport Report() => new(_items.Count == 0, _items.ToArray());
    }
}
