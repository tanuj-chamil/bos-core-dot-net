using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bos.Contracts;
using Bos.Core;
using Microsoft.AspNetCore.Identity;
using static Bos.Identity.IdentityDocuments;

namespace Bos.Identity;

/// <summary>Local Identity accounts and BOS service credentials, enlisted in BOS document UoWs.</summary>
public sealed class IdentityService(IUnitOfWorkFactory factory, IClock clock, IIdGenerator ids) : IIdentityService
{
    private readonly Authorization authorization = new(clock);
    private byte[]? cursorKey;
    private static readonly BosUser DummyUser = new();
    private static readonly PasswordHasher<BosUser> DummyHasher = new();
    private static readonly string DummyHash = DummyHasher.HashPassword(DummyUser, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public async Task<JsonNode?> InvokeAsync(string method, JsonObject args, InvocationContext context, IUnitOfWork uow, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (context.RunId is not null || context.OperationDepth > 0) throw new BosException("FORBIDDEN", "Identity administration is unavailable in business execution.", 403);
        if (method == "identity.getCurrent")
        {
            Only(args);
            var user = await GetUserAsync(uow, context.PrincipalId, ct);
            return new JsonObject { ["id"] = user.Id, ["displayName"] = user.DisplayName, ["roles"] = Roles(user.RoleIds), ["effectiveGrants"] = Grants(EffectiveContextGrants(context)) };
        }
        if (method == "authorization.can")
        {
            Only(args, "action", "resource");
            var action = Text(args, "action"); var resource = Text(args, "resource");
            ValidateGrant(action, resource);
            return JsonValue.Create(authorization.Can(context, action, resource));
        }
        if (!Methods.Contains(method, StringComparer.Ordinal)) throw new BosException("NOT_FOUND", "Unknown identity method.", 404);
        authorization.Require(context, "identity.manage", "*");
        var mutating = !method.EndsWith(".get", StringComparison.Ordinal) && !method.EndsWith(".list", StringComparison.Ordinal);
        if (mutating && (!uow.IsWrite || context.ReadOnly)) throw new BosException("MODE_VIOLATION", "A writable transaction is required.");
        using var managers = new IdentityManagers(uow, clock, ct);
        JsonNode result;
        switch (method)
        {
            case "users.create":
            {
                Only(args, "user"); var input = Object(args, "user"); Only(input, "userName", "displayName", "kind");
                var kind = Text(input, "kind"); if (kind is not ("human" or "service")) throw Invalid("kind");
                var user = new BosUser { Id = NewId(), UserName = Name(input, "userName"), DisplayName = Display(input, "displayName"), Kind = kind, Active = false, LockoutEnabled = true };
                Check(await managers.Users.CreateAsync(user)); result = Summary(user); break;
            }
            case "users.get":
                Only(args, "id"); return Summary(await GetUserAsync(uow, Id(args, "id"), ct));
            case "users.list":
                Only(args, "query"); return await ListAsync(uow, context, "users", Query(args), null, ct);
            case "users.update":
            case "users.disable":
            {
                var disable = method == "users.disable";
                Only(args, disable ? ["id", "expectedRevision"] : ["id", "changes", "expectedRevision"]);
                var user = await GetUserAsync(uow, Id(args, "id"), ct); Revision(Integer(args, "expectedRevision"), user.Revision);
                var changes = disable ? new JsonObject { ["active"] = false } : Object(args, "changes"); Only(changes, "displayName", "active");
                var displayName = changes.ContainsKey("displayName") ? Display(changes, "displayName") : user.DisplayName;
                var active = changes.ContainsKey("active") ? Boolean(changes, "active") : user.Active;
                if (displayName == user.DisplayName && active == user.Active) throw NoChange();
                if (active && user.Kind == "human" && !await managers.Users.HasPasswordAsync(user)) throw new BosException("INVALID_STATE", "Set a password before activating a human account.");
                var deactivating = user.Active && !active;
                user.DisplayName = displayName; user.Active = active; user.Revision++;
                await PreserveAdministratorAsync(uow, user, null, ct);
                if (deactivating)
                {
                    Check(await managers.Users.UpdateSecurityStampAsync(user));
                    await RevokeUserTokensAsync(uow, user.Id, ct);
                }
                else Check(await managers.Users.UpdateAsync(user));
                result = Summary(user); break;
            }
            case "users.setPassword":
            {
                Only(args, "id", "newPassword", "expectedRevision");
                var user = await GetUserAsync(uow, Id(args, "id"), ct); Revision(Integer(args, "expectedRevision"), user.Revision);
                var password = JsonArgs.Secret(args, "newPassword"); Password(password);
                if (user.Kind != "human") throw new BosException("INVALID_STATE", "Only human accounts have passwords.");
                user.Revision++;
                await SetPasswordAsync(managers.Users, user, password);
                result = new JsonObject { ["userId"] = user.Id, ["revision"] = user.Revision, ["passwordChanged"] = true }; break;
            }
            case "roles.create":
            {
                Only(args, "role"); var input = Object(args, "role"); Only(input, "name", "grants");
                var role = new BosRole { Id = NewId(), Name = Name(input, "name"), Grants = ReadGrants(Array(input, "grants")) };
                Check(await managers.Roles.CreateAsync(role)); result = Summary(role); break;
            }
            case "roles.get":
                Only(args, "id"); return Summary(await GetRoleAsync(uow, Id(args, "id"), ct));
            case "roles.list":
                Only(args, "query"); return await ListAsync(uow, context, "roles", Query(args), null, ct);
            case "roles.update":
            {
                Only(args, "id", "changes", "expectedRevision"); var changes = Object(args, "changes"); Only(changes, "grants");
                var role = await GetRoleAsync(uow, Id(args, "id"), ct); Revision(Integer(args, "expectedRevision"), role.Revision);
                if (changes.Count == 0) throw NoChange();
                var grants = ReadGrants(Array(changes, "grants"));
                if (role.Grants.SequenceEqual(grants)) throw NoChange();
                role.Grants = grants; role.Revision++;
                await PreserveAdministratorAsync(uow, null, role, ct);
                Check(await managers.Roles.UpdateAsync(role)); result = Summary(role); break;
            }
            case "roles.assign":
            case "roles.remove":
            {
                Only(args, "userId", "roleId", "expectedUserRevision");
                var user = await GetUserAsync(uow, Id(args, "userId"), ct); Revision(Integer(args, "expectedUserRevision"), user.Revision);
                var role = await GetRoleAsync(uow, Id(args, "roleId"), ct);
                var assign = method == "roles.assign"; var hasRole = user.RoleIds.Contains(role.Id, StringComparer.Ordinal);
                if (assign == hasRole) throw NoChange();
                // Check the prospective membership without writing any document first.
                if (assign) user.RoleIds.Add(role.Id); else user.RoleIds.Remove(role.Id);
                await PreserveAdministratorAsync(uow, user, null, ct);
                if (assign) user.RoleIds.Remove(role.Id); else user.RoleIds.Add(role.Id);
                user.Revision++;
                Check(assign ? await managers.Users.AddToRoleAsync(user, role.Name!) : await managers.Users.RemoveFromRoleAsync(user, role.Name!));
                result = Summary(user); break;
            }
            case "tokens.create":
            {
                Only(args, "principalId", "scopes", "expiresAt");
                var user = await GetUserAsync(uow, Id(args, "principalId"), ct);
                if (!Active(user) || user.Kind != "service") throw new BosException("INVALID_STATE", "Tokens require an active service principal.");
                var scopes = ReadGrants(Array(args, "scopes")); if (scopes.Count == 0) throw Invalid("scopes", "At least one explicit scope is required.");
                var targetGrants = await EffectiveAsync(uow, user, ct);
                if (scopes.Any(g => !Covers(targetGrants, g.Action, g.Resource) || !authorization.Can(context, g.Action, g.Resource))) throw new BosException("FORBIDDEN", "Token scopes exceed target or issuer authority.", 403);
                var now = clock.UtcNow;
                var expiry = args.ContainsKey("expiresAt") ? ParseInstant(Text(args, "expiresAt"), "expiresAt") : now.AddDays(30);
                if (expiry <= now || expiry > now.AddDays(365)) throw Invalid("expiresAt", "Expiry must be in the future and within 365 days.");
                var secret = RandomNumberGenerator.GetBytes(32); var id = NewId();
                var token = new JsonObject { ["id"] = id, ["principalId"] = user.Id, ["scopes"] = Grants(scopes), ["createdAt"] = Instant(now), ["expiresAt"] = Instant(expiry), ["secretHash"] = Convert.ToHexStringLower(SHA256.HashData(secret)) };
                await uow.Documents.PutAsync("tokens", id, token, ct);
                result = new JsonObject { ["token"] = $"bos_{id}.{Base64(secret)}", ["metadata"] = TokenMetadata(token) };
                CryptographicOperations.ZeroMemory(secret); break;
            }
            case "tokens.list":
                Only(args, "principalId", "query"); return await ListAsync(uow, context, "tokens", Query(args), Id(args, "principalId"), ct);
            case "tokens.revoke":
            {
                Only(args, "id"); var id = Id(args, "id");
                var token = await uow.Documents.GetAsync("tokens", id, ct) ?? throw NotFound();
                if (token["revokedAt"] is not null) return TokenMetadata(token);
                token["revokedAt"] = Instant(clock.UtcNow); await uow.Documents.PutAsync("tokens", id, token, ct);
                result = TokenMetadata(token); break;
            }
            default: throw new BosException("NOT_FOUND", "Unknown identity method.", 404);
        }
        await AuditAsync(uow, method, result, context, ct);
        return result;
    }

    public async Task<(Caller Caller, JsonObject User)> LoginAsync(string userName, string password, CancellationToken ct = default)
    {
        if (userName is null || password is null || password.Length > 128 || !NamePattern.IsMatch(userName)) { DummyVerify(password); throw Unauthenticated(); }
        await using var uow = await factory.BeginWriteAsync(ct);
        using var managers = new IdentityManagers(uow, clock, ct);
        var user = await managers.Users.FindByNameAsync(userName);
        if (user is null || !Active(user) || user.Kind != "human") { DummyVerify(password); throw Unauthenticated(); }
        if (user.BootstrapSecret && user.BootstrapRedeemed) { DummyVerify(password); throw Unauthenticated(); }
        if (await managers.Users.IsLockedOutAsync(user)) { DummyVerify(password); throw Unauthenticated(); }
        if (!await managers.Users.CheckPasswordAsync(user, password))
        {
            Check(await managers.Users.AccessFailedAsync(user));
            await uow.CommitAsync(ct); // Failed access counts must survive the authentication error.
            throw Unauthenticated();
        }
        Check(await managers.Users.ResetAccessFailedCountAsync(user));
        if (user.BootstrapSecret)
        {
            // Redeem with successful password verification, before returning a Caller.
            // A failed/rolled-back commit cannot burn the secret. Serialized SQLite
            // writers ensure two simultaneous attempts cannot both redeem it.
            user.BootstrapRedeemed = true;
            Check(await managers.Users.UpdateAsync(user));
        }
        var caller = new Caller(user.Id, SecurityStamp: await managers.Users.GetSecurityStampAsync(user));
        var summary = Summary(user);
        await uow.CommitAsync(ct);
        return (caller, summary);
    }

    public async Task ChangePasswordAsync(Caller caller, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        Password(newPassword);
        await using var uow = await factory.BeginWriteAsync(ct);
        var context = new InvocationContext { Caller = caller };
        await authorization.ReloadAsync(uow, context, ct);
        if (caller.CredentialId is not null) throw new BosException("FORBIDDEN", "A human session is required.", 403);
        using var managers = new IdentityManagers(uow, clock, ct);
        var user = await GetUserAsync(uow, caller.PrincipalId, ct);
        if (user.Kind != "human" || await managers.Users.IsLockedOutAsync(user)) throw Unauthenticated();
        if (currentPassword is null || currentPassword.Length > 128 || !await managers.Users.CheckPasswordAsync(user, currentPassword))
        {
            Check(await managers.Users.AccessFailedAsync(user)); await uow.CommitAsync(ct); throw Unauthenticated();
        }
        Revision(user.Revision, user.Revision); user.Revision++;
        user.BootstrapSecret = false; user.BootstrapRedeemed = false;
        Check(await managers.Users.ChangePasswordAsync(user, currentPassword, newPassword));
        Check(await managers.Users.ResetAccessFailedCountAsync(user));
        await AuditAsync(uow, "auth.changePassword", new JsonObject { ["userId"] = user.Id, ["revision"] = user.Revision }, context, ct);
        await uow.CommitAsync(ct);
    }

    public async Task<Caller?> AuthenticateTokenAsync(string token, CancellationToken ct = default)
    {
        if (token is null || token.Length != 84 || !token.StartsWith("bos_", StringComparison.Ordinal) || token[40] != '.') return null;
        var id = token.Substring(4, 36); var encodedSecret = token[41..];
        if (!Guid.TryParseExact(id, "D", out var uuid) || uuid.ToString("D") != id || encodedSecret.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        byte[] secret;
        try { secret = Unbase64(encodedSecret); } catch (FormatException) { return null; }
        if (secret.Length != 32 || Base64(secret) != encodedSecret) { CryptographicOperations.ZeroMemory(secret); return null; }
        var hash = SHA256.HashData(secret); CryptographicOperations.ZeroMemory(secret);
        await using var uow = await factory.BeginReadAsync(ct);
        var stored = await uow.Documents.GetAsync("tokens", id, ct);
        if (stored is null) return null;
        byte[] storedHash;
        try { storedHash = Convert.FromHexString(Text(stored, "secretHash")); } catch (FormatException) { return null; }
        if (storedHash.Length != 32 || !CryptographicOperations.FixedTimeEquals(hash, storedHash)) return null;
        var principalId = Text(stored, "principalId");
        if (!TokenValid(stored, principalId, clock.UtcNow) || await uow.Documents.GetAsync("users", principalId, ct) is not { } doc) return null;
        var user = User(doc);
        if (!Active(user) || user.Kind != "service") return null;
        return new Caller(principalId, id, ReadGrants(Array(stored, "scopes")).ToArray(), user.SecurityStamp);
    }

    public async Task<JsonObject> BootstrapAsync(IUnitOfWork uow, string password, CancellationToken ct = default)
    {
        if (!uow.IsWrite) throw new BosException("MODE_VIOLATION", "Bootstrap requires a writable transaction.");
        Password(password);
        await EnsureCursorKeyAsync(uow, ct);
        using var managers = new IdentityManagers(uow, clock, ct);
        var users = (await uow.Documents.ListAsync("users", ct: ct)).Select(User).ToList();
        BosUser? admin = null;
        foreach (var user in users.OrderBy(u => u.UserName == "Admin" ? 0 : 1).ThenBy(u => u.Id, StringComparer.Ordinal))
            if (user.Kind == "human" && Active(user) && Covers(await EffectiveAsync(uow, user, ct), "identity.manage", "*")) { admin = user; break; }
        admin ??= await managers.Users.FindByNameAsync("Admin");
        if (admin is { Kind: not "human" }) throw new BosException("INVALID_STATE", "The bootstrap account name is already reserved by a service principal.");
        var role = await managers.Roles.FindByNameAsync("Administrators");
        var allGrants = Actions.Select(action => new Grant(action, "*")).OrderBy(g => g.Action, StringComparer.Ordinal).ToList();
        if (role is null)
        {
            role = new BosRole { Id = NewId(), Name = "Administrators", Grants = allGrants };
            Check(await managers.Roles.CreateAsync(role));
        }
        else if (!role.Grants.SequenceEqual(allGrants))
        {
            Revision(role.Revision, role.Revision); role.Grants = allGrants; role.Revision++;
            Check(await managers.Roles.UpdateAsync(role));
        }
        if (admin is null)
        {
            admin = new BosUser { Id = NewId(), UserName = "Admin", DisplayName = "Administrator", Kind = "human", Active = true, LockoutEnabled = true, BootstrapSecret = true, BootstrapRedeemed = false };
            Check(await managers.Users.CreateAsync(admin, password));
        }
        else
        {
            Revision(admin.Revision, admin.Revision); admin.Revision++; admin.Active = true; admin.AccessFailedCount = 0; admin.LockoutEnd = null;
            await SetPasswordAsync(managers.Users, admin, password, bootstrapSecret: true);
        }
        if (!admin.RoleIds.Contains(role.Id, StringComparer.Ordinal)) Check(await managers.Users.AddToRoleAsync(admin, role.Name!));
        await uow.Documents.AppendAuditAsync(new JsonObject { ["actor"] = "operator", ["method"] = "identity.bootstrap", ["objectId"] = admin.Id, ["revision"] = admin.Revision, ["at"] = Instant(clock.UtcNow) }, ct);
        return Summary(admin);
    }

    private static readonly string[] Methods = ["users.create", "users.get", "users.list", "users.update", "users.disable", "users.setPassword", "roles.create", "roles.get", "roles.list", "roles.update", "roles.assign", "roles.remove", "tokens.create", "tokens.list", "tokens.revoke"];
    private string NewId()
    {
        var id = ids.NewId();
        if (!Guid.TryParseExact(id, "D", out var uuid) || uuid.ToString("D") != id) throw new InvalidOperationException("IIdGenerator must produce canonical lowercase UUIDs.");
        return id;
    }
    private static void DummyVerify(string? password) => DummyHasher.VerifyHashedPassword(DummyUser, DummyHash, password is { Length: <= 128 } ? password : "invalid credentials");
    private static BosException NoChange() => new("NO_CHANGE", "No change was requested.", 409);
    private static BosException NotFound() => new("NOT_FOUND", "Identity object not found.", 404);
    private static async Task<BosUser> GetUserAsync(IUnitOfWork uow, string id, CancellationToken ct) => await uow.Documents.GetAsync("users", id, ct) is { } d ? User(d) : throw NotFound();
    private static async Task<BosRole> GetRoleAsync(IUnitOfWork uow, string id, CancellationToken ct) => await uow.Documents.GetAsync("roles", id, ct) is { } d ? Role(d) : throw NotFound();
    private static void Check(IdentityResult result)
    {
        if (result.Succeeded) return;
        var codes = result.Errors.Select(e => e.Code).ToArray();
        if (codes.Any(c => c.StartsWith("Duplicate", StringComparison.Ordinal))) throw new BosException("UNIQUE_CONFLICT", "The name is already in use.", 409);
        if (codes.Contains("ConcurrencyFailure", StringComparer.Ordinal)) throw new BosException("REVISION_CONFLICT", "Identity changed concurrently.", 409);
        if (codes.Contains("PasswordMismatch", StringComparer.Ordinal)) throw Unauthenticated();
        throw Invalid("identity", "Identity validation failed.");
    }
    private static async Task SetPasswordAsync(UserManager<BosUser> users, BosUser user, string password, bool bootstrapSecret = false)
    {
        // Both manager updates enlist in the same supplied transaction. No reset-token provider
        // or separate Identity database is needed for this privileged recovery command.
        user.BootstrapSecret = bootstrapSecret; user.BootstrapRedeemed = false;
        if (await users.HasPasswordAsync(user)) Check(await users.RemovePasswordAsync(user));
        Check(await users.AddPasswordAsync(user, password));
        user.AccessFailedCount = 0; user.LockoutEnd = null;
        Check(await users.UpdateAsync(user));
    }
    private async Task RevokeUserTokensAsync(IUnitOfWork uow, string principalId, CancellationToken ct)
    {
        foreach (var token in await uow.Documents.ListAsync("tokens", ct: ct))
            if (Text(token, "principalId") == principalId && token["revokedAt"] is null)
            { token["revokedAt"] = Instant(clock.UtcNow); await uow.Documents.PutAsync("tokens", Text(token, "id"), token, ct); }
    }
    private static async Task PreserveAdministratorAsync(IUnitOfWork uow, BosUser? replacementUser, BosRole? replacementRole, CancellationToken ct)
    {
        var users = (await uow.Documents.ListAsync("users", ct: ct)).Select(User).ToList();
        var roles = (await uow.Documents.ListAsync("roles", ct: ct)).Select(Role).ToDictionary(r => r.Id, StringComparer.Ordinal);
        // Administrator predicate: an ACTIVE principal with identity.manage on '*', through
        // any currently assigned role. Active humans additionally require a password hash.
        // No magic role ID/name, cached claims, token scope, or mere role membership counts.
        bool IsAdmin(BosUser user, bool prospective)
        {
            if (prospective && replacementUser?.Id == user.Id) user = replacementUser;
            return Active(user) && user.RoleIds.Any(id =>
            {
                var role = prospective && replacementRole?.Id == id ? replacementRole : roles.GetValueOrDefault(id);
                return role is not null && Covers(role.Grants, "identity.manage", "*");
            });
        }
        if (users.Any(u => IsAdmin(u, false)) && !users.Any(u => IsAdmin(u, true))) throw new BosException("POLICY_VIOLATION", "The last active administrator must be preserved.", 409);
    }
    private static List<Grant> EffectiveContextGrants(InvocationContext context)
    {
        IEnumerable<Grant> result = context.Grants;
        foreach (var ceiling in context.Ceilings)
            result = result.SelectMany(grant => ceiling.Where(c => c.Action == grant.Action && (c.Resource == "*" || grant.Resource == "*" || c.Resource == grant.Resource))
                .Select(c => new Grant(grant.Action, grant.Resource == "*" ? c.Resource : grant.Resource))).Distinct().ToArray();
        return result.OrderBy(g => g.Action, StringComparer.Ordinal).ThenBy(g => g.Resource, StringComparer.Ordinal).ToList();
    }
    private async Task AuditAsync(IUnitOfWork uow, string method, JsonNode result, InvocationContext context, CancellationToken ct)
    {
        var obj = result as JsonObject;
        var metadata = obj?["metadata"] as JsonObject ?? obj;
        await uow.Documents.AppendAuditAsync(new JsonObject { ["actor"] = context.PrincipalId, ["method"] = method, ["commandId"] = context.CommandId, ["runId"] = context.RunId,
            ["objectId"] = (metadata?["id"] ?? metadata?["userId"])?.DeepClone(), ["revision"] = metadata?["revision"]?.DeepClone(), ["at"] = Instant(clock.UtcNow) }, ct);
    }
    private static JsonObject Query(JsonObject args) => args.ContainsKey("query") ? Object(args, "query") : new JsonObject();

    private async Task<JsonObject> ListAsync(IUnitOfWork uow, InvocationContext context, string collection, JsonObject query, string? principalId, CancellationToken ct)
    {
        Only(query, "where", "limit", "cursor");
        var limit = query.ContainsKey("limit") ? Integer(query, "limit", 200) : 50;
        var where = query.ContainsKey("where") ? Array(query, "where") : new JsonArray();
        if (where.Count > 10) throw Invalid("where", "At most ten catalogue filters are allowed.");
        var filters = new List<(string Field, JsonNode Value)>();
        foreach (var entry in where)
        {
            var filter = entry as JsonObject ?? throw Invalid("where"); Only(filter, "field", "op", "value");
            var field = Text(filter, "field"); if (Text(filter, "op") != "eq") throw Invalid("op");
            var allowed = collection switch { "users" => new[] { "userName", "kind", "active" }, "roles" => ["name"], _ => ["revoked"] };
            if (!allowed.Contains(field, StringComparer.Ordinal)) throw Invalid("field");
            JsonNode value;
            if (field is "active" or "revoked") value = JsonValue.Create(Boolean(filter, "value"))!;
            else
            {
                var text = Text(filter, "value");
                if (field == "kind" ? text is not ("human" or "service") : !NamePattern.IsMatch(text)) throw Invalid("value");
                value = JsonValue.Create(text)!;
            }
            filters.Add((field, value));
        }
        var normalizedFilters = new JsonArray(filters.OrderBy(f => f.Field, StringComparer.Ordinal).ThenBy(f => f.Value.ToJsonString(), StringComparer.Ordinal)
            .Select(f => (JsonNode)new JsonObject { ["field"] = f.Field, ["value"] = f.Value.DeepClone() }).ToArray());
        var binding = new JsonObject { ["collection"] = collection, ["caller"] = context.PrincipalId, ["principalId"] = principalId, ["where"] = normalizedFilters }.ToJsonString();
        var bindingHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));
        var signingKey = await CursorKeyAsync(uow, ct);
        (string Sort, string Id)? after = null;
        if (query["cursor"] is { } cursorNode)
        {
            if (cursorNode is not JsonValue value || !value.TryGetValue<string>(out var cursor)) throw InvalidCursor();
            after = DecodeCursor(cursor, bindingHash, signingKey);
        }
        var items = new List<JsonObject>();
        foreach (var document in await uow.Documents.ListAsync(collection, ct: ct))
        {
            if (principalId is not null && Text(document, "principalId") != principalId) continue;
            if (filters.Any(f => !JsonNode.DeepEquals(f.Field == "revoked" ? JsonValue.Create(document["revokedAt"] is not null) : document[f.Field], f.Value))) continue;
            items.Add(collection switch { "users" => Summary(User(document)), "roles" => Summary(Role(document)), _ => TokenMetadata(document) });
        }
        (string Sort, string Id) Key(JsonObject doc) => (Text(doc, collection switch { "users" => "userName", "roles" => "name", _ => "createdAt" }), Text(doc, "id"));
        int Compare((string Sort, string Id) a, (string Sort, string Id) b)
        {
            var comparison = StringComparer.Ordinal.Compare(a.Sort, b.Sort);
            if (collection == "tokens") comparison = -comparison;
            return comparison == 0 ? StringComparer.Ordinal.Compare(a.Id, b.Id) : comparison;
        }
        items.Sort((a, b) => Compare(Key(a), Key(b)));
        var eligible = items.Where(item => after is null || Compare(Key(item), after.Value) > 0).Take(limit + 1).ToList();
        var page = eligible.Take(limit).ToArray();
        var next = eligible.Count > limit ? EncodeCursor(bindingHash, Key(page[^1]), signingKey) : null;
        return new JsonObject { ["items"] = new JsonArray(page.Select(p => (JsonNode)p).ToArray()), ["nextCursor"] = next };
    }
    private static async Task EnsureCursorKeyAsync(IUnitOfWork uow, CancellationToken ct)
    {
        // Workspace metadata is private storage, never an identity catalogue. Initialize
        // only in bootstrap's supplied write UoW, preserving rollback and restore semantics.
        if (await uow.Documents.GetAsync("runtimeMetadata", "cursorKey", ct) is null)
            await uow.Documents.PutAsync("runtimeMetadata", "cursorKey", new JsonObject { ["id"] = "cursorKey", ["value"] = Base64(RandomNumberGenerator.GetBytes(32)) }, ct);
    }
    private async Task<byte[]> CursorKeyAsync(IUnitOfWork uow, CancellationToken ct)
    {
        if (Volatile.Read(ref cursorKey) is { } cached) return cached;
        var document = await uow.Documents.GetAsync("runtimeMetadata", "cursorKey", ct)
            ?? throw new BosException("INVALID_STATE", "Workspace identity metadata has not been initialized.");
        byte[] material;
        try { material = Unbase64(Text(document, "value")); }
        catch (FormatException) { throw new BosException("INVALID_STATE", "Workspace identity metadata is invalid."); }
        if (material.Length != 32) throw new BosException("INVALID_STATE", "Workspace identity metadata is invalid.");
        var derived = HMACSHA256.HashData(material, "Bos.Identity.CatalogueCursor.v1"u8);
        CryptographicOperations.ZeroMemory(material);
        // A supplied writer may still roll back bootstrap's initialization. Cache only
        // material observed in a committed read snapshot, never uncommitted writer state.
        if (!uow.IsWrite) Interlocked.CompareExchange(ref cursorKey, derived, null);
        return derived;
    }
    private static string EncodeCursor(string binding, (string Sort, string Id) key, byte[] signingKey)
    {
        var payload = Encoding.UTF8.GetBytes(new JsonObject { ["binding"] = binding, ["sort"] = key.Sort, ["id"] = key.Id }.ToJsonString());
        return Base64(payload) + "." + Base64(HMACSHA256.HashData(signingKey, payload));
    }
    private static (string Sort, string Id) DecodeCursor(string cursor, string binding, byte[] signingKey)
    {
        try
        {
            if (cursor.Length > 4096) throw InvalidCursor();
            var parts = cursor.Split('.'); if (parts.Length != 2) throw InvalidCursor();
            var payload = Unbase64(parts[0]); var signature = Unbase64(parts[1]);
            if (Base64(payload) != parts[0] || Base64(signature) != parts[1]
                || !CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(signingKey, payload))) throw InvalidCursor();
            var data = JsonNode.Parse(payload) as JsonObject ?? throw InvalidCursor();
            Only(data, "binding", "sort", "id");
            if (Text(data, "binding") != binding) throw InvalidCursor();
            return (Text(data, "sort"), Text(data, "id"));
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or InvalidOperationException or BosException) { throw InvalidCursor(); }
    }
    private static BosException InvalidCursor() => new("INVALID_REQUEST", "Invalid catalogue cursor.", 400, "cursor");
}
