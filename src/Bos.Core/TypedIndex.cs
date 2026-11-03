using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Bos.Core;

/// <summary>Ordinal sortable keys for present scalar values; missing/null are separate index states.</summary>
public static class TypedIndex
{
    private static readonly BigInteger Offset = BigInteger.Pow(10, 41);

    public static string Canonical(string kind, JsonNode value) => value is null
        ? throw ValueCodec.Invalid("An index key requires a present, non-null scalar.")
        : ValueCodec.Scalar(kind, value);

    public static string Key(string kind, JsonNode value)
    {
        var canonical = Canonical(kind, value);
        if (kind is "string" or "enum")
        {
            // SQLite BINARY compares UTF-8 bytes, whereas BOS ordinal order is UTF-16.
            // Fixed-width hex preserves UTF-16 ordinal order, including supplementary text
            // and embedded NUL, without requiring a connection-specific SQLite collation.
            var key = new StringBuilder(canonical.Length * 4);
            foreach (var unit in canonical) key.Append(((int)unit).ToString("X4", CultureInfo.InvariantCulture));
            return key.ToString();
        }
        if (kind is not ("integer" or "decimal")) return canonical;
        var number = ValueCodec.Decimal(canonical);
        var scaled = ValueCodec.Rescale(number.Value, number.Scale, 12);
        if (BigInteger.Abs(scaled) >= Offset) throw ValueCodec.Invalid("Numeric index value exceeds its range.");
        return (scaled + Offset).ToString("D42", CultureInfo.InvariantCulture);
    }
}
