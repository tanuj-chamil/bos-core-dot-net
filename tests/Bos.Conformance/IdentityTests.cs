using System;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Bos.Contracts;
using Bos.Identity;
using Bos.Sqlite;
using Xunit;

namespace Bos.Conformance;

public sealed class IdentityTests
{
    [Fact]
    public async Task BootstrapEnlistsAndCanResetAnActiveHumanAdministrator()
    {
        await using var f = await Fixture.CreateAsync(bootstrap: false);
        string id;
        await using (var uow = await f.Factory.BeginWriteAsync())
        {
            var user = await f.Identity.BootstrapAsync(uow, Fixture.Password);
            id = S(user, "id");
            Assert.Equal("Admin", S(user, "userName"));
            Assert.True(user["active"]!.GetValue<bool>());
            Assert.Equal(1, N(user, "revision"));
            var stored = (await uow.Documents.GetAsync("users", id))!;
            Assert.DoesNotContain(Fixture.Password, stored.ToJsonString());
            Assert.True(stored["bootstrapSecret"]!.GetValue<bool>());
            Assert.False(stored["bootstrapRedeemed"]!.GetValue<bool>());
            Assert.NotNull(stored["passwordHash"]);
            Assert.NotNull(await uow.Documents.GetAsync("runtimeMetadata", "cursorKey"));
            await uow.RollbackAsync();
        }
        await using (var read = await f.Factory.BeginReadAsync())
        {
            Assert.Null(await read.Documents.GetAsync("users", id));
            Assert.Null(await read.Documents.GetAsync("runtimeMetadata", "cursorKey"));
        }
        f.Admin = await f.BootstrapAsync(Fixture.Password);
        var login = await f.Identity.LoginAsync("aDmIn", Fixture.Password);
        Assert.Equal(f.Admin.PrincipalId, login.Caller.PrincipalId);
        Assert.NotNull(login.Caller.SecurityStamp);
        var changed = await f.BootstrapAsync("Replacement-Password-2026");
        Assert.NotEqual(login.Caller.SecurityStamp, changed.SecurityStamp);
        await f.ErrorAsync("UNAUTHENTICATED", () => f.ReloadAsync(login.Caller));
        await f.ErrorAsync("UNAUTHENTICATED", async () => { await f.Identity.LoginAsync("Admin", Fixture.Password); });
        Assert.Equal(changed.PrincipalId, (await f.Identity.LoginAsync("Admin", "Replacement-Password-2026")).Caller.PrincipalId);
        await f.ErrorAsync("UNAUTHENTICATED", async () => { await f.Identity.LoginAsync("Admin", "Replacement-Password-2026"); });
    }

