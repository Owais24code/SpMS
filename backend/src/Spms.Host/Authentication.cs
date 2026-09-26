using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Spms.Host.Identity;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Host;

/// <summary>
/// Authentication: who is calling. Three schemes behind one policy scheme,
/// all producing the same SpMS claim shape (<see cref="SpmsClaims"/>), so
/// nothing downstream knows which one authenticated the caller.
///
///   Entra      staff and service principals: an Entra ID JWT, then resolved
///              to an SpMS principal, tenant, roles and properties
///              (<see cref="PrincipalResolver"/>). Unprovisioned identities are refused.
///   Guest      a guest session minted by SpMS itself after a magic link is
///              redeemed (SEC-010/011): short-lived, purpose-limited, HMAC-signed.
///   Dev        Development only: X-Spa-Login names a seeded dev login (resolved
///              exactly like Entra), or X-Spa-Scopes asserts claims directly for
///              the contract sweep.
/// </summary>
public static class SpmsAuth
{
    public const string PolicyScheme = "Spms";
    public const string JwtScheme = JwtBearerDefaults.AuthenticationScheme;
    public const string GuestScheme = "SpmsGuest";
    public const string DevScheme = "SpmsDevHeaders";
    public const string DevIssuer = "spms-dev";

    /// <summary>Development identity: the fixed ids of database/seed/dev.sql.</summary>
    public static class DevIds
    {
        public const string Tenant = "01920000-0000-7000-8000-000000000001";
        public const string Riverside = "01920000-0000-7000-8000-000000000101";
        public const string Harbour = "01920000-0000-7000-8000-000000000102";
        public const string Dana = "01920000-0000-7000-8000-000000000201";
    }

    public static AuthenticationBuilder AddSpmsAuthentication(
        this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var options = config.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        var devAllowed = env.IsDevelopment() && options.AllowDevHeaders;
        var entra = !string.IsNullOrWhiteSpace(options.Authority);

        var guestKey = GuestSessionIssuer.KeyFrom(options.GuestSigningKey, env);
        services.AddSingleton(new GuestSessionOptions(guestKey, TimeSpan.FromMinutes(options.GuestSessionMinutes)));
        services.AddSingleton<IGuestSessionIssuer, GuestSessionIssuer>();
        services.AddMemoryCache();
        services.AddSingleton<PrincipalResolver>();

        var builder = services.AddAuthentication(o =>
        {
            o.DefaultScheme = PolicyScheme;
            o.DefaultChallengeScheme = PolicyScheme;
        });

        builder.AddPolicyScheme(PolicyScheme, "SpMS", o =>
        {
            o.ForwardDefaultSelector = http =>
            {
                if (devAllowed && (http.Request.Headers.ContainsKey("X-Spa-Scopes") || http.Request.Headers.ContainsKey("X-Spa-Login")))
                    return DevScheme;
                var auth = http.Request.Headers.Authorization.ToString();
                if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && IsGuestToken(auth[7..].Trim()))
                    return GuestScheme;
                return entra ? JwtScheme : GuestScheme;
            };
        });

        if (entra)
        {
            builder.AddJwtBearer(JwtScheme, o =>
            {
                o.Authority = options.Authority;
                o.Audience = options.Audience;
                o.RequireHttpsMetadata = !env.IsDevelopment();
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers = options.ValidIssuers.Length > 0 ? options.ValidIssuers : null,
                    ValidateAudience = true,
                    ValidAudiences = string.IsNullOrWhiteSpace(options.Audience) ? null : [options.Audience],
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    // A 90-second preflight token must not live behind five minutes of skew.
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "preferred_username",
                    RoleClaimType = SpmsClaims.Roles,
                };
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var principal = ctx.Principal!;
                        var issuer = principal.FindFirst("iss")?.Value ?? "";
                        // oid is stable per user per tenant; sub is per application.
                        var subject = principal.FindFirst("oid")?.Value ?? principal.FindFirst("sub")?.Value ?? "";
                        var resolver = ctx.HttpContext.RequestServices.GetRequiredService<PrincipalResolver>();
                        var id = await resolver.ResolveAsync(issuer, subject, ctx.HttpContext.RequestAborted);
                        if (id is null)
                        {
                            ctx.Fail("This identity is not provisioned in SpMS.");
                            return;
                        }
                        var tokenScopes = principal.FindAll("scp").SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            .Concat(principal.FindAll("roles").Select(c => c.Value));
                        principal.AddIdentity(IdentityClaims.For(id, RequestedProperty(ctx.HttpContext), tokenScopes,
                            id.PrincipalType == "Service" ? ActorType.Service : ActorType.Staff));
                    },
                    OnChallenge = Challenge,
                };
            });
        }

        builder.AddJwtBearer(GuestScheme, o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = GuestSessionIssuer.Validation(guestKey);
            o.Events = new JwtBearerEvents { OnChallenge = Challenge };
        });

        if (devAllowed)
            builder.AddScheme<AuthenticationSchemeOptions, DevHeaderHandler>(DevScheme, _ => { });

        return builder;
    }

    /// <summary>The property the client asks to act at (header, then query); validated against the allowed set.</summary>
    public static Guid? RequestedProperty(HttpContext http)
    {
        var raw = http.Request.Headers["X-Spa-Property"].FirstOrDefault() ?? http.Request.Query["property"].FirstOrDefault();
        return Guid.TryParse(raw, out var g) ? g : null;
    }

    private static bool IsGuestToken(string token)
    {
        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            return jwt.Issuer == GuestSessionIssuer.Issuer;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A 401 never says why; the correlation id ties it to the log line that does.</summary>
    private static Task Challenge(JwtBearerChallengeContext ctx)
    {
        ctx.HandleResponse();
        var correlation = RequestContext.CorrelationOf(ctx.HttpContext);
        return Problem.From(ApiError.AuthenticationRequired, correlation, "Present a valid bearer token.").ExecuteAsync(ctx.HttpContext);
    }
}

