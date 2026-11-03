using System.Security.Claims;
using System.Text.Encodings.Web;
using Bos.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Bos.Host;

public static class Authentication
{
    public const string Scheme = "Bos";
    public const string CookieScheme = "Bos.Cookie";
    public const string TokenScheme = "Bos.Token";
    private const string StampClaim = "bos:security-stamp";

    public static void AddBosAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme)
            .AddPolicyScheme(Scheme, Scheme, options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey("Authorization") ? TokenScheme : CookieScheme)
            .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenScheme, _ => { })
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = "__Host-Bos.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = _ => throw new BosException("UNAUTHENTICATED", "Authentication is required.", 401),
                    OnRedirectToAccessDenied = _ => throw new BosException("FORBIDDEN", "Access is forbidden.", 403),
                    OnValidatePrincipal = async context =>
                    {
                        var caller = CookieCaller(context.Principal);
                        if (caller is null) { context.RejectPrincipal(); return; }
                        try
                        {
                            var factory = context.HttpContext.RequestServices.GetRequiredService<IUnitOfWorkFactory>();
                            await using var uow = await factory.BeginReadAsync(context.HttpContext.RequestAborted);
                            var authorization = context.HttpContext.RequestServices.GetRequiredService<IAuthorization>();
                            await authorization.ReloadAsync(uow, new InvocationContext { Caller = caller }, context.HttpContext.RequestAborted);
                        }
                        catch (BosException e) when (e.Status is 401 or 403 or 404)
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(CookieScheme);
                        }
                    }
                };
            });
    }

    public static ClaimsPrincipal CookiePrincipal(Caller caller)
    {
        if (string.IsNullOrEmpty(caller.SecurityStamp)) throw new InvalidOperationException("Login did not supply a security stamp.");
        return new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, caller.PrincipalId), new Claim(StampClaim, caller.SecurityStamp)
        ], CookieScheme));
    }

    private static Caller? CookieCaller(ClaimsPrincipal? principal)
    {
        var id = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = principal?.FindFirstValue(StampClaim);
        return Guid.TryParseExact(id, "D", out _) && !string.IsNullOrEmpty(stamp) ? new Caller(id!, SecurityStamp: stamp) : null;
    }

    public static bool IsCookie(HttpContext context) => context.User.Identity?.AuthenticationType == CookieScheme;

    public static Caller RequireCaller(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            throw new BosException("UNAUTHENTICATED", "Authentication is required.", 401);
        if (context.Items.TryGetValue(typeof(Caller), out var value) && value is Caller tokenCaller) return tokenCaller;
        return CookieCaller(context.User) ?? throw new BosException("UNAUTHENTICATED", "Authentication is required.", 401);
    }
}

internal sealed class TokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IIdentityService identity)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers.Authorization;
        if (headers.Count != 1 || headers[0] is not string header || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Invalid credentials.");
        var token = header[7..];
        if (token.Length is < 1 or > 512 || token.Any(char.IsWhiteSpace)) return AuthenticateResult.Fail("Invalid credentials.");
        Caller? caller;
        try { caller = await identity.AuthenticateTokenAsync(token, Context.RequestAborted); }
        catch (BosException e) when (e.Status is 401 or 403 or 404) { return AuthenticateResult.Fail("Invalid credentials."); }
        if (caller is null) return AuthenticateResult.Fail("Invalid credentials.");
        Context.Items[typeof(Caller)] = caller;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, caller.PrincipalId)], Authentication.TokenScheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Authentication.TokenScheme));
    }
}