    [Fact]
    public async Task BootstrapSecretAdmitsOneSessionWhichCanReplaceItWithAReusablePassword()
    {
        await using var f = await Fixture.CreateAsync();
        var bad = await Assert.ThrowsAsync<BosException>(() => f.Identity.LoginAsync("Admin", "incorrect password"));
        await using (var read = await f.Factory.BeginReadAsync())
            Assert.False((await read.Documents.GetAsync("users", f.Admin.PrincipalId))!["bootstrapRedeemed"]!.GetValue<bool>());
        var first = await f.Identity.LoginAsync("Admin", Fixture.Password);
        Assert.DoesNotContain("bootstrap", first.User.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        await using (var read = await f.Factory.BeginReadAsync())
        {
            var stored = (await read.Documents.GetAsync("users", first.Caller.PrincipalId))!;
            Assert.True(stored["bootstrapSecret"]!.GetValue<bool>());
            Assert.True(stored["bootstrapRedeemed"]!.GetValue<bool>());
            Assert.Equal(1, N(stored, "revision")); // Redemption is internal credential state, not a profile edit.
        }
        var restarted = new IdentityService(f.Factory, f.Clock, new UuidGenerator());
        var redeemed = await Assert.ThrowsAsync<BosException>(() => restarted.LoginAsync("Admin", Fixture.Password));
        Assert.Equal((bad.Code, bad.Status, bad.Message), (redeemed.Code, redeemed.Status, redeemed.Message));
        Assert.True(f.Authorization.Can(await f.ReloadAsync(first.Caller), "identity.manage", "*"));
        await restarted.ChangePasswordAsync(first.Caller, Fixture.Password, "Permanent-Password-2026");
        await f.ErrorAsync("UNAUTHENTICATED", () => f.ReloadAsync(first.Caller));
        await using (var read = await f.Factory.BeginReadAsync())
        {
            var stored = (await read.Documents.GetAsync("users", first.Caller.PrincipalId))!;
            Assert.False(stored["bootstrapSecret"]!.GetValue<bool>());
            Assert.False(stored["bootstrapRedeemed"]!.GetValue<bool>());
        }
        await f.ErrorAsync("UNAUTHENTICATED", async () => { await restarted.LoginAsync("Admin", Fixture.Password); });
        var normal = await restarted.LoginAsync("Admin", "Permanent-Password-2026");
        Assert.Equal(normal.Caller, (await restarted.LoginAsync("Admin", "Permanent-Password-2026")).Caller);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdministrativePasswordReplacementClearsBootstrapStateAndPreservesRawSecret(bool redeem)
    {
        await using var f = await Fixture.CreateAsync();
        if (redeem) await f.Identity.LoginAsync("Admin", Fixture.Password);
        const string decomposed = "Admin-e\u0301-new-password-2026";
        await f.CallAsync("users.setPassword", new() { ["id"] = f.Admin.PrincipalId, ["expectedRevision"] = 1, ["newPassword"] = decomposed });
        await f.ErrorAsync("UNAUTHENTICATED", () => f.ReloadAsync(f.Admin));
        await using (var read = await f.Factory.BeginReadAsync())
        {
            var stored = (await read.Documents.GetAsync("users", f.Admin.PrincipalId))!;
            Assert.False(stored["bootstrapSecret"]!.GetValue<bool>()); Assert.False(stored["bootstrapRedeemed"]!.GetValue<bool>());
        }
        await f.ErrorAsync("UNAUTHENTICATED", async () => { await f.Identity.LoginAsync("Admin", decomposed.Normalize()); });
        var login = await f.Identity.LoginAsync("Admin", decomposed);
        Assert.Equal(login.Caller, (await f.Identity.LoginAsync("Admin", decomposed)).Caller);
    }

    [Fact]
    public async Task ConcurrentBootstrapLoginAttemptsRedeemExactlyOnce()
    {
        await using var f = await Fixture.CreateAsync();
        async Task<Caller?> AttemptAsync()
        {
            try { return (await f.Identity.LoginAsync("Admin", Fixture.Password)).Caller; }
            catch (BosException e) when (e.Code == "UNAUTHENTICATED") { return null; }
        }
        var attempts = await Task.WhenAll(Task.Run(AttemptAsync), Task.Run(AttemptAsync));
        Assert.Single(attempts, caller => caller is not null);
    }

    [Fact]
    public async Task FailedLoginCommitDoesNotConsumeBootstrapSecret()
    {
        await using var f = await Fixture.CreateAsync();
        var failing = new IdentityService(new FailCommitFactory(f.Factory), f.Clock, new UuidGenerator());
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.LoginAsync("Admin", Fixture.Password));
        await using (var read = await f.Factory.BeginReadAsync())
            Assert.False((await read.Documents.GetAsync("users", f.Admin.PrincipalId))!["bootstrapRedeemed"]!.GetValue<bool>());
        Assert.Equal(f.Admin.PrincipalId, (await f.Identity.LoginAsync("Admin", Fixture.Password)).Caller.PrincipalId);
        await f.ErrorAsync("UNAUTHENTICATED", async () => { await f.Identity.LoginAsync("Admin", Fixture.Password); });
    }

    [Fact]
    public async Task LoginUsesFiveAttemptsAndFifteenMinuteIdentityLockoutWithSafeErrors()
    {
        await using var f = await Fixture.CreateAsync();
        f.Clock.Now = f.Clock.Now.AddTicks(4321); // Internal lockout must not expire before the exact boundary.
        var unknown = await Assert.ThrowsAsync<BosException>(() => f.Identity.LoginAsync("Unknown", "bad password"));
        for (var i = 0; i < 5; i++)
        {
            var bad = await Assert.ThrowsAsync<BosException>(() => f.Identity.LoginAsync("Admin", "bad password"));
            Assert.Equal(unknown.Code, bad.Code); Assert.Equal(unknown.Message, bad.Message); Assert.Equal(401, bad.Status);
        }
        await f.ErrorAsync("UNAUTHENTICATED", async () => { await f.Identity.LoginAsync("Admin", Fixture.Password); });
        f.Clock.Now = f.Clock.Now.AddMinutes(15).AddTicks(-1);
        await f.ErrorAsync("UNAUTHENTICATED", async () => { await f.Identity.LoginAsync("Admin", Fixture.Password); });
        f.Clock.Now = f.Clock.Now.AddTicks(1);
        Assert.Equal(f.Admin.PrincipalId, (await f.Identity.LoginAsync("Admin", Fixture.Password)).Caller.PrincipalId);
        await using var read = await f.Factory.BeginReadAsync();
        Assert.Equal(0, N((await read.Documents.GetAsync("users", f.Admin.PrincipalId))!, "accessFailedCount"));
    }

    [Fact]
    public async Task HumansRequireExplicitPasswordThenActivationAndPublicOutputsStaySafe()
    {
        await using var f = await Fixture.CreateAsync();
        var user = await f.UserAsync("Alice", "human"); var id = S(user, "id");
        Assert.False(user["active"]!.GetValue<bool>()); Assert.Empty(user["roles"]!.AsArray());
        await f.ErrorAsync("INVALID_STATE", () => f.CallAsync("users.update", new() { ["id"] = id, ["expectedRevision"] = 1, ["changes"] = new JsonObject { ["active"] = true } }));
        var receipt = await f.CallAsync("users.setPassword", new() { ["id"] = id, ["expectedRevision"] = 1, ["newPassword"] = "Alice-Password-2026" });
        Assert.Equal(3, receipt.Count); Assert.True(receipt["passwordChanged"]!.GetValue<bool>()); Assert.Equal(2, N(receipt, "revision"));
        user = await f.CallAsync("users.get", new() { ["id"] = id }); Assert.False(user["active"]!.GetValue<bool>());
        await f.ErrorAsync("UNAUTHENTICATED", async () => { await f.Identity.LoginAsync("Alice", "Alice-Password-2026"); });
        user = await f.CallAsync("users.update", new() { ["id"] = id, ["expectedRevision"] = 2, ["changes"] = new JsonObject { ["active"] = true } });
        var login = await f.Identity.LoginAsync("Alice", "Alice-Password-2026");
        Assert.Equal(id, login.Caller.PrincipalId); Assert.Equal(user.ToJsonString(), login.User.ToJsonString());
        Assert.DoesNotContain("password", login.User.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        await f.ErrorAsync("UNIQUE_CONFLICT", () => f.UserAsync("aLiCe", "service"));
        await f.ErrorAsync("REVISION_CONFLICT", () => f.CallAsync("users.disable", new() { ["id"] = id, ["expectedRevision"] = 2 }));
        await f.ErrorAsync("NO_CHANGE", () => f.CallAsync("users.update", new() { ["id"] = id, ["expectedRevision"] = 3, ["changes"] = new JsonObject() }));
    }

    [Fact]
    public async Task LastAdministratorIsProtectedThroughAllThreeDemotionPaths()
    {
        await using var f = await Fixture.CreateAsync();
        var user = await f.CallAsync("users.get", new() { ["id"] = f.Admin.PrincipalId }); var roleId = user["roles"]![0]!.GetValue<string>();
        await f.ErrorAsync("POLICY_VIOLATION", () => f.CallAsync("users.disable", new() { ["id"] = f.Admin.PrincipalId, ["expectedRevision"] = 1 }));
        await f.ErrorAsync("POLICY_VIOLATION", () => f.CallAsync("users.update", new() { ["id"] = f.Admin.PrincipalId, ["expectedRevision"] = 1, ["changes"] = new JsonObject { ["active"] = false } }));
        await f.ErrorAsync("POLICY_VIOLATION", () => f.CallAsync("roles.remove", new() { ["userId"] = f.Admin.PrincipalId, ["roleId"] = roleId, ["expectedUserRevision"] = 1 }));
        await f.ErrorAsync("POLICY_VIOLATION", () => f.CallAsync("roles.update", new() { ["id"] = roleId, ["expectedRevision"] = 1, ["changes"] = new JsonObject { ["grants"] = new JsonArray() } }));
        Assert.True((await f.CallAsync("users.get", new() { ["id"] = f.Admin.PrincipalId }))["active"]!.GetValue<bool>());
        Assert.Equal(1, N(await f.CallAsync("roles.get", new() { ["id"] = roleId }), "revision"));
        // A role name carries no administrator semantics; a different role with the privilege suffices.
        var other = await f.UserAsync("Recovery", "service"); var id = S(other, "id");
        other = await f.CallAsync("users.update", new() { ["id"] = id, ["expectedRevision"] = 1, ["changes"] = new JsonObject { ["active"] = true } });
        var alternative = await f.RoleAsync("DifferentName", G("identity.manage", "*"));
        await f.CallAsync("roles.assign", new() { ["userId"] = id, ["roleId"] = S(alternative, "id"), ["expectedUserRevision"] = 2 });
        await f.CallAsync("roles.remove", new() { ["userId"] = f.Admin.PrincipalId, ["roleId"] = roleId, ["expectedUserRevision"] = 1 });
        Assert.False(f.Authorization.Can(await f.ReloadAsync(f.Admin), "identity.manage", "*"));
    }

    [Fact]
    public async Task NestedCeilingsAndReloadSnapshotsOnlyNarrowAuthority()
    {
        await using var f = await Fixture.CreateAsync();
        var context = await f.ReloadAsync(f.Admin);
        context.Ceilings.Add([new("record.read", "MaterialRequest"), new("record.update", "MaterialRequest")]);
        context.Ceilings.Add([new("record.read", "*")]);
        Assert.True(f.Authorization.Can(context, "record.read", "MaterialRequest"));
        Assert.False(f.Authorization.Can(context, "record.read", "Other"));
        Assert.False(f.Authorization.Can(context, "record.update", "MaterialRequest"));
        await using var read = await f.Factory.BeginReadAsync();
        var current = (JsonObject)(await f.Identity.InvokeAsync("identity.getCurrent", new(), context, read))!;
        Assert.Single(current["effectiveGrants"]!.AsArray());
        Assert.Equal("MaterialRequest", S(current["effectiveGrants"]![0]!, "resource"));
        await f.Authorization.ReloadAsync(read, context);
        Assert.False(f.Authorization.Can(context, "identity.manage", "*"));
        var empty = await f.ReloadAsync(f.Admin);
        empty.Ceilings.Add([]); await f.Authorization.ReloadAsync(read, empty);
        Assert.False(f.Authorization.Can(empty, "record.read", "MaterialRequest"));
        await f.ErrorAsync("UNAUTHENTICATED", () => f.ReloadAsync(new Caller(f.Admin.PrincipalId)));
        await f.ErrorAsync("UNAUTHENTICATED", () => f.ReloadAsync(f.Admin with { SecurityStamp = "forged" }));
    }

    [Fact]
    public async Task TokensAreCryptographicOneTimeSecretsWithCurrentStateAndScopeChecks()
    {
        await using var f = await Fixture.CreateAsync();
        var (user, role) = await f.ServiceAsync("Robot", G("record.read", "*")); var id = S(user, "id");
        var issue = await f.CallAsync("tokens.create", new() { ["principalId"] = id, ["scopes"] = G("record.read", "MaterialRequest") });
        var token = S(issue, "token"); var metadata = issue["metadata"]!.AsObject();
        Assert.Matches(@"\Abos_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.[A-Za-z0-9_-]{43}\z", token);
        Assert.Equal(f.Clock.Now.AddDays(30), DateTimeOffset.Parse(S(metadata, "expiresAt")));
        var caller = await f.Identity.AuthenticateTokenAsync(token); Assert.NotNull(caller);
        var context = await f.ReloadAsync(caller!);
        Assert.True(f.Authorization.Can(context, "record.read", "MaterialRequest")); Assert.False(f.Authorization.Can(context, "record.read", "Other"));
        await using (var read = await f.Factory.BeginReadAsync())
        {
            var stored = (await read.Documents.GetAsync("tokens", S(metadata, "id")))!;
            var secretText = token.Split('.')[1]; var secret = Convert.FromBase64String(secretText.Replace('-', '+').Replace('_', '/') + "=");
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(secret)), S(stored, "secretHash"));
            Assert.DoesNotContain(token, stored.ToJsonString()); Assert.DoesNotContain(secretText, stored.ToJsonString());
        }
        var list = await f.CallAsync("tokens.list", new() { ["principalId"] = id });
        Assert.DoesNotContain("secretHash", list.ToJsonString()); Assert.DoesNotContain(token, list.ToJsonString());
        Assert.Null(await f.Identity.AuthenticateTokenAsync(token[..^1] + (token[^1] == 'A' ? "B" : "A")));
        await f.CallAsync("roles.update", new() { ["id"] = S(role, "id"), ["expectedRevision"] = 1, ["changes"] = new JsonObject { ["grants"] = G("record.read", "Other") } });
        Assert.False(f.Authorization.Can(await f.ReloadAsync(caller!), "record.read", "MaterialRequest"));
        var revoked = await f.CallAsync("tokens.revoke", new() { ["id"] = S(metadata, "id") });
        Assert.NotNull(revoked["revokedAt"]);
        Assert.Equal(revoked.ToJsonString(), (await f.CallAsync("tokens.revoke", new() { ["id"] = S(metadata, "id") })).ToJsonString());
        Assert.Null(await f.Identity.AuthenticateTokenAsync(token));
        await f.ErrorAsync("UNAUTHENTICATED", () => f.ReloadAsync(caller!));
    }

