using Bos.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bos.Identity;

// These stores only use the caller's Documents repository. They never create a connection,
// own a transaction, increment a public revision, or commit: the command owns all of those.
internal sealed class DocumentUserStore(IUnitOfWork uow) : IUserPasswordStore<BosUser>, IUserSecurityStampStore<BosUser>, IUserLockoutStore<BosUser>, IUserRoleStore<BosUser>
{
    private static IdentityResult Conflict() => IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure", Description = "Identity changed." });
    public void Dispose() { }
    public Task<string> GetUserIdAsync(BosUser u, CancellationToken ct) => Task.FromResult(u.Id);
    public Task<string?> GetUserNameAsync(BosUser u, CancellationToken ct) => Task.FromResult(u.UserName);
    public Task SetUserNameAsync(BosUser u, string? name, CancellationToken ct) { u.UserName = name; return Task.CompletedTask; }
    public Task<string?> GetNormalizedUserNameAsync(BosUser u, CancellationToken ct) => Task.FromResult(u.NormalizedUserName);
    public Task SetNormalizedUserNameAsync(BosUser u, string? name, CancellationToken ct) { u.NormalizedUserName = name; return Task.CompletedTask; }
    public async Task<IdentityResult> CreateAsync(BosUser u, CancellationToken ct)
    {
        if (await uow.Documents.GetAsync("users", u.Id, ct) is not null || await FindByNameAsync(u.NormalizedUserName!, ct) is not null)
            return IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = "User name already exists." });
        u.ConcurrencyStamp = Guid.NewGuid().ToString();
        await uow.Documents.PutAsync("users", u.Id, IdentityDocuments.Document(u), ct);
        return IdentityResult.Success;
    }
    public async Task<IdentityResult> UpdateAsync(BosUser u, CancellationToken ct)
    {
        var current = await uow.Documents.GetAsync("users", u.Id, ct);
        if (current is null || current["concurrencyStamp"]?.GetValue<string>() != u.ConcurrencyStamp) return Conflict();
        var other = await FindByNameAsync(u.NormalizedUserName!, ct);
        if (other is not null && other.Id != u.Id) return IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = "User name already exists." });
        u.ConcurrencyStamp = Guid.NewGuid().ToString();
        await uow.Documents.PutAsync("users", u.Id, IdentityDocuments.Document(u), ct);
        return IdentityResult.Success;
    }
    public Task<IdentityResult> DeleteAsync(BosUser u, CancellationToken ct) => throw new NotSupportedException("BOS users are disabled, never deleted.");
    public async Task<BosUser?> FindByIdAsync(string id, CancellationToken ct) => await uow.Documents.GetAsync("users", id, ct) is { } d ? IdentityDocuments.User(d) : null;
    public async Task<BosUser?> FindByNameAsync(string name, CancellationToken ct)
        => (await uow.Documents.ListAsync("users", ct: ct)).FirstOrDefault(d => d["normalizedUserName"]?.GetValue<string>() == name) is { } d ? IdentityDocuments.User(d) : null;
    public Task SetPasswordHashAsync(BosUser u, string? hash, CancellationToken ct) { u.PasswordHash = hash; return Task.CompletedTask; }
    public Task<string?> GetPasswordHashAsync(BosUser u, CancellationToken ct) => Task.FromResult(u.PasswordHash);
    public Task<bool> HasPasswordAsync(BosUser u, CancellationToken ct) => Task.FromResult(u.PasswordHash is not null);
    public Task SetSecurityStampAsync(BosUser u, string stamp, CancellationToken ct) { u.SecurityStamp = stamp; return Task.CompletedTask; }
    public Task<string?> GetSecurityStampAsync(BosUser u, CancellationToken ct) => Task.FromResult(u.SecurityStamp);
    public Task<DateTimeOffset?> GetLockoutEndDateAsync(BosUser u, CancellationToken ct) => Task.FromResult(u.LockoutEnd);
    public Task SetLockoutEndDateAsync(BosUser u, DateTimeOffset? end, CancellationToken ct) { u.LockoutEnd = end; return Task.CompletedTask; }
    public Task<int> IncrementAccessFailedCountAsync(BosUser u, CancellationToken ct) => Task.FromResult(++u.AccessFailedCount);
    public Task ResetAccessFailedCountAsync(BosUser u, CancellationToken ct) { u.AccessFailedCount = 0; return Task.CompletedTask; }
    public Task<int> GetAccessFailedCountAsync(BosUser u, CancellationToken ct) => Task.FromResult(u.AccessFailedCount);
    public Task<bool> GetLockoutEnabledAsync(BosUser u, CancellationToken ct) => Task.FromResult(u.LockoutEnabled);
    public Task SetLockoutEnabledAsync(BosUser u, bool enabled, CancellationToken ct) { u.LockoutEnabled = enabled; return Task.CompletedTask; }
    private async Task<BosRole?> RoleAsync(string normalizedName, CancellationToken ct)
        => (await uow.Documents.ListAsync("roles", ct: ct)).FirstOrDefault(d => d["normalizedName"]?.GetValue<string>() == normalizedName) is { } d ? IdentityDocuments.Role(d) : null;
    public async Task AddToRoleAsync(BosUser u, string roleName, CancellationToken ct)
    {
        var role = await RoleAsync(roleName, ct) ?? throw new BosException("NOT_FOUND", "Role not found.", 404);
        if (!u.RoleIds.Contains(role.Id, StringComparer.Ordinal)) u.RoleIds.Add(role.Id);
    }
    public async Task RemoveFromRoleAsync(BosUser u, string roleName, CancellationToken ct)
    {
        var role = await RoleAsync(roleName, ct);
        if (role is not null) u.RoleIds.Remove(role.Id);
    }
    public async Task<IList<string>> GetRolesAsync(BosUser u, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var id in u.RoleIds)
            if (await uow.Documents.GetAsync("roles", id, ct) is { } d) names.Add(IdentityDocuments.Text(d, "name"));
        return names;
    }
    public async Task<bool> IsInRoleAsync(BosUser u, string roleName, CancellationToken ct) => await RoleAsync(roleName, ct) is { } r && u.RoleIds.Contains(r.Id, StringComparer.Ordinal);
    public async Task<IList<BosUser>> GetUsersInRoleAsync(string roleName, CancellationToken ct)
    {
        var role = await RoleAsync(roleName, ct);
        return role is null ? [] : (await uow.Documents.ListAsync("users", ct: ct)).Select(IdentityDocuments.User).Where(u => u.RoleIds.Contains(role.Id)).ToList();
    }
}

