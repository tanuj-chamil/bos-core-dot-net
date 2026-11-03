using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bos.Contracts;

namespace Bos.Core;

/// <summary>Bounded, strict JSON decoding and deterministic UTF-8 canonicalization.</summary>
public static class CanonicalJson
{
    public const int MaxBytes = 256 * 1024;
    public const int MaxDepth = 64;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = MaxDepth
    };

    /// <param name="preserveSecrets">Preserve scalar password, currentPassword, newPassword and source strings for endpoint binding.</param>
    /// <param name="preserveSource">Preserve scalar source strings for stored document reads, without preserving secrets.</param>
    public static JsonNode? Parse(string json, bool preserveSecrets = false, bool preserveSource = false)
    {
        try
        {
            if (json is null) throw Invalid("A JSON body is required.");
            // The throwing encoder also rejects unpaired UTF-16 surrogates in CLR input.
            if (Utf8.GetByteCount(json) > MaxBytes) throw TooLarge();
            var bytes = Utf8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaxDepth });
            if (!reader.Read()) throw Invalid("A JSON body is required.");
            var result = Read(ref reader, preserveSecrets, preserveSource || preserveSecrets);
            if (reader.Read()) throw Invalid("Only one JSON value is allowed.");
            return result;
        }
        catch (BosException) { throw; }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Invalid("Malformed JSON or invalid Unicode.");
        }
    }

    /// <param name="preserveSource">Preserve scalar source strings verbatim for document storage; all other strings remain canonical.</param>
    public static string Serialize(JsonNode? value, bool preserveSource = false)
    {
        try
        {
            var normalized = NormalizeCore(value, 0, preserveSource);
            var result = normalized?.ToJsonString(Options) ?? "null";
            if (Utf8.GetByteCount(result) > MaxBytes) throw TooLarge();
            return result;
        }
        catch (BosException) { throw; }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Invalid("Value is not valid bounded JSON.");
        }
    }

    public static string Hash(JsonNode? value) =>
        Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(Serialize(value))));

    public static JsonNode? Normalize(JsonNode? value) => Parse(Serialize(value));

    internal static string Text(string value) => RawText(value).Normalize(NormalizationForm.FormC);

    private static string RawText(string value)
    {
        try
        {
            if (Utf8.GetByteCount(value) > MaxBytes) throw TooLarge();
            return value;
        }
        catch (ArgumentException) { throw Invalid("Invalid Unicode."); }
    }

    private static JsonNode? Read(ref Utf8JsonReader reader, bool preserveSecrets, bool preserveSource, bool rawValue = false)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var obj = new JsonObject();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) throw Invalid("Expected an object key.");
                    CheckEscapes(reader.ValueSpan);
                    var name = Text(reader.GetString()!);
                    if (obj.ContainsKey(name)) throw Invalid("Duplicate JSON object key.");
                    if (!reader.Read()) throw Invalid("Expected a property value.");
                    obj.Add(name, Read(ref reader, preserveSecrets, preserveSource,
                        (preserveSecrets && name is ("password" or "currentPassword" or "newPassword"))
                        || (preserveSource && name == "source")));
                }
                if (reader.TokenType != JsonTokenType.EndObject) throw Invalid("Unclosed object.");
                return obj;
            case JsonTokenType.StartArray:
                var array = new JsonArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) array.Add(Read(ref reader, preserveSecrets, preserveSource));
                if (reader.TokenType != JsonTokenType.EndArray) throw Invalid("Unclosed array.");
                return array;
            case JsonTokenType.String:
                CheckEscapes(reader.ValueSpan);
                var text = reader.GetString()!;
                return JsonValue.Create(rawValue ? text : Text(text));
            case JsonTokenType.Number:
                return JsonNode.Parse(Number(Encoding.UTF8.GetString(reader.ValueSpan)));
            case JsonTokenType.True: return JsonValue.Create(true);
            case JsonTokenType.False: return JsonValue.Create(false);
            case JsonTokenType.Null: return null;
            default: throw Invalid("Unexpected JSON token.");
        }
    }

    // GetString implementations can replace malformed escaped surrogates; reject before decoding.
    private static void CheckEscapes(ReadOnlySpan<byte> token)
    {
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] != '\\') continue;
            if (++i >= token.Length) throw Invalid("Invalid escape.");
            if (token[i] != 'u') continue;
            var code = Hex(token, i + 1);
            i += 4;
            if (code is >= 0xdc00 and <= 0xdfff) throw Invalid("Unpaired Unicode surrogate.");
            if (code is < 0xd800 or > 0xdbff) continue;
            if (i + 6 >= token.Length || token[i + 1] != '\\' || token[i + 2] != 'u')
                throw Invalid("Unpaired Unicode surrogate.");
            var low = Hex(token, i + 3);
            if (low is < 0xdc00 or > 0xdfff) throw Invalid("Unpaired Unicode surrogate.");
            i += 6;
        }
    }

    private static int Hex(ReadOnlySpan<byte> token, int start)
    {
        if (start + 4 > token.Length) throw Invalid("Invalid Unicode escape.");
        var result = 0;
        for (var j = start; j < start + 4; j++)
        {
            var digit = token[j] switch
            {
                >= (byte)'0' and <= (byte)'9' => token[j] - '0',
                >= (byte)'a' and <= (byte)'f' => token[j] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => token[j] - 'A' + 10,
                _ => -1
            };
            if (digit < 0) throw Invalid("Invalid Unicode escape.");
            result = result * 16 + digit;
        }
        return result;
    }

    private static JsonNode? NormalizeCore(JsonNode? value, int depth, bool preserveSource, bool rawValue = false)
    {
        if (depth > MaxDepth) throw Invalid("JSON nesting limit exceeded.");
        if (value is null) return null;
        if (value is JsonObject obj)
        {
            var entries = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var pair in obj)
                if (!entries.TryAdd(Text(pair.Key), pair.Value)) throw Invalid("Duplicate normalized JSON key.");
            var result = new JsonObject();
            foreach (var pair in entries)
                result.Add(pair.Key, NormalizeCore(pair.Value, depth + 1, preserveSource, preserveSource && pair.Key == "source"));
            return result;
        }
        if (value is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array) result.Add(NormalizeCore(item, depth + 1, preserveSource));
            return result;
        }
        return value.GetValueKind() switch
        {
            JsonValueKind.String => JsonValue.Create(rawValue ? RawText(value.GetValue<string>()) : Text(value.GetValue<string>())),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            JsonValueKind.Null => null,
            JsonValueKind.Number => JsonNode.Parse(Number(value.ToJsonString())),
            _ => throw Invalid("Unsupported JSON value.")
        };
    }

    private static string Number(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var finite) || !double.IsFinite(finite))
            throw Invalid("Non-finite JSON number.");
        var exponentAt = text.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? text : text[..exponentAt];
        var exponent = 0;
        if (exponentAt >= 0 && (!int.TryParse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
            || exponent < -MaxBytes || exponent > MaxBytes)) throw Invalid("JSON number is too large.");
        var negative = mantissa.StartsWith('-');
        if (negative) mantissa = mantissa[1..];
        var dot = mantissa.IndexOf('.');
        var scale = (dot < 0 ? 0 : mantissa.Length - dot - 1) - exponent;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        while (scale > 0 && digits.EndsWith('0')) { digits = digits[..^1]; scale--; }
        if (digits.Length + Math.Abs((long)scale) > MaxBytes) throw TooLarge();
        var result = scale <= 0 ? digits + new string('0', -scale)
            : scale < digits.Length ? digits.Insert(digits.Length - scale, ".")
            : "0." + new string('0', scale - digits.Length) + digits;
        return negative ? "-" + result : result;
    }

    private static BosException Invalid(string message) => new("INVALID_REQUEST", message, 400);
    private static BosException TooLarge() => new("INVALID_REQUEST", "JSON body exceeds 256 KiB.", 413);
}
