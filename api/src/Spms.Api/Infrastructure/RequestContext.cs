using Microsoft.AspNetCore.Http;

namespace Spms.Api.Infrastructure;

/// <summary>
/// Per-request identity and correlation.
///
/// Header-supplied identity is a DEVELOPMENT affordance only and is gated by
/// <see cref="DevHeaderAuth"/>. Outside Development the headers are ignored
/// entirely: a build that trusted X-Spa-Scopes in production would let any
/// caller mint themselves spa.admin with a curl flag.
///
/// Every call site asks this object rather than reading headers, so swapping
/// in JWT claims from Entra ID changes only <see cref="Build"/>.
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
    /// Set once at startup from IHostEnvironment, before the server listens.
    /// </summary>
    public static bool DevHeaderAuth { get; private set; }

    public static void EnableDevHeaderAuth() => DevHeaderAuth = true;

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

        if (!DevHeaderAuth)
        {
            // No JWT middleware is wired yet, so outside Development there is
            // no way to establish identity. Answering 401 is the honest
            // result; falling back to the demo tenant would be an open door.
            return new RequestContext(
                TenantId: string.Empty, PropertyId: string.Empty, Actor: "anonymous",
                CorrelationId: correlation, Scopes: new HashSet<string>(StringComparer.Ordinal),
                Authenticated: false);
        }

        var scopes = (http.Request.Headers["X-Spa-Scopes"].FirstOrDefault() ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        // Whitespace, not just null: a present-but-empty X-Spa-Tenant used to
        // yield TenantId "" and scope every read to a tenant that cannot exist.
        var tenant = http.Request.Headers["X-Spa-Tenant"].FirstOrDefault();
        var property = http.Request.Headers["X-Spa-Property"].FirstOrDefault();
        var actor = http.Request.Headers["X-Spa-Actor"].FirstOrDefault();

        return new RequestContext(
            TenantId: string.IsNullOrWhiteSpace(tenant) ? "tenant-demo" : tenant.Trim(),
            PropertyId: string.IsNullOrWhiteSpace(property) ? "prop-riverside" : property.Trim(),
            Actor: string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim(),
            CorrelationId: correlation,
            Scopes: scopes,
            Authenticated: true);
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