internal sealed class DocumentRoleStore(IUnitOfWork uow) : IRoleStore<BosRole>
{
    public void Dispose() { }
    public Task<string> GetRoleIdAsync(BosRole r, CancellationToken ct) => Task.FromResult(r.Id);
    public Task<string?> GetRoleNameAsync(BosRole r, CancellationToken ct) => Task.FromResult(r.Name);
    public Task SetRoleNameAsync(BosRole r, string? name, CancellationToken ct) { r.Name = name; return Task.CompletedTask; }
    public Task<string?> GetNormalizedRoleNameAsync(BosRole r, CancellationToken ct) => Task.FromResult(r.NormalizedName);
    public Task SetNormalizedRoleNameAsync(BosRole r, string? name, CancellationToken ct) { r.NormalizedName = name; return Task.CompletedTask; }
    public async Task<IdentityResult> CreateAsync(BosRole r, CancellationToken ct)
    {
        if (await uow.Documents.GetAsync("roles", r.Id, ct) is not null || await FindByNameAsync(r.NormalizedName!, ct) is not null)
            return IdentityResult.Failed(new IdentityError { Code = "DuplicateRoleName", Description = "Role name already exists." });
        r.ConcurrencyStamp = Guid.NewGuid().ToString();
        await uow.Documents.PutAsync("roles", r.Id, IdentityDocuments.Document(r), ct); return IdentityResult.Success;
    }
    public async Task<IdentityResult> UpdateAsync(BosRole r, CancellationToken ct)
    {
        var current = await uow.Documents.GetAsync("roles", r.Id, ct);
        if (current is null || current["concurrencyStamp"]?.GetValue<string>() != r.ConcurrencyStamp)
            return IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure", Description = "Identity changed." });
        var other = await FindByNameAsync(r.NormalizedName!, ct);
        if (other is not null && other.Id != r.Id) return IdentityResult.Failed(new IdentityError { Code = "DuplicateRoleName", Description = "Role name already exists." });
        r.ConcurrencyStamp = Guid.NewGuid().ToString();
        await uow.Documents.PutAsync("roles", r.Id, IdentityDocuments.Document(r), ct); return IdentityResult.Success;
    }
    public Task<IdentityResult> DeleteAsync(BosRole r, CancellationToken ct) => throw new NotSupportedException("BOS roles cannot be deleted.");
    public async Task<BosRole?> FindByIdAsync(string id, CancellationToken ct) => await uow.Documents.GetAsync("roles", id, ct) is { } d ? IdentityDocuments.Role(d) : null;
    public async Task<BosRole?> FindByNameAsync(string name, CancellationToken ct)
        => (await uow.Documents.ListAsync("roles", ct: ct)).FirstOrDefault(d => d["normalizedName"]?.GetValue<string>() == name) is { } d ? IdentityDocuments.Role(d) : null;
}

