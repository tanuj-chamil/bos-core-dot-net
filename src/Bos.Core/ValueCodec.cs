using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bos.Contracts;

namespace Bos.Core;

// Schema constraints and index ordering share these exact, non-floating-point codecs.
internal static class ValueCodec
{
    internal static bool TryMetadataInt(JsonNode? node, out int result)
    {
        result = 0;
        if (node is not JsonValue value || node.GetValueKind() != JsonValueKind.Number) return false;
        return value.TryGetValue<int>(out result)
            || int.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    internal static string String(JsonNode value)
    {
        if (value.GetValueKind() != JsonValueKind.String) throw Invalid("Expected a string.");
        return CanonicalJson.Text(value.GetValue<string>());
    }

    internal static BigInteger Integer(string text)
    {
        if (text.Length > 16384 || !Regex.IsMatch(text, "\\A[+-]?[0-9]+\\z", RegexOptions.CultureInvariant))
            throw Invalid("Expected a base-10 integer string.");
        var value = BigInteger.Parse(text, CultureInfo.InvariantCulture);
        if (value < long.MinValue || value > long.MaxValue) throw Invalid("Integer exceeds signed 64-bit range.");
        return value;
    }

    internal static (BigInteger Value, int Scale) Decimal(string text)
    {
        if (text.Length > 16384 || !Regex.IsMatch(text, "\\A[+-]?[0-9]+(?:\\.[0-9]+)?\\z", RegexOptions.CultureInvariant))
            throw Invalid("Expected a base-10 decimal string without an exponent.");
        var dot = text.IndexOf('.');
        var scale = dot < 0 ? 0 : text.Length - dot - 1;
        if (scale > 12) throw Invalid("Decimal has more than 12 fractional digits.");
        var value = BigInteger.Parse(text.Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        return (value, scale);
    }

    internal static BigInteger Rescale(BigInteger value, int from, int to)
    {
        if (from > to) throw Invalid("Excess fractional digits; explicit rounding is required.");
        return value * BigInteger.Pow(10, to - from);
    }

    internal static string Format(BigInteger value, int scale)
    {
        var digits = BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        if (scale > 0) digits = digits.Insert(digits.Length - scale, ".");
        return value.Sign < 0 ? "-" + digits : digits;
    }

    internal static string Scalar(string kind, JsonNode value)
    {
        if (kind == "boolean")
        {
            if (value.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("Expected a boolean.");
            return value.GetValue<bool>() ? "true" : "false";
        }
        var text = String(value);
        switch (kind)
        {
            case "string":
            case "enum":
                if (Encoding.UTF8.GetByteCount(text) > 16 * 1024) throw Invalid("String exceeds 16 KiB UTF-8.");
                return text;
            case "integer": return Integer(text).ToString(CultureInfo.InvariantCulture);
            case "decimal":
                var number = Decimal(text);
                // A scale-free index representation is shared by equal decimal values.
                while (number.Scale > 0 && number.Value % 10 == 0) number = (number.Value / 10, number.Scale - 1);
                if (BigInteger.Abs(number.Value) >= BigInteger.Pow(10, 28)) throw Invalid("Decimal exceeds 28 digits.");
                return Format(number.Value, number.Scale);
            case "reference":
                try { return JsonArgs.Uuid(text); }
                catch (BosException) { throw Invalid("Expected a canonical lowercase UUID."); }
            case "date":
                if (!Regex.IsMatch(text, "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}\\z", RegexOptions.CultureInvariant)
                    || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw Invalid("Expected a Gregorian YYYY-MM-DD date.");
                return text;
            case "instant":
                return Instant(text);
            default: throw Invalid("Unknown scalar kind.");
        }
    }

    private static string Instant(string text)
    {
        var match = Regex.Match(text, "\\A(?<local>[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,3})?)(?<zone>[Zz]|[+-][0-9]{2}:[0-9]{2})\\z", RegexOptions.CultureInvariant);
        if (!match.Success || !DateTime.TryParseExact(match.Groups["local"].Value.Replace('t', 'T'),
                ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.f", "yyyy-MM-dd'T'HH:mm:ss.ff", "yyyy-MM-dd'T'HH:mm:ss.fff"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw Invalid("Expected an RFC 3339 instant with offset and at most milliseconds.");
        var zone = match.Groups["zone"].Value;
        var minutes = 0;
        if (zone.Length > 1)
        {
            var hours = int.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture);
            var remainder = int.Parse(zone.AsSpan(4, 2), CultureInfo.InvariantCulture);
            if (hours > 23 || remainder > 59) throw Invalid("Invalid instant offset.");
            minutes = (hours * 60 + remainder) * (zone[0] == '-' ? -1 : 1);
        }
        try { return local.AddMinutes(-minutes).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { throw Invalid("Instant is outside the supported Gregorian range."); }
    }

    internal static BosException Invalid(string message) => new("CONVERSION_FAILED", message);
}
