using Bos.Contracts;
using static Bos.Identity.IdentityDocuments;

namespace Bos.Identity;

/// <summary>Current stored role grants, intersected with every credential/runtime ceiling.</summary>
public sealed class Authorization(IClock? clock = null) : IAuthorization
{
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task ReloadAsync(IUnitOfWork uow, InvocationContext context, CancellationToken ct = default)
    {
        if (await uow.Documents.GetAsync("users", context.PrincipalId, ct) is not { } document) throw Unauthenticated();
        var user = User(document);
        if (!Active(user)) throw Unauthenticated();
        if (context.Caller.SecurityStamp is { } stamp && (user.SecurityStamp is null || !EqualSecret(stamp, user.SecurityStamp))) throw Unauthenticated();
        if (context.Caller.CredentialId is { } tokenId)
        {
            var token = await uow.Documents.GetAsync("tokens", tokenId, ct);
            if (user.Kind != "service" || token is null || !TokenValid(token, user.Id, clock.UtcNow)) throw Unauthenticated();
            context.Ceilings.Add(ReadGrants(Array(token, "scopes")));
            if (context.Caller.TokenScopes is { } issuedScopes) context.Ceilings.Add(issuedScopes.ToArray());
        }
        else if (user.Kind != "human" || context.Caller.SecurityStamp is null || context.Caller.TokenScopes is not null) throw Unauthenticated();

        var grants = await EffectiveAsync(uow, user, ct);
        context.Grants = grants.ToArray();
        // Preserve even an empty snapshot on repeated reloads. A nested call can only narrow
        // authority; neither a role change nor a later reload may widen this invocation.
        context.Ceilings.Add(grants.ToArray());
    }

    public bool Can(InvocationContext context, string action, string resource)
        => Actions.Contains(action, StringComparer.Ordinal) && Covers(context.Grants, action, resource)
            && context.Ceilings.All(ceiling => Covers(ceiling, action, resource));

    public void Require(InvocationContext context, string action, string resource)
    {
        if (!Can(context, action, resource)) throw new BosException("FORBIDDEN", "Permission denied.", 403);
    }
}