internal sealed class IdentityManagers : IDisposable
{
    internal UserManager<BosUser> Users { get; }
    internal RoleManager<BosRole> Roles { get; }
    internal IdentityManagers(IUnitOfWork uow, IClock clock, CancellationToken ct)
    {
        var options = Options.Create(new IdentityOptions
        {
            Password = new PasswordOptions { RequiredLength = 12, RequiredUniqueChars = 1, RequireDigit = false, RequireLowercase = false, RequireUppercase = false, RequireNonAlphanumeric = false },
            Lockout = new LockoutOptions { AllowedForNewUsers = true, MaxFailedAccessAttempts = 5, DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15) },
            User = new UserOptions { AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_" }
        });
        var userStore = new DocumentUserStore(uow);
        Users = new ClockUserManager(userStore, options, clock, ct);
        Roles = new CancellableRoleManager(new DocumentRoleStore(uow), ct);
    }
    public void Dispose() { Users.Dispose(); Roles.Dispose(); }

    private sealed class CancellableRoleManager(DocumentRoleStore store, CancellationToken ct)
        : RoleManager<BosRole>(store, [new RoleValidator<BosRole>()], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), NullLogger<RoleManager<BosRole>>.Instance)
    {
        protected override CancellationToken CancellationToken => ct;
    }

    // Identity normally reads the system clock internally. Keep its lockout/password/store
    // primitives, while making expiry deterministic through the shared BOS clock.
    private sealed class ClockUserManager : UserManager<BosUser>
    {
        private readonly IClock clock;
        private readonly CancellationToken cancellation;
        protected override CancellationToken CancellationToken => cancellation;
        internal ClockUserManager(DocumentUserStore store, IOptions<IdentityOptions> options, IClock clock, CancellationToken ct)
            : base(store, options, new PasswordHasher<BosUser>(), [new UserValidator<BosUser>()], [new PasswordValidator<BosUser>()], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!, NullLogger<UserManager<BosUser>>.Instance)
        { this.clock = clock; cancellation = ct; }
        public override Task<bool> IsLockedOutAsync(BosUser user) => Task.FromResult(user.LockoutEnabled && user.LockoutEnd is { } end && end > clock.UtcNow);
        public override async Task<IdentityResult> AccessFailedAsync(BosUser user)
        {
            var store = (IUserLockoutStore<BosUser>)Store;
            if (await store.IncrementAccessFailedCountAsync(user, CancellationToken) >= Options.Lockout.MaxFailedAccessAttempts)
            {
                await store.SetLockoutEndDateAsync(user, clock.UtcNow.Add(Options.Lockout.DefaultLockoutTimeSpan), CancellationToken);
                await store.ResetAccessFailedCountAsync(user, CancellationToken);
            }
            return await UpdateAsync(user);
        }
    }
}
