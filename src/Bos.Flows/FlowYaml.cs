using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bos.Contracts;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Bos.Flows;

// Two event passes are deliberate: no tree is allocated until the entire stream
// has passed the safe YAML profile (in particular, before any alias expansion).
internal static class FlowYaml
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static JsonObject Parse(string source)
    {
        try
        {
            if (Utf8.GetByteCount(source) > 65536) throw Error("YAML exceeds 64 KiB.");
            Read(source, false);
            return Read(source, true) as JsonObject ?? throw Error("The flow must be a mapping.");
        }
        catch (BosException) { throw; }
        catch (Exception e) when (e is YamlException or EncoderFallbackException or FormatException or OverflowException or ArgumentException)
        { throw Error("Invalid YAML: " + e.Message); }
    }

    private static JsonNode? Read(string source, bool materialize)
    {
        var parser = new Parser(new StringReader(source));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        int nodes = 0;
        var result = Node(0);
        parser.Consume<DocumentEnd>();
        if (!parser.TryConsume<StreamEnd>(out _)) throw Error("Exactly one YAML document is required.");
        return result;

        JsonNode? Node(int depth)
        {
            if (depth > 64 || ++nodes > 32768) throw Error("YAML nesting/node limit exceeded.");
            var current = parser.Current;
            if (current is AnchorAlias) throw Error("Aliases are forbidden.");
            if (current is NodeEvent ne && (!ne.Anchor.IsEmpty || !ne.Tag.IsEmpty))
                throw Error("Anchors and explicit tags are forbidden.");
            if (parser.TryConsume<Scalar>(out var scalar))
                return ScalarValue(scalar, materialize);
            if (parser.TryConsume<SequenceStart>(out _))
            {
                var array = materialize ? new JsonArray() : null;
                while (!parser.TryConsume<SequenceEnd>(out _))
                {
                    var value = Node(depth + 1);
                    if (materialize) array!.Add(value);
                }
                return array;
            }
            if (parser.TryConsume<MappingStart>(out _))
            {
                var obj = materialize ? new JsonObject() : null;
                var keys = new HashSet<string>(StringComparer.Ordinal);
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    if (parser.Current is not Scalar key || !key.Anchor.IsEmpty || !key.Tag.IsEmpty)
                        throw Error("Mapping keys must be untagged strings.");
                    parser.MoveNext();
                    if (key.Value == "<<" || !IsString(key) || ScalarValue(key, true) is not JsonValue keyValue ||
                        !keyValue.TryGetValue<string>(out _) || !keys.Add(key.Value.Normalize()))
                        throw Error("Duplicate, merge or non-string mapping key.");
                    var value = Node(depth + 1);
                    if (materialize) obj!.Add(key.Value.Normalize(), value);
                }
                return obj;
            }
            throw Error("Expected a mapping, sequence or scalar.");
        }
    }

    private static bool IsString(Scalar s) => s.Style != ScalarStyle.Plain ||
        !(s.Value is "true" or "false" or "null" || Regex.IsMatch(s.Value, @"^[+-]?[0-9]"));

    private static JsonNode? ScalarValue(Scalar s, bool materialize)
    {
        string text = s.Value;
        if (s.Style != ScalarStyle.Plain) return materialize ? JsonValue.Create(text.Normalize()) : null;
        if (text == "null") return null;
        if (text is "true" or "false") return materialize ? JsonValue.Create(text == "true") : null;
        if (Regex.IsMatch(text, @"^-?(0|[1-9][0-9]*)$"))
        {
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n))
                throw Error("Unquoted integers are bounded metadata; quote business numbers.");
            return materialize ? JsonValue.Create(n) : null;
        }
        if (text.Length == 0 || text is "~" or "Null" or "NULL" or "True" or "TRUE" or "False" or "FALSE" ||
            Regex.IsMatch(text, @"^[+-]?([0-9]|\.[0-9])|^[+-]?\.(inf|Inf|INF|nan|NaN|NAN)$"))
            throw Error("Only JSON-like scalars are accepted; quote business values.");
        return materialize ? JsonValue.Create(text.Normalize()) : null;
    }

    internal static BosException Error(string message, string path = "") => new("FLOW_INVALID", message, 422, path);
}