    [Fact]
    public async Task TokenScopesMustFitTargetAndEveryIssuerCeilingAndExpiryIsBounded()
    {
        await using var f = await Fixture.CreateAsync();
        var (target, _) = await f.ServiceAsync("Target", G("record.read", "MaterialRequest")); var id = S(target, "id");
        await f.ErrorAsync("FORBIDDEN", () => f.CallAsync("tokens.create", new() { ["principalId"] = id, ["scopes"] = G("record.read", "*") }));
        var (issuer, _) = await f.ServiceAsync("Issuer", new JsonArray(G("identity.manage", "*")[0]!.DeepClone()));
        var issuerToken = await f.CallAsync("tokens.create", new() { ["principalId"] = S(issuer, "id"), ["scopes"] = G("identity.manage", "*") });
        var issuerCaller = (await f.Identity.AuthenticateTokenAsync(S(issuerToken, "token")))!;
        await f.ErrorAsync("FORBIDDEN", () => f.CallAsync("tokens.create", new() { ["principalId"] = id, ["scopes"] = G("record.read", "MaterialRequest") }, issuerCaller));
        await f.ErrorAsync("INVALID_REQUEST", () => f.CallAsync("tokens.create", new() { ["principalId"] = id, ["scopes"] = new JsonArray() }));
        await f.ErrorAsync("INVALID_REQUEST", () => f.CallAsync("tokens.create", new() { ["principalId"] = id, ["scopes"] = G("record.read", "MaterialRequest"), ["expiresAt"] = f.Clock.Now.AddDays(366).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) }));
        await f.ErrorAsync("INVALID_STATE", () => f.CallAsync("tokens.create", new() { ["principalId"] = f.Admin.PrincipalId, ["scopes"] = G("record.read", "*") }));
        var expiring = await f.CallAsync("tokens.create", new() { ["principalId"] = id, ["scopes"] = G("record.read", "MaterialRequest"), ["expiresAt"] = f.Clock.Now.AddSeconds(1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) });
        var token = S(expiring, "token"); Assert.NotNull(await f.Identity.AuthenticateTokenAsync(token));
        f.Clock.Now = f.Clock.Now.AddSeconds(1); Assert.Null(await f.Identity.AuthenticateTokenAsync(token));
    }

    [Theory]
    [InlineData("2026-11-04T12:00:00.0001Z")]
    [InlineData("2026-11-04T12:00:00.0000000Z")]
    [InlineData("2026-11-04T17:30:00.1234+05:30")]
    [InlineData("2026-11-04T12:00:00")]
    [InlineData("2026-11-31T12:00:00Z")]
    public async Task TokenExpiryUsesSharedInstantValidationWithoutRoundingInvalidInputs(string expiry)
    {
        await using var f = await Fixture.CreateAsync();
        var (user, _) = await f.ServiceAsync("ExpiryValidation", G("record.read", "*"));
        var error = await Assert.ThrowsAsync<BosException>(() => f.CallAsync("tokens.create", new() { ["principalId"] = S(user, "id"), ["scopes"] = G("record.read", "*"), ["expiresAt"] = expiry }));
        Assert.Equal("CONVERSION_FAILED", error.Code);
        Assert.Contains(error.Details, detail => detail.Path == "expiresAt");
        Assert.Empty((await f.CallAsync("tokens.list", new() { ["principalId"] = S(user, "id") }))["items"]!.AsArray());
    }

    [Fact]
    public async Task TokenExpiryNormalizesRfc3339OffsetsAndReadsLegacyStoredPrecision()
    {
        await using var f = await Fixture.CreateAsync();
        var (user, _) = await f.ServiceAsync("ExpiryNormalization", G("record.read", "*"));
        var issued = await f.CallAsync("tokens.create", new() { ["principalId"] = S(user, "id"), ["scopes"] = G("record.read", "*"), ["expiresAt"] = "2026-11-04t17:30:00.12+05:30" });
        Assert.Equal("2026-11-04T12:00:00.120Z", S(issued["metadata"]!, "expiresAt"));
        await using (var write = await f.Factory.BeginWriteAsync())
        {
            var token = (await write.Documents.GetAsync("tokens", S(issued["metadata"]!, "id")))!;
            token["createdAt"] = f.Clock.Now.AddTicks(4321).ToString("O", CultureInfo.InvariantCulture);
            token["expiresAt"] = f.Clock.Now.AddDays(1).AddTicks(4321).ToString("O", CultureInfo.InvariantCulture);
            await write.Documents.PutAsync("tokens", S(token, "id"), token); await write.CommitAsync();
        }
        Assert.NotNull(await f.Identity.AuthenticateTokenAsync(S(issued, "token")));
        var listed = Assert.Single((await f.CallAsync("tokens.list", new() { ["principalId"] = S(user, "id") }))["items"]!.AsArray())!;
        Assert.Equal("2026-11-03T12:00:00.000Z", S(listed, "createdAt"));
        Assert.Equal("2026-11-04T12:00:00.000Z", S(listed, "expiresAt"));
    }

    [Fact]
    public async Task DisableRevokesTokensAndStampAndAllMutationsRollBackTogether()
    {
        await using var f = await Fixture.CreateAsync();
        var (user, _) = await f.ServiceAsync("Worker", G("record.read", "*")); var id = S(user, "id");
        var issue = await f.CallAsync("tokens.create", new() { ["principalId"] = id, ["scopes"] = G("record.read", "*") }); var token = S(issue, "token");
        var caller = (await f.Identity.AuthenticateTokenAsync(token))!;
        var context = await f.ReloadAsync(f.Admin);
        await using (var write = await f.Factory.BeginWriteAsync())
        {
            await f.Identity.InvokeAsync("users.disable", new() { ["id"] = id, ["expectedRevision"] = N(user, "revision") }, context, write);
            Assert.NotNull((await write.Documents.GetAsync("tokens", S(issue["metadata"]!, "id")))!["revokedAt"]);
            await write.RollbackAsync();
        }
        Assert.NotNull(await f.Identity.AuthenticateTokenAsync(token));
        await f.CallAsync("users.disable", new() { ["id"] = id, ["expectedRevision"] = N(user, "revision") });
        Assert.Null(await f.Identity.AuthenticateTokenAsync(token));
        await f.ErrorAsync("UNAUTHENTICATED", () => f.ReloadAsync(caller));
        await using var read = await f.Factory.BeginReadAsync();
        Assert.NotEqual(caller.SecurityStamp, S((await read.Documents.GetAsync("users", id))!, "securityStamp"));
    }

    [Fact]
    public async Task PasswordChangesNeedCurrentPasswordInvalidateSessionsAndNeverNormalizeSecret()
    {
        await using var f = await Fixture.CreateAsync();
        const string decomposed = "Long-e\u0301-password-2026";
        await f.ErrorAsync("UNAUTHENTICATED", () => f.Identity.ChangePasswordAsync(f.Admin, "wrong password", decomposed));
        await f.Identity.ChangePasswordAsync(f.Admin, Fixture.Password, decomposed);
        await f.ErrorAsync("UNAUTHENTICATED", () => f.ReloadAsync(f.Admin));
        var login = await f.Identity.LoginAsync("Admin", decomposed);
        await f.ErrorAsync("UNAUTHENTICATED", async () => { await f.Identity.LoginAsync("Admin", decomposed.Normalize()); });
        Assert.Equal(2, N(login.User, "revision"));
        await using var read = await f.Factory.BeginReadAsync();
        foreach (var document in await read.Documents.ListAsync("users")) Assert.DoesNotContain(decomposed, document.ToJsonString());
    }

    [Fact]
    public async Task PasswordRecoveryAndTokenIssuanceNeverCommitTheProvidedTransaction()
    {
        await using var f = await Fixture.CreateAsync();
        var (service, _) = await f.ServiceAsync("Transactional", G("record.read", "*"));
        string token;
        await using (var write = await f.Factory.BeginWriteAsync())
        {
            var context = new InvocationContext { Caller = f.Admin }; await f.Authorization.ReloadAsync(write, context);
            var passwordReceipt = (JsonObject)(await f.Identity.InvokeAsync("users.setPassword", new() { ["id"] = f.Admin.PrincipalId, ["expectedRevision"] = 1, ["newPassword"] = "Never-Committed-Password" }, context, write))!;
            Assert.Equal(2, N(passwordReceipt, "revision"));
            Assert.NotEqual(f.Admin.SecurityStamp, S((await write.Documents.GetAsync("users", f.Admin.PrincipalId))!, "securityStamp"));
            var issue = (JsonObject)(await f.Identity.InvokeAsync("tokens.create", new() { ["principalId"] = S(service, "id"), ["scopes"] = G("record.read", "*") }, context, write))!;
            token = S(issue, "token");
            await write.RollbackAsync();
        }
        Assert.Null(await f.Identity.AuthenticateTokenAsync(token));
        Assert.Equal(f.Admin.SecurityStamp, (await f.Identity.LoginAsync("Admin", Fixture.Password)).Caller.SecurityStamp);
        await f.ErrorAsync("INVALID_REQUEST", () => f.CallAsync("users.disable", new() { ["id"] = S(service, "id"), ["expectedRevision"] = "3" }));
        await f.ErrorAsync("INVALID_REQUEST", () => f.CallAsync("users.disable", new() { ["id"] = S(service, "id"), ["expectedRevision"] = 0 }));
    }

    [Fact]
    public async Task CatalogueCursorsAreAuthenticatedQueryBoundKeysetsAndOutputsAreScrubbed()
    {
        await using var f = await Fixture.CreateAsync();
        await f.UserAsync("Bravo", "service"); await f.UserAsync("Charlie", "service");
        var page = await f.CallAsync("users.list", new() { ["query"] = new JsonObject { ["limit"] = 1 } });
        Assert.Single(page["items"]!.AsArray()); Assert.Equal("Admin", S(page["items"]![0]!, "userName"));
        var cursor = S(page, "nextCursor");
        // A new item before the last key cannot shift the continuation like an offset.
        await f.UserAsync("Aaron", "service");
        page = await f.CallAsync("users.list", new() { ["query"] = new JsonObject { ["limit"] = 1, ["cursor"] = cursor } });
        Assert.Equal("Bravo", S(page["items"]![0]!, "userName")); Assert.DoesNotContain("passwordHash", page.ToJsonString()); Assert.DoesNotContain("securityStamp", page.ToJsonString());
        await f.ErrorAsync("INVALID_REQUEST", () => f.CallAsync("roles.list", new() { ["query"] = new JsonObject { ["cursor"] = cursor } }));
        await f.ErrorAsync("INVALID_REQUEST", () => f.CallAsync("users.list", new() { ["query"] = new JsonObject { ["cursor"] = cursor + "x" } }));
        foreach (var invalid in new JsonNode[] { JsonValue.Create(cursor + "=")!, JsonValue.Create(7)!, new JsonArray() })
        {
            var error = await Assert.ThrowsAsync<BosException>(() => f.CallAsync("users.list", new() { ["query"] = new JsonObject { ["cursor"] = invalid } }));
            Assert.Equal("INVALID_REQUEST", error.Code); Assert.Equal(400, error.Status); Assert.Equal("cursor", error.Path);
        }
        await f.ErrorAsync("INVALID_REQUEST", () => f.CallAsync("users.list", new() { ["query"] = new JsonObject { ["limit"] = 201 } }));
        await f.ErrorAsync("INVALID_REQUEST", () => f.CallAsync("users.list", new() { ["query"] = null }));
    }

    [Fact]
    public async Task CatalogueCursorKeyIsPersistedAndReadOnlyContinuationSurvivesServiceRecreation()
    {
        await using var f = await Fixture.CreateAsync(bootstrap: false);
        // Reading our own staged initialization must not cache a key whose transaction
        // can still roll back. A later bootstrap establishes the committed workspace key.
        await using (var write = await f.Factory.BeginWriteAsync())
        {
            var staged = await f.Identity.BootstrapAsync(write, Fixture.Password);
            var stored = (await write.Documents.GetAsync("users", S(staged, "id")))!;
            var context = new InvocationContext { Caller = new Caller(S(staged, "id"), SecurityStamp: S(stored, "securityStamp")) };
            await f.Authorization.ReloadAsync(write, context);
            await f.Identity.InvokeAsync("users.list", new(), context, write);
            await write.RollbackAsync();
        }
        f.Admin = await f.BootstrapAsync(Fixture.Password);
        await f.UserAsync("Bravo", "service"); await f.UserAsync("Charlie", "service");
        async Task<JsonObject> ReadPageAsync(IdentityService identity, IUnitOfWorkFactory factory, string? cursor = null)
        {
            await using var read = await factory.BeginReadAsync();
            var context = new InvocationContext { Caller = f.Admin }; await f.Authorization.ReloadAsync(read, context);
            var query = new JsonObject { ["limit"] = 1 }; if (cursor is not null) query["cursor"] = cursor;
            return (JsonObject)(await identity.InvokeAsync("users.list", new() { ["query"] = query }, context, read))!;
        }
        var first = await ReadPageAsync(f.Identity, f.Factory); var cursor = S(first, "nextCursor");
        string key;
        await using (var read = await f.Factory.BeginReadAsync()) key = (await read.Documents.GetAsync("runtimeMetadata", "cursorKey"))!.ToJsonString();
        // Recreate both the adapter and service, retaining only the committed workspace database.
        var factory = new SqliteUnitOfWorkFactory(f.Factory.DatabasePath);
        await factory.InitializeAsync();
        var restarted = new IdentityService(factory, f.Clock, new UuidGenerator());
        var second = await ReadPageAsync(restarted, factory, cursor);
        Assert.Equal("Bravo", S(second["items"]![0]!, "userName"));
        Assert.DoesNotContain("cursorKey", second.ToJsonString());
        await using (var write = await factory.BeginWriteAsync())
        {
            await restarted.BootstrapAsync(write, "Reset-Password-2026");
            Assert.Equal(key, (await write.Documents.GetAsync("runtimeMetadata", "cursorKey"))!.ToJsonString());
            await write.RollbackAsync();
        }
        Assert.Equal("Charlie", S((await ReadPageAsync(restarted, factory, S(second, "nextCursor")))["items"]![0]!, "userName"));
    }

    [Theory]
    [InlineData("users.create", "{\"user\":{\"userName\":\"X\",\"displayName\":\"X\",\"kind\":\"service\",\"password\":\"hidden\"}}", "UNKNOWN_FIELD")]
    [InlineData("users.create", "{\"user\":{\"userName\":\"X\\n\",\"displayName\":\"X\",\"kind\":\"service\"}}", "INVALID_REQUEST")]
    [InlineData("users.list", "{\"query\":{\"where\":[{\"field\":\"active\",\"op\":\"eq\",\"value\":\"true\"}]}}", "INVALID_REQUEST")]
    [InlineData("roles.create", "{\"role\":{\"name\":\"Invalid\",\"grants\":[{\"action\":\"unknown\",\"resource\":\"*\"}]}}", "INVALID_REQUEST")]
    [InlineData("roles.create", "{\"role\":{\"name\":\"Invalid\",\"grants\":[{\"action\":\"identity.manage\",\"resource\":\"X\"}]}}", "INVALID_REQUEST")]
    [InlineData("authorization.can", "{\"action\":\"record.read\",\"resource\":\"X\",\"principalId\":\"forged\"}", "UNKNOWN_FIELD")]
    public async Task StrictBindingRejectsUnknownFieldsTypesAndActions(string method, string json, string code)
    {
        await using var f = await Fixture.CreateAsync();
        await f.ErrorAsync(code, () => f.CallAsync(method, JsonNode.Parse(json)!.AsObject()));
    }

    [Fact]
    public async Task RoleGrantsCollapseDuplicatesMembershipHasCasAndNoOpAndAuditIsSecretFree()
    {
        await using var f = await Fixture.CreateAsync();
        var user = await f.UserAsync("Principal", "service");
        var grants = G("record.read", "MaterialRequest"); grants.Add(grants[0]!.DeepClone());
        var role = await f.RoleAsync("Readers", grants); Assert.Single(role["grants"]!.AsArray());
        var roleId = S(role, "id"); var userId = S(user, "id");
        await f.CallAsync("roles.assign", new() { ["userId"] = userId, ["roleId"] = roleId, ["expectedUserRevision"] = 1 });
        await f.ErrorAsync("REVISION_CONFLICT", () => f.CallAsync("roles.remove", new() { ["userId"] = userId, ["roleId"] = roleId, ["expectedUserRevision"] = 1 }));
        await f.ErrorAsync("NO_CHANGE", () => f.CallAsync("roles.assign", new() { ["userId"] = userId, ["roleId"] = roleId, ["expectedUserRevision"] = 2 }));
        await f.ErrorAsync("NO_CHANGE", () => f.CallAsync("roles.update", new() { ["id"] = roleId, ["expectedRevision"] = 1, ["changes"] = new JsonObject { ["grants"] = grants.DeepClone() } }));
        user = await f.CallAsync("roles.remove", new() { ["userId"] = userId, ["roleId"] = roleId, ["expectedUserRevision"] = 2 });
        Assert.Equal(3, N(user, "revision")); Assert.Empty(user["roles"]!.AsArray());
        await using var read = await f.Factory.BeginReadAsync();
        await using var command = read.Connection.CreateCommand(); command.Transaction = read.Transaction;
        command.CommandText = "SELECT json FROM Audit";
        await using var reader = await command.ExecuteReaderAsync();
        var audit = new StringBuilder(); while (await reader.ReadAsync()) audit.Append(reader.GetString(0));
        Assert.Contains("roles.assign", audit.ToString()); Assert.DoesNotContain(Fixture.Password, audit.ToString()); Assert.DoesNotContain("passwordHash", audit.ToString());
        Assert.DoesNotContain("securityStamp", audit.ToString()); Assert.DoesNotContain("secretHash", audit.ToString());
    }

    private static string S(JsonNode node, string key) => node[key]!.GetValue<string>();
    private static int N(JsonNode node, string key) => node[key]!.GetValue<int>();
    private static JsonArray G(string action, string resource) => new(new JsonObject { ["action"] = action, ["resource"] = resource });

    private sealed class TestClock : IClock
    {
        internal DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }
    // Only inject a failure at commit; all documents, Identity stores and rollback use
    // the actual SQLite adapter, so this verifies transaction ownership rather than a mock.
    private sealed class FailCommitFactory(IUnitOfWorkFactory inner) : IUnitOfWorkFactory
    {
        public string DatabasePath => inner.DatabasePath;
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public Task<IUnitOfWork> BeginReadAsync(CancellationToken ct = default) => inner.BeginReadAsync(ct);
        public async Task<IUnitOfWork> BeginWriteAsync(CancellationToken ct = default) => new FailCommit(await inner.BeginWriteAsync(ct));
        private sealed class FailCommit(IUnitOfWork inner) : IUnitOfWork
        {
            public DbConnection Connection => inner.Connection;
            public DbTransaction Transaction => inner.Transaction;
            public bool IsWrite => inner.IsWrite;
            public IDocumentRepository Documents => inner.Documents;
            public IRecordRepository Records => inner.Records;
            public Task CommitAsync(CancellationToken ct = default) => throw new InvalidOperationException("Controlled commit failure.");
            public Task RollbackAsync(CancellationToken ct = default) => inner.RollbackAsync(ct);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Password = "Admin-Password-2026";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "bos-identity-" + Guid.NewGuid().ToString("N"));
        internal SqliteUnitOfWorkFactory Factory { get; private set; } = null!;
        internal TestClock Clock { get; } = new();
        internal Authorization Authorization { get; private set; } = null!;
        internal IdentityService Identity { get; private set; } = null!;
        internal Caller Admin { get; set; } = null!;
        internal static async Task<Fixture> CreateAsync(bool bootstrap = true)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.directory);
            f.Factory = new(Path.Combine(f.directory, "bos.db")); await f.Factory.InitializeAsync();
            f.Authorization = new(f.Clock); f.Identity = new(f.Factory, f.Clock, new UuidGenerator());
            if (bootstrap) f.Admin = await f.BootstrapAsync(Password);
            return f;
        }
        internal async Task<Caller> BootstrapAsync(string password)
        {
            await using var write = await Factory.BeginWriteAsync();
            var user = await Identity.BootstrapAsync(write, password); var stored = (await write.Documents.GetAsync("users", S(user, "id")))!;
            await write.CommitAsync(); return new Caller(S(user, "id"), SecurityStamp: S(stored, "securityStamp"));
        }
        internal async Task<InvocationContext> ReloadAsync(Caller caller)
        {
            await using var read = await Factory.BeginReadAsync(); var context = new InvocationContext { Caller = caller };
            await Authorization.ReloadAsync(read, context); return context;
        }
        internal async Task<JsonObject> CallAsync(string method, JsonObject args, Caller? caller = null)
        {
            await using var write = await Factory.BeginWriteAsync(); var context = new InvocationContext { Caller = caller ?? Admin };
            await Authorization.ReloadAsync(write, context);
            var result = (JsonObject)(await Identity.InvokeAsync(method, args, context, write))!;
            await write.CommitAsync(); return result;
        }
        internal Task<JsonObject> UserAsync(string name, string kind) => CallAsync("users.create", new() { ["user"] = new JsonObject { ["userName"] = name, ["displayName"] = name, ["kind"] = kind } });
        internal Task<JsonObject> RoleAsync(string name, JsonArray grants) => CallAsync("roles.create", new() { ["role"] = new JsonObject { ["name"] = name, ["grants"] = grants } });
        internal async Task<(JsonObject User, JsonObject Role)> ServiceAsync(string name, JsonArray grants)
        {
            var user = await UserAsync(name, "service"); var id = S(user, "id");
            user = await CallAsync("users.update", new() { ["id"] = id, ["expectedRevision"] = 1, ["changes"] = new JsonObject { ["active"] = true } });
            var role = await RoleAsync(name + "Role", grants);
            user = await CallAsync("roles.assign", new() { ["userId"] = id, ["roleId"] = S(role, "id"), ["expectedUserRevision"] = 2 });
            return (user, role);
        }
        internal async Task ErrorAsync(string code, Func<Task> action) => Assert.Equal(code, (await Assert.ThrowsAsync<BosException>(action)).Code);
        public ValueTask DisposeAsync() { if (Directory.Exists(directory)) Directory.Delete(directory, true); return ValueTask.CompletedTask; }
    }
}
