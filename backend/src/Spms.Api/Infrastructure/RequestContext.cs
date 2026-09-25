using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Spms.Api.Infrastructure;

/// <summary>
/// Per-request identity and correlation, read from the authenticated
/// principal.
///
/// It reads CLAIMS, not headers. Which scheme produced those claims — a real
/// Entra ID bearer token or the Development header handler — is invisible
/// here, so there is one code path to reason about and the production path is
/// exercised by every test that runs in Development.
/// </summary>
public sealed record RequestContext(
    string TenantId,
    string PropertyId,
    string Actor,
    string CorrelationId,
    IReadOnlySet<string> Scopes,
    bool Authenticated)
{
    private const string ItemsKey = "spms.request-context";

    public bool Has(string scope) => Scopes.Contains(scope);

    /// <summary>
    /// Returns the request's context, building it once and caching it.
    ///
    /// Caching is not an optimisation. Building it twice minted two different
    /// correlation ids for one request when the client supplied none, so the
    /// id in the response header, the id in the problem body and the id in the
    /// log were three different values — which defeats the entire purpose of
    /// having one.
    /// </summary>
    public static RequestContext From(HttpContext http)
    {
        if (http.Items.TryGetValue(ItemsKey, out var cached) && cached is RequestContext ctx) return ctx;

        var built = Build(http);
        http.Items[ItemsKey] = built;
        return built;
    }

    private static RequestContext Build(HttpContext http)
    {
        var correlation = Sanitize(http.Request.Headers["X-Correlation-Id"].FirstOrDefault());
        var user = http.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            return new RequestContext(
                TenantId: string.Empty, PropertyId: string.Empty, Actor: "anonymous",
                CorrelationId: correlation, Scopes: new HashSet<string>(StringComparer.Ordinal),
                Authenticated: false);
        }

        // Entra ID delivers delegated permissions in `scp` as one
        // space-separated string, and application permissions as repeated
        // `roles` claims. Both are accepted so a daemon client and a signed-in
        // operator reach the same authorization decision.
        var scopes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in user.FindAll(SpmsAuth.Claims.Scope))
            foreach (var s in c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                scopes.Add(s);

        foreach (var c in user.FindAll(SpmsAuth.Claims.Roles)) scopes.Add(c.Value.Trim());
        foreach (var c in user.FindAll(ClaimTypes.Role)) scopes.Add(c.Value.Trim());

        var tenant = user.FindFirst(SpmsAuth.Claims.TenantId)?.Value;
        var property = user.FindFirst(SpmsAuth.Claims.PropertyId)?.Value;

        // Property may legitimately arrive per request for an operator entitled
        // to several, but only from the set the token grants. Until the claim
        // carries a list, the claim alone decides — a header that could widen
        // scope is the hole this replaces.
        var actor = user.FindFirst("preferred_username")?.Value
                    ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? user.FindFirst("oid")?.Value
                    ?? "unknown";

        return new RequestContext(
            TenantId: string.IsNullOrWhiteSpace(tenant) ? string.Empty : tenant.Trim(),
            PropertyId: string.IsNullOrWhiteSpace(property) ? string.Empty : property.Trim(),
            Actor: actor.Trim(),
            CorrelationId: correlation,
            Scopes: scopes,
            // A token with no tenant or property claim cannot be scoped, so it
            // is not usable identity however valid its signature.
            Authenticated: !string.IsNullOrWhiteSpace(tenant) && !string.IsNullOrWhiteSpace(property));
    }

    /// <summary>
    /// The correlation id is echoed into a response header, so a client-supplied
    /// CR or LF would be a response-splitting vector. Length is capped because
    /// it is also written to every log line for the request.
    /// </summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.NewGuid().ToString("n");
        var clean = new string(value.Where(c => c is >= ' ' and < (char)127 && c != ',').ToArray());
        if (clean.Length == 0) return Guid.NewGuid().ToString("n");
        return clean.Length > 64 ? clean[..64] : clean;
    }
}