/// <summary>Turns a resolved identity into the SpMS claim set.</summary>
public static class IdentityClaims
{
    public static ClaimsIdentity For(ResolvedIdentity id, Guid? requestedProperty, IEnumerable<string> tokenScopes, ActorType actorType)
    {
        var current = id.PickProperty(requestedProperty);
        var claims = new List<Claim>
        {
            new(SpmsClaims.TenantId, id.TenantId.ToString()),
            new(SpmsClaims.PrincipalId, id.PrincipalId.ToString()),
            new(SpmsClaims.ActorType, actorType.ToString()),
            new(SpmsClaims.Properties, string.Join(' ', id.Properties.Select(p => p.PropertyId))),
            new("preferred_username", id.DisplayName),
        };
        if (current is { } p) claims.Add(new Claim(SpmsClaims.PropertyId, p.ToString()));
        foreach (var role in id.RoleCodes) claims.Add(new Claim(SpmsClaims.Role, role));
        foreach (var s in RoleScopes.Effective(id.RoleCodes, tokenScopes)) claims.Add(new Claim(SpmsClaims.EffectiveScope, s));
        return new ClaimsIdentity(claims, "spms", "preferred_username", SpmsClaims.Role);
    }
}

public sealed class AuthOptions
{
    /// <summary>e.g. https://login.microsoftonline.com/{tenant}/v2.0</summary>
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    public string[] ValidIssuers { get; set; } = [];

    /// <summary>Base64 HMAC key (32+ bytes) for guest sessions; from Key Vault in deployed environments.</summary>
    public string? GuestSigningKey { get; set; }
    public int GuestSessionMinutes { get; set; } = 30;

    /// <summary>
    /// Honoured only in Development, and even there it can be switched off:
    /// the cost of this being live in a deployed environment is that any
    /// caller mints themselves spa.admin with a curl flag.
    /// </summary>
    public bool AllowDevHeaders { get; set; } = true;
}

/// <summary>
/// Development-only handler.
///
/// X-Spa-Login: a seeded dev login handle (issuer "spms-dev"), resolved by the
/// same <see cref="PrincipalResolver"/> an Entra token goes through — real
/// roles, real properties, real scopes. This is what the front end's demo
/// sign-in uses.
///
/// X-Spa-Scopes: claims asserted directly (tenant, property, principal from
/// X-Spa-*). The HTTP contract sweep uses it to probe scope refusals without a
/// login per case.
/// </summary>
public sealed class DevHeaderHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PrincipalResolver resolver)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var login = Request.Headers["X-Spa-Login"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(login))
        {
            var id = await resolver.ResolveAsync(SpmsAuth.DevIssuer, login.Trim(), Context.RequestAborted);
            if (id is null) return AuthenticateResult.Fail("Unknown development login.");
            var identity = IdentityClaims.For(id, SpmsAuth.RequestedProperty(Context), [], ActorType.Staff);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SpmsAuth.DevScheme));
        }

        var scopes = Request.Headers["X-Spa-Scopes"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(scopes)) return AuthenticateResult.NoResult();

        static string Or(string? v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();

        var tenant = Or(Request.Headers["X-Spa-Tenant"].FirstOrDefault(), SpmsAuth.DevIds.Tenant);
        var property = Or(Request.Headers["X-Spa-Property"].FirstOrDefault(), SpmsAuth.DevIds.Riverside);
        var properties = Or(Request.Headers["X-Spa-Properties"].FirstOrDefault(), property);
        var principal = Or(Request.Headers["X-Spa-Principal"].FirstOrDefault(), SpmsAuth.DevIds.Dana);
        var actor = Or(Request.Headers["X-Spa-Actor"].FirstOrDefault(), "dev-unknown");
        var actorType = Or(Request.Headers["X-Spa-Actor-Type"].FirstOrDefault(), nameof(ActorType.Staff));

        var claims = new List<Claim>
        {
            new(SpmsClaims.Scope, scopes.Trim()),
            new(SpmsClaims.TenantId, tenant),
            new(SpmsClaims.PropertyId, property),
            new(SpmsClaims.Properties, properties.Replace(',', ' ')),
            new(SpmsClaims.PrincipalId, principal),
            new(SpmsClaims.ActorType, actorType),
            new("preferred_username", actor),
        };
        var guest = Request.Headers["X-Spa-Guest"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(guest)) claims.Add(new Claim(SpmsClaims.GuestId, guest.Trim()));

        var asserted = new ClaimsIdentity(claims, SpmsAuth.DevScheme, "preferred_username", SpmsClaims.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(asserted), SpmsAuth.DevScheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        return Problem.From(ApiError.AuthenticationRequired, RequestContext.CorrelationOf(Context),
            "Development authentication: set X-Spa-Login or X-Spa-Scopes.").ExecuteAsync(Context);
    }
}
