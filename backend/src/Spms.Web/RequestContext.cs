using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Spms.SharedKernel;

namespace Spms.Web;

/// <summary>
/// Per-request identity and correlation, read from the authenticated
/// principal.
///
/// It reads CLAIMS, not headers. Which scheme produced those claims — an
/// Entra ID bearer token resolved to a principal, a guest session from a magic
/// link, or the Development header handler — is invisible here, so there is
/// one code path to reason about.
///
/// The claims it reads are SpMS's own (<see cref="SpmsClaims"/>), added by
/// principal resolution after the token validates: the token proves who the
/// caller is at the identity provider, and SpMS decides which tenant and
/// properties that person may act in. Roles and property lists never come
/// from the token itself.
/// </summary>
public sealed record RequestContext(
    Guid TenantId,
    Guid PropertyId,
    Guid? PrincipalId,
    ActorType ActorType,
    string Actor,
    string CorrelationId,
    IReadOnlySet<string> Scopes,
    IReadOnlyList<Guid> PropertyIds,
    bool Authenticated)
{
    private const string ItemsKey = "spms.request-context";

    public bool Has(string scope) => Scopes.Contains(scope);

    /// <summary>The guest a guest session acts for (ActorType Guest only).</summary>
    public Guid? GuestId { get; init; }

    public bool IsGuest => ActorType == ActorType.Guest && GuestId is not null;

    /// <summary>The tenant and property as the uuid strings module ports take.</summary>
    public string Tenant() => TenantId.ToString("D");
    public string Property() => PropertyId.ToString("D");

    /// <summary>
    /// Returns the request's context, building it once and caching it.
    ///
    /// Caching is not an optimisation. Building it twice minted two different
    /// correlation ids for one request when the client supplied none, so the
    /// id in the response header, the problem body and the log differed.
    /// </summary>
    public static RequestContext From(HttpContext http)
    {
        if (http.Items.TryGetValue(ItemsKey, out var cached) && cached is RequestContext ctx) return ctx;
        var built = Build(http);
        http.Items[ItemsKey] = built;
        return built;
    }

    /// <summary>
    /// The request's correlation id without building the identity part — for
    /// middleware that runs before authentication, which must not cache an
    /// anonymous context for the endpoints to find later.
    /// </summary>
    public static string CorrelationOf(HttpContext http)
    {
        if (http.Items.TryGetValue("spms.correlation", out var c) && c is string s) return s;
        var id = Sanitize(http.Request.Headers["X-Correlation-Id"].FirstOrDefault());
        http.Items["spms.correlation"] = id;
        return id;
    }

    /// <summary>Forgets the cached context (after principal resolution added claims).</summary>
    public static void Reset(HttpContext http) => http.Items.Remove(ItemsKey);

    private static RequestContext Build(HttpContext http)
    {
        var correlation = CorrelationOf(http);

        var user = http.User;
        var anonymous = new RequestContext(Guid.Empty, Guid.Empty, null, ActorType.System, "anonymous",
            correlation, new HashSet<string>(StringComparer.Ordinal), [], Authenticated: false);

        if (user?.Identity?.IsAuthenticated != true) return anonymous;

        // SpMS's own effective scopes (role-derived, narrowed by the token) win.
        // A raw token scp is only read when nothing resolved the caller — the
        // development header scheme — so a token can never widen what its
        // roles grant.
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var effective = user.FindAll(SpmsClaims.EffectiveScope).ToList();
        if (effective.Count > 0)
        {
            foreach (var claim in effective) scopes.Add(claim.Value.Trim());
        }
        else
        {
            foreach (var claim in user.FindAll(SpmsClaims.Scope))
                foreach (var one in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    scopes.Add(one);
        }

        _ = Guid.TryParse(user.FindFirst(SpmsClaims.TenantId)?.Value, out var tenant);
        _ = Guid.TryParse(user.FindFirst(SpmsClaims.PropertyId)?.Value, out var property);
        Guid? principal = Guid.TryParse(user.FindFirst(SpmsClaims.PrincipalId)?.Value, out var pid) ? pid : null;

        var properties = user.FindAll(SpmsClaims.Properties)
            .SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
        if (property != Guid.Empty && !properties.Contains(property)) properties.Add(property);

        var actorType = Enum.TryParse<ActorType>(user.FindFirst(SpmsClaims.ActorType)?.Value, out var at) ? at : ActorType.Staff;
        var actor = user.FindFirst("preferred_username")?.Value
                    ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? principal?.ToString()
                    ?? "unknown";

        Guid? guest = Guid.TryParse(user.FindFirst(SpmsClaims.GuestId)?.Value, out var gid) ? gid : null;

        return new RequestContext(
            TenantId: tenant, PropertyId: property, PrincipalId: principal, ActorType: actorType,
            Actor: actor.Trim(), CorrelationId: correlation, Scopes: scopes, PropertyIds: properties,
            // A caller with no resolved tenant or property cannot be scoped, so
            // it is not usable identity however valid its token's signature.
            Authenticated: tenant != Guid.Empty && property != Guid.Empty)
        {
            GuestId = guest,
        };
    }

    /// <summary>
    /// The correlation id is echoed into a response header, so a client-supplied
    /// CR or LF would be a response-splitting vector. Length is capped because
    /// it is also written to every log line for the request.
    /// </summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.NewGuid().ToString("n");
        var clean = new string(value.Where(ch => ch is >= ' ' and < (char)127 && ch != ',').ToArray());
        if (clean.Length == 0) return Guid.NewGuid().ToString("n");
        return clean.Length > 64 ? clean[..64] : clean;
    }
}

/// <summary>The claim types SpMS itself issues after resolving a caller.</summary>
public static class SpmsClaims
{
    public const string Scope = "scp";
    public const string Roles = "roles";
    public const string TenantId = "spa_tenant";
    public const string PropertyId = "spa_property";
    public const string Properties = "spa_properties";
    public const string PrincipalId = "spa_principal";
    public const string ActorType = "spa_actor_type";
    public const string GuestId = "spa_guest";
    public const string SessionPurpose = "spa_purpose";
    public const string SessionEntity = "spa_entity";
    /// <summary>A role code SpMS resolved for the caller (repeated).</summary>
    public const string Role = "spa_role";
    /// <summary>A scope SpMS grants the caller (repeated): role scopes narrowed by the token.</summary>
    public const string EffectiveScope = "spa_scope";
}
