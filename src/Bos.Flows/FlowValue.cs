using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bos.Contracts;

namespace Bos.Flows;

internal enum ValueKind { Missing, Null, String, Boolean, Integer, Decimal, Date, Instant, Array, Object }

// Decimal never passes through System.Decimal or binary floating point.
internal readonly record struct ExactDecimal(BigInteger Coefficient, int Scale)
{
    internal static ExactDecimal Parse(string text)
    {
        if (text.Length > 262140 || !Regex.IsMatch(text, @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$"))
            throw new BosException("CONVERSION_FAILED", "Invalid decimal.");
        int dot = text.IndexOf('.');
        int scale = dot < 0 ? 0 : text.Length - dot - 1;
        return Checked(BigInteger.Parse(text.Replace(".", ""), CultureInfo.InvariantCulture), scale);
    }
    internal static ExactDecimal Checked(BigInteger value, int scale)
    {
        if (scale < 0 || scale > 262140) throw new BosException("EXECUTION_LIMIT", "Decimal representation exceeds allocation bound.");
        while (BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture).Length > 28 && scale > 0 && value % 10 == 0)
        { value /= 10; scale--; }
        if (BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture).Length > 28)
            throw new BosException("ARITHMETIC_OVERFLOW", "Decimal precision exceeds 28 digits.");
        return new(value, scale);
    }
    internal static BigInteger Pow(int n) => BigInteger.Pow(10, n);
    internal int Compare(ExactDecimal b)
    {
        int scale = Math.Max(Scale, b.Scale);
        return (Coefficient * Pow(scale - Scale)).CompareTo(b.Coefficient * Pow(scale - b.Scale));
    }
    internal ExactDecimal Add(ExactDecimal b, bool subtract = false)
    {
        int scale = Math.Max(Scale, b.Scale);
        return Checked(Coefficient * Pow(scale - Scale) + (subtract ? -1 : 1) * b.Coefficient * Pow(scale - b.Scale), scale);
    }
    internal ExactDecimal Multiply(ExactDecimal b) => Checked(Coefficient * b.Coefficient, Scale + b.Scale);
    internal ExactDecimal Round(int scale, string rounding) => Divide(new(1, 0), scale, rounding);
    internal ExactDecimal Divide(ExactDecimal b, int scale, string rounding)
    {
        if (scale is < 0 or > 12) throw new BosException("INVALID_ARGUMENT", "Scale must be between 0 and 12.");
        if (rounding is not ("halfEven" or "halfAwayFromZero" or "towardZero"))
            throw new BosException("INVALID_ARGUMENT", "Unknown rounding mode.");
        if (b.Coefficient.IsZero) throw new BosException("DIVIDE_BY_ZERO", "Division by zero.");
        int power = b.Scale + scale - Scale;
        BigInteger numerator = Coefficient * Pow(Math.Max(power, 0));
        BigInteger denominator = b.Coefficient * Pow(Math.Max(-power, 0));
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        int cmp = (BigInteger.Abs(remainder) * 2).CompareTo(BigInteger.Abs(denominator));
        if (rounding != "towardZero" && (cmp > 0 || cmp == 0 && (rounding == "halfAwayFromZero" || !quotient.IsEven)))
            quotient += numerator.Sign * denominator.Sign;
        return Checked(quotient, scale);
    }
    public override string ToString()
    {
        string digits = BigInteger.Abs(Coefficient).ToString(CultureInfo.InvariantCulture).PadLeft(Scale + 1, '0');
        return (Coefficient.Sign < 0 ? "-" : "") + (Scale == 0 ? digits : digits.Insert(digits.Length - Scale, "."));
    }
}

