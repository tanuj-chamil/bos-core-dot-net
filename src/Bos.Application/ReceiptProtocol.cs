using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;

namespace Bos.Application;

/// <summary>Canonical root-command identity and the two secret-command receipt exceptions.</summary>
public static class ReceiptProtocol
{
    public static void ValidateKey(string key)
    {
        if (key is null || key.Length is < 16 or > 128 || key.Any(c => c > 127 || c < 32 || c == 127))
            throw new BosException("INVALID_REQUEST", "Idempotency-Key must contain 16–128 printable ASCII characters.", 400, "Idempotency-Key");
    }

    // Revisions, input and secrets belong in the hash, never in the target scope.
    public static string Scope(string method, JsonObject args)
    {
        var target = new JsonObject();
        foreach (var field in new[] { "type", "name", "id", "userId", "roleId", "principalId" })
            if (args.TryGetPropertyValue(field, out var value)) target[field] = value?.DeepClone();
        if (method is "types.create" or "flows.create" && args["definition"] is JsonObject definition && definition["name"] is { } name)
            target["name"] = name.DeepClone();
        return CanonicalJson.Serialize(new JsonObject { ["method"] = method, ["target"] = target });
    }

    public static string Key(string principal, string scope, string key)
    {
        ValidateKey(key);
        return CanonicalJson.Hash(new JsonObject { ["principalId"] = principal, ["scope"] = scope, ["key"] = key });
    }

    public static string Hash(string method, JsonObject args)
    {
        var nonsecret = (JsonObject)args.DeepClone();
        if (method == "users.setPassword") nonsecret.Remove("newPassword");
        return CanonicalJson.Hash(new JsonObject { ["method"] = method, ["contractVersion"] = 1, ["args"] = nonsecret });
    }

    /// <summary>Call before storing direct-command results. Never persist a token's one-time plaintext.</summary>
    public static JsonNode? StoredResult(string method, JsonNode? result) =>
        method == "tokens.create" ? TokenReplay(result) : result?.DeepClone();

    /// <summary>Call only after current authorization has been checked by the root dispatcher.</summary>
    public static JsonNode? ReplayResult(string method, JsonNode? result) => StoredResult(method, result);

    public static JsonObject TokenReplay(JsonNode? result)
    {
        if (result is not JsonObject issue || issue["metadata"] is not JsonObject metadata)
            throw new BosException("INTERNAL_ERROR", "Invalid token receipt.", 500);
        return new JsonObject
        {
            ["token"] = null,
            ["metadata"] = metadata.DeepClone(),
            ["replayCode"] = "SECRET_ALREADY_DISCLOSED"
        };
    }
}
