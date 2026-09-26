using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Host;

/// <summary>
/// Authentication for the R1 slice.
///
/// Two schemes, one abstraction. Both produce a <see cref="ClaimsPrincipal"/>
/// with the same claim shape, so nothing downstream knows or cares which one
/// authenticated the caller — <see cref="RequestContext"/> reads claims and
/// never headers.
///
/// This replaces a static DevHeaderAuth flag that endpoints and route
/// registration both consulted. A boolean that changes what a request is
/// allowed to do, set from statement order in Program.cs, is not an
/// authentication model.
/// </summary>
public static class SpmsAuth
{
    public const string JwtScheme = JwtBearerDefaults.AuthenticationScheme;
    public const string DevScheme = "SpmsDevHeaders";

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

        var builder = services.AddAuthentication(o =>
        {
            // In Development the dev scheme is the default so the front end and
            // the acceptance sweep work without an identity provider. Anywhere
            // else the only default is a real bearer token.
            o.DefaultAuthenticateScheme = env.IsDevelopment() && options.AllowDevHeaders ? DevScheme : JwtScheme;
            o.DefaultChallengeScheme = o.DefaultAuthenticateScheme;
        });

        if (!string.IsNullOrWhiteSpace(options.Authority))
        {
            builder.AddJwtBearer(JwtScheme, o =>
            {
                o.Authority = options.Authority;
                o.Audience = options.Audience;
                o.RequireHttpsMetadata = !env.IsDevelopment();

                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers = options.ValidIssuers.Length > 0 ? options.ValidIssuers : null,
                    ValidateAudience = true,
                    ValidAudiences = string.IsNullOrWhiteSpace(options.Audience) ? null : [options.Audience],
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    // Entra ID clock skew is real but small; five minutes is the
                    // library default and far too generous for a 90-second
                    // preflight token to live behind.
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "preferred_username",
                    RoleClaimType = SpmsClaims.Roles,
                };

                o.MapInboundClaims = false;

                o.Events = new JwtBearerEvents
                {
                    // A 401 must not leak why. The correlation id is how support
                    // ties the refusal to the log line that does say why.
                    OnChallenge = ctx =>
                    {
                        ctx.HandleResponse();
                        var rc = RequestContext.From(ctx.HttpContext);
                        return Problem.From(ApiError.AuthenticationRequired, rc.CorrelationId,
                            "Present a valid bearer token.").ExecuteAsync(ctx.HttpContext);
                    },
                };
            });
        }

        if (env.IsDevelopment() && options.AllowDevHeaders)
        {
            builder.AddScheme<AuthenticationSchemeOptions, DevHeaderHandler>(DevScheme, _ => { });
        }

        return builder;
    }
}

public sealed class AuthOptions
{
    /// <summary>e.g. https://login.microsoftonline.com/{tenant}/v2.0</summary>
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    public string[] ValidIssuers { get; set; } = [];

    /// <summary>
    /// Honoured only in Development, and even there it can be switched off.
    /// Two gates rather than one, because the cost of this being live in a
    /// deployed environment is that any caller mints themselves spa.admin with
    /// a curl flag.
    /// </summary>
    public bool AllowDevHeaders { get; set; } = true;
}

/// <summary>
/// Development-only handler that turns X-Spa-* headers into the same claims a
/// real token would carry.
///
/// It is a first-class authentication scheme rather than a special case inside
/// RequestContext, so the production path and the development path go through
/// identical code from the endpoint's point of view — which is the only way to
/// be confident the production path works before there is an identity
/// provider to test against.
/// </summary>
public sealed class DevHeaderHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var scopes = Request.Headers["X-Spa-Scopes"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(scopes)) return Task.FromResult(AuthenticateResult.NoResult());

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

        var identity = new ClaimsIdentity(claims, SpmsAuth.DevScheme, "preferred_username", SpmsClaims.Roles);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SpmsAuth.DevScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var rc = RequestContext.From(Context);
        Response.StatusCode = 401;
        return Problem.From(ApiError.AuthenticationRequired, rc.CorrelationId,
            "Development authentication: set X-Spa-Scopes.").ExecuteAsync(Context);
    }
}