internal sealed class FlowValue
{
    internal ValueKind Kind { get; }
    internal object? Data { get; }
    internal int Bytes { get; }
    private FlowValue(ValueKind kind, object? data, int bytes = 4)
    {
        if (bytes > 262144) throw new BosException("EXECUTION_LIMIT", "Intermediate value exceeds 256 KiB.");
        Kind = kind; Data = data; Bytes = bytes;
    }
    internal static readonly FlowValue Missing = new(ValueKind.Missing, null);
    internal static readonly FlowValue Null = new(ValueKind.Null, null);
    internal static FlowValue Text(string s)
    {
        if (s.Length > 262142) throw new BosException("EXECUTION_LIMIT", "String exceeds allocation limit.");
        s = s.Normalize();
        return new(ValueKind.String, s, checked(Encoding.UTF8.GetByteCount(s) + 2));
    }
    private static int JsonStringBytes(string s)
    {
        int bytes = 2;
        foreach (char c in s)
        {
            bytes += c < 0x20 || c is '"' or '\\' ? 6 : c < 0x80 ? 1 : c < 0x800 ? 2 : char.IsSurrogate(c) ? 2 : 3;
            if (bytes > 262144) throw new BosException("EXECUTION_LIMIT", "String exceeds allocation limit.");
        }
        return bytes;
    }
    internal static FlowValue Bool(bool b) => new(ValueKind.Boolean, b);
    internal static FlowValue Integer(long n) => new(ValueKind.Integer, n, 22);
    internal static FlowValue Decimal(ExactDecimal n) => new(ValueKind.Decimal, n, Math.Max(32, n.Scale + 5));
    internal static FlowValue Date(DateOnly d) => new(ValueKind.Date, d, 12);
    internal static FlowValue Instant(DateTimeOffset d) => new(ValueKind.Instant, d.ToUniversalTime(), 26);
    internal static FlowValue Array(IEnumerable<FlowValue> items)
    {
        var b = ImmutableArray.CreateBuilder<FlowValue>(); int bytes = 2;
        foreach (var item in items)
        {
            bytes = checked(bytes + item.Bytes + 1);
            if (bytes > 262144) throw new BosException("EXECUTION_LIMIT", "Array exceeds 256 KiB.");
            b.Add(item);
        }
        return new(ValueKind.Array, b.ToImmutable(), bytes);
    }
    internal static FlowValue Object(IEnumerable<KeyValuePair<string, FlowValue>> fields)
    {
        var b = ImmutableDictionary.CreateBuilder<string, FlowValue>(StringComparer.Ordinal); int bytes = 2;
        foreach (var (key, value) in fields)
        {
            bytes = checked(bytes + JsonStringBytes(key) + value.Bytes + 2);
            if (bytes > 262144) throw new BosException("EXECUTION_LIMIT", "Object exceeds 256 KiB.");
            b.Add(key, value);
        }
        return new(ValueKind.Object, b.ToImmutable(), bytes);
    }
    internal ImmutableArray<FlowValue> Items => Require<ImmutableArray<FlowValue>>(ValueKind.Array);
    internal ImmutableDictionary<string, FlowValue> Fields => Require<ImmutableDictionary<string, FlowValue>>(ValueKind.Object);
    internal T Require<T>(ValueKind kind)
    {
        if (Kind == ValueKind.Missing) throw new BosException("MISSING_VALUE", "A required expression value is missing.");
        if (Kind != kind) throw new BosException("TYPE_MISMATCH", $"Expected {kind}, received {Kind}.");
        return (T)Data!;
    }
    internal FlowValue Field(string name) => Kind == ValueKind.Object && Fields.TryGetValue(name, out var v) ? v : Missing;
    internal FlowValue Path(string path)
    {
        var value = this;
        foreach (string segment in path.Split('.'))
        {
            if (value.Kind is ValueKind.Null or ValueKind.Missing) return Missing;
            if (value.Kind != ValueKind.Object) throw new BosException("TYPE_MISMATCH", "Path traverses a scalar or array.");
            value = value.Field(segment);
        }
        return value;
    }
    internal JsonNode? Json(bool metadata = false)
    {
        return Kind switch
        {
            ValueKind.Null => null,
            ValueKind.String => JsonValue.Create((string)Data!),
            ValueKind.Boolean => JsonValue.Create((bool)Data!),
            ValueKind.Integer => metadata ? MetadataJson() : JsonValue.Create(ScalarText()),
            ValueKind.Decimal or ValueKind.Date or ValueKind.Instant => JsonValue.Create(ScalarText()),
            ValueKind.Array => new JsonArray(Items.Select(v => v.Json(metadata)).ToArray()),
            ValueKind.Object => new JsonObject(Fields.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, x.Value.Json(metadata)))),
            _ => throw new BosException("MISSING_VALUE", "Missing cannot be serialized.")
        };
    }
    private JsonNode MetadataJson()
    {
        long number = (long)Data!;
        if (number is < int.MinValue or > int.MaxValue) throw new BosException("CONVERSION_FAILED", "Metadata integer is out of Int32 range.");
        return JsonValue.Create((int)number)!;
    }
    internal string ScalarText() => Kind switch
    {
        ValueKind.String => (string)Data!, ValueKind.Null => "null",
        ValueKind.Boolean => (bool)Data! ? "true" : "false",
        ValueKind.Integer => ((long)Data!).ToString(CultureInfo.InvariantCulture),
        ValueKind.Decimal => ((ExactDecimal)Data!).ToString(),
        ValueKind.Date => ((DateOnly)Data!).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ValueKind.Instant => ((DateTimeOffset)Data!).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        ValueKind.Missing => throw new BosException("MISSING_VALUE", "Missing value."),
        _ => throw new BosException("TYPE_MISMATCH", "Expected a scalar.")
    };
    internal static FlowValue Literal(JsonNode? node, int depth = 0)
    {
        if (depth > 64) throw new BosException("EXECUTION_LIMIT", "Value nesting limit exceeded.");
        if (node is null) return Null;
        if (node is JsonObject o) return Object(o.Select(x => KeyValuePair.Create(x.Key, Literal(x.Value, depth + 1))));
        if (node is JsonArray a) return Array(a.Select(x => Literal(x, depth + 1)));
        var v = (JsonValue)node;
        if (v.TryGetValue<string>(out var s)) return Text(s);
        if (v.TryGetValue<bool>(out var b)) return Bool(b);
        if (v.TryGetValue<long>(out var n)) return Integer(n);
        if (v.TryGetValue<int>(out var i)) return Integer(i);
        if (v.GetValueKind() == System.Text.Json.JsonValueKind.Number && long.TryParse(v.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) return Integer(n);
        throw new BosException("TYPE_MISMATCH", "Only bounded integer JSON numbers are supported.");
    }
    internal static long ParseInteger(string text)
    {
        if (!Regex.IsMatch(text, @"^-?(0|[1-9][0-9]*)$") || !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n))
            throw new BosException("CONVERSION_FAILED", "Invalid signed 64-bit integer.");
        return n;
    }
}
