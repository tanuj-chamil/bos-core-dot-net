using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bos.Contracts;

namespace Bos.Core;

/// <summary>Strict argument binding: explicit null never selects a default.</summary>
public static class JsonArgs
{
    private static readonly UTF8Encoding SecretUtf8 = new(false, true);

    public static void Only(JsonObject args, params string[] allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var pair in args)
            if (!names.Contains(pair.Key)) throw Error(pair.Key, "Unknown argument.");
    }

    public static string String(JsonObject args, string name, string? defaultValue = null) =>
        Read(args, name, defaultValue, n => n.GetValueKind() == JsonValueKind.String, n => CanonicalJson.Text(n.GetValue<string>()));

    /// <summary>Bind a required Unicode-valid string verbatim; callers enforce their secret length policy.</summary>
    public static string Secret(JsonObject args, string name) =>
        Read<string>(args, name, null, n => n.GetValueKind() == JsonValueKind.String, n =>
        {
            try
            {
                var secret = n.GetValue<string>();
                SecretUtf8.GetByteCount(secret);
                return secret;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw Error(name, "Invalid Unicode in secret argument.");
            }
        });

    public static int Int(JsonObject args, string name, int? defaultValue = null)
    {
        if (!args.TryGetPropertyValue(name, out var node)) return defaultValue ?? throw Error(name, "Required argument is missing.");
        if (!ValueCodec.TryMetadataInt(node, out var result))
            throw Error(name, "Expected an Int32 metadata number.");
        return result;
    }

    public static bool Bool(JsonObject args, string name, bool? defaultValue = null)
    {
        if (!args.TryGetPropertyValue(name, out var node)) return defaultValue ?? throw Error(name, "Required argument is missing.");
        if (node is null || node.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw Error(name, "Expected a boolean.");
        return node.GetValue<bool>();
    }

    public static JsonObject Object(JsonObject args, string name, JsonObject? defaultValue = null) =>
        Read(args, name, defaultValue, n => n is JsonObject, n => (JsonObject)n.DeepClone());

    public static JsonArray Array(JsonObject args, string name, JsonArray? defaultValue = null) =>
        Read(args, name, defaultValue, n => n is JsonArray, n => (JsonArray)n.DeepClone());

    public static string Name(string value)
    {
        if (value is null || !Regex.IsMatch(value, "\\A[A-Za-z][A-Za-z0-9_]{0,63}\\z", RegexOptions.CultureInvariant))
            throw Error("", "Invalid name.");
        return value;
    }

    public static string Uuid(string value)
    {
        if (value is null || !Guid.TryParseExact(value, "D", out var id) || value != id.ToString("D"))
            throw Error("", "Expected a canonical lowercase UUID.");
        return value;
    }

    private static T Read<T>(JsonObject args, string name, T? fallback, Func<JsonNode, bool> accepts, Func<JsonNode, T> convert) where T : class
    {
        if (!args.TryGetPropertyValue(name, out var node))
        {
            if (fallback is null) throw Error(name, "Required argument is missing.");
            return fallback is JsonNode json ? (T)(object)json.DeepClone() : fallback;
        }
        if (node is null || !accepts(node)) throw Error(name, "Invalid argument type.");
        return convert(node);
    }

    private static BosException Error(string path, string message) => new("INVALID_REQUEST", message, 400, path);
}
