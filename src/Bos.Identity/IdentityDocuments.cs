using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bos.Contracts;
using Bos.Core;
using Microsoft.AspNetCore.Identity;

namespace Bos.Identity;

internal sealed class BosUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "human";
    public bool Active { get; set; }
    // Bootstrap/reset passwords admit exactly one login. Redemption leaves the first
    // session and hash intact so that session can verify the secret while replacing it.
    // Losing that session before replacement requires another offline bootstrap/reset.
    public bool BootstrapSecret { get; set; }
    public bool BootstrapRedeemed { get; set; }
    public int Revision { get; set; } = 1;
    public List<string> RoleIds { get; set; } = [];
}

internal sealed class BosRole : IdentityRole
{
    public int Revision { get; set; } = 1;
    public List<Grant> Grants { get; set; } = [];
}

internal static class IdentityDocuments
{
    internal static readonly string[] Actions = ["types.read", "types.manage", "record.read", "record.create", "record.update", "record.delete", "operations.execute", "flows.read", "flows.manage", "flows.execute", "runs.readAll", "identity.manage", "audit.read", "system.manage"];
    internal static readonly Regex NamePattern = new(@"\A[A-Za-z][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static BosException Invalid(string path, string message = "Invalid argument.") => new("INVALID_REQUEST", message, 422, path);
    internal static BosException Unauthenticated() => new("UNAUTHENTICATED", "Authentication failed.", 401);
    internal static void Only(JsonObject obj, params string[] names)
    {
        foreach (var key in obj.Select(x => x.Key))
            if (!names.Contains(key, StringComparer.Ordinal)) throw new BosException("UNKNOWN_FIELD", "Unknown field.", 422, key);
    }
    // Raw strings are intentional: JsonArgs.String normalizes NFC, which would change a
    // password. Names are ASCII; display names normalize explicitly; secrets never do.
    internal static string Text(JsonObject obj, string key)
        => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw Invalid(key, "A string is required.");
    internal static bool Boolean(JsonObject obj, string key) => JsonArgs.Bool(obj, key);
    internal static JsonObject Object(JsonObject obj, string key) => JsonArgs.Object(obj, key);
    internal static JsonArray Array(JsonObject obj, string key) => JsonArgs.Array(obj, key);
    internal static int Integer(JsonObject obj, string key, int max = int.MaxValue)
    {
        var n = JsonArgs.Int(obj, key);
        return n > 0 && n <= max ? n : throw Invalid(key, "A positive Int32 is required.");
    }
    internal static string Name(JsonObject obj, string key)
    {
        var name = Text(obj, key);
        if (!NamePattern.IsMatch(name)) throw Invalid(key, "Invalid name.");
        return name;
    }
    internal static string Id(JsonObject obj, string key)
    {
        var id = Text(obj, key);
        if (!Guid.TryParseExact(id, "D", out var uuid) || uuid.ToString("D") != id) throw Invalid(key, "A canonical lowercase UUID is required.");
        return id;
    }
    internal static string Display(JsonObject obj, string key)
    {
        var text = Text(obj, key);
        // Reject malformed UTF-16 before normalizing; JSON parsing also enforces this at the boundary.
        for (var i = 0; i < text.Length; i++)
            if (char.IsSurrogate(text[i]) && (!char.IsHighSurrogate(text[i]) || ++i >= text.Length || !char.IsLowSurrogate(text[i]))) throw Invalid(key);
        text = text.Normalize(NormalizationForm.FormC);
        if (text.EnumerateRunes().Count() is < 1 or > 200) throw Invalid(key, "Display name must contain 1–200 characters.");
        return text;
    }
    internal static void Password(string password)
    {
        if (password is null || password.Length is < 12 or > 128) throw Invalid("newPassword", "Password must contain 12–128 characters.");
        for (var i = 0; i < password.Length; i++)
            if (char.IsSurrogate(password[i]) && (!char.IsHighSurrogate(password[i]) || ++i >= password.Length || !char.IsLowSurrogate(password[i]))) throw Invalid("newPassword", "Password contains invalid Unicode.");
    }
    internal static List<Grant> ReadGrants(JsonArray array)
    {
        if (array.Count > 1000) throw Invalid("grants", "Too many grants.");
        var grants = new List<Grant>();
        foreach (var item in array)
        {
            var obj = item as JsonObject ?? throw Invalid("grants");
            Only(obj, "action", "resource");
            var action = Text(obj, "action"); var resource = Text(obj, "resource");
            ValidateGrant(action, resource);
            grants.Add(new(action, resource));
        }
        return grants.Distinct().OrderBy(g => g.Action, StringComparer.Ordinal).ThenBy(g => g.Resource, StringComparer.Ordinal).ToList();
    }
    internal static void ValidateGrant(string action, string resource)
    {
        if (!Actions.Contains(action, StringComparer.Ordinal)) throw Invalid("action", "Unknown action.");
        if (resource != "*" && !NamePattern.IsMatch(resource)) throw Invalid("resource", "Invalid resource.");
        if (action is "identity.manage" or "audit.read" or "system.manage" or "runs.readAll" && resource != "*") throw Invalid("resource", "Administrative grants require resource '*'.");
    }
    internal static bool Covers(IEnumerable<Grant> grants, string action, string resource)
        => grants.Any(g => g.Action == action && (g.Resource == "*" || g.Resource == resource));
    internal static JsonArray Grants(IEnumerable<Grant> grants) => new(grants.Select(g => (JsonNode)new JsonObject { ["action"] = g.Action, ["resource"] = g.Resource }).ToArray());
    internal static JsonArray Roles(IEnumerable<string> ids) => new(ids.Order(StringComparer.Ordinal).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    internal static string Instant(DateTimeOffset value) => value.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture);
    internal static DateTimeOffset ParseInstant(string value, string path)
    {
        // Public expiry uses the same RFC 3339 grammar and millisecond precision as
        // record values. In particular, excess fractional digits must fail, not round.
        var schema = new JsonObject { ["fields"] = new JsonObject { [path] = new JsonObject { ["kind"] = "instant", ["required"] = true } } };
        var normalized = Schema.Normalize(schema, new JsonObject { [path] = value }, applyDefaults: false);
        return DateTimeOffset.ParseExact(Text(normalized, path), InstantFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
    private static DateTimeOffset StoredInstant(string value, string path)
    {
        // Existing documents used round-trip instants. Continue to read those without
        // admitting the legacy precision through public token issuance. Lockout state
        // also retains exact ticks internally, so normalization cannot shorten lockout.
        if (!DateTimeOffset.TryParseExact(value, ["O", InstantFormat], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)) throw Invalid(path, "Invalid stored instant.");
        return instant;
    }
    internal static BosUser User(JsonObject d) => new()
    {
        Id = Text(d, "id"), UserName = Text(d, "userName"), NormalizedUserName = Text(d, "normalizedUserName"),
        DisplayName = Text(d, "displayName"), Kind = Text(d, "kind"), Active = Boolean(d, "active"), Revision = Integer(d, "revision"),
        PasswordHash = d["passwordHash"]?.GetValue<string>(), SecurityStamp = d["securityStamp"]?.GetValue<string>(), ConcurrencyStamp = d["concurrencyStamp"]?.GetValue<string>(),
        AccessFailedCount = d["accessFailedCount"]?.GetValue<int>() ?? 0, LockoutEnabled = d["lockoutEnabled"]?.GetValue<bool>() ?? true,
        BootstrapSecret = d["bootstrapSecret"]?.GetValue<bool>() ?? false, BootstrapRedeemed = d["bootstrapRedeemed"]?.GetValue<bool>() ?? false,
        LockoutEnd = d["lockoutEnd"] is JsonValue end ? StoredInstant(end.GetValue<string>(), "lockoutEnd") : null,
        RoleIds = Array(d, "roles").Select(n => n!.GetValue<string>()).ToList()
    };
    internal static BosRole Role(JsonObject d) => new() { Id = Text(d, "id"), Name = Text(d, "name"), NormalizedName = Text(d, "normalizedName"), ConcurrencyStamp = Text(d, "concurrencyStamp"), Revision = Integer(d, "revision"), Grants = ReadGrants(Array(d, "grants")) };
    internal static JsonObject Summary(BosUser u) => new() { ["id"] = u.Id, ["userName"] = u.UserName, ["displayName"] = u.DisplayName, ["kind"] = u.Kind, ["active"] = u.Active, ["roles"] = Roles(u.RoleIds), ["revision"] = u.Revision };
    internal static JsonObject Document(BosUser u)
    {
        var d = Summary(u);
        d["normalizedUserName"] = u.NormalizedUserName; d["passwordHash"] = u.PasswordHash; d["securityStamp"] = u.SecurityStamp; d["concurrencyStamp"] = u.ConcurrencyStamp;
        d["accessFailedCount"] = u.AccessFailedCount; d["lockoutEnabled"] = u.LockoutEnabled; d["lockoutEnd"] = u.LockoutEnd is { } end ? end.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : null;
        d["bootstrapSecret"] = u.BootstrapSecret; d["bootstrapRedeemed"] = u.BootstrapRedeemed;
        return d;
    }
    internal static JsonObject Summary(BosRole r) => new() { ["id"] = r.Id, ["name"] = r.Name, ["grants"] = Grants(r.Grants), ["revision"] = r.Revision };
    internal static JsonObject Document(BosRole r)
    {
        var d = Summary(r); d["normalizedName"] = r.NormalizedName; d["concurrencyStamp"] = r.ConcurrencyStamp; return d;
    }
    internal static JsonObject TokenMetadata(JsonObject d)
    {
        var metadata = new JsonObject();
        foreach (var field in new[] { "id", "principalId", "scopes", "createdAt", "expiresAt", "revokedAt" })
            if (d.ContainsKey(field)) metadata[field] = d[field]?.DeepClone();
        foreach (var field in new[] { "createdAt", "expiresAt", "revokedAt" })
            if (metadata[field] is not null) metadata[field] = Instant(StoredInstant(Text(metadata, field), field));
        return metadata;
    }
    internal static bool TokenValid(JsonObject token, string principalId, DateTimeOffset now)
        => Text(token, "principalId") == principalId && token["revokedAt"] is null && StoredInstant(Text(token, "expiresAt"), "expiresAt") > now;
    internal static async Task<List<Grant>> EffectiveAsync(IUnitOfWork uow, BosUser user, CancellationToken ct)
    {
        var grants = new List<Grant>();
        foreach (var id in user.RoleIds.Distinct(StringComparer.Ordinal))
            if (await uow.Documents.GetAsync("roles", id, ct) is { } role) grants.AddRange(Role(role).Grants);
        return grants.Distinct().OrderBy(g => g.Action, StringComparer.Ordinal).ThenBy(g => g.Resource, StringComparer.Ordinal).ToList();
    }
    internal static bool Active(BosUser user) => user.Active && (user.Kind == "service" || user.Kind == "human" && user.PasswordHash is not null);
    internal static void Revision(int expected, int actual)
    {
        if (expected != actual) throw new BosException("REVISION_CONFLICT", "Revision does not match.", 409);
        if (actual == int.MaxValue) throw new BosException("CAPACITY_EXCEEDED", "Revision limit exceeded.");
    }
    internal static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] Unbase64(string text) => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
    internal static bool EqualSecret(string a, string b) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
