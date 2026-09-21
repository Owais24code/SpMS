using Spms.Application;
using Spms.Domain.Permissions;

namespace Spms.Api.Http;

/// <summary>
/// Builds the caller from request headers.
///
/// Stand-in for the identity provider: scopes arrive in a header rather than a
/// validated JWT. The shape is what production will hand over, so only this
/// file changes when Entra ID lands. Registered only when DevAuth is enabled.
/// </summary>
public static class CallerAccessor
{
    public const string ScopeHeader = "X-Spms-Scopes";
    public const string SubjectHeader = "X-Spms-Subject";
    public const string PropertyHeader = "X-Spms-Property";
    public const string PurposeHeader = "X-Spms-Purpose";
    public const string CorrelationHeader = "X-Correlation-Id";

    public static CallerContext From(HttpContext ctx)
    {
        var scopes = ctx.Request.Headers[ScopeHeader].ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(SpmsScopes.All.Contains)   // unknown scope strings are discarded, not trusted
            .ToHashSet();

        var correlation = ctx.Request.Headers[CorrelationHeader].ToString();
        if (string.IsNullOrWhiteSpace(correlation)) correlation = ctx.TraceIdentifier;

        return new CallerContext(
            Subject: Fallback(ctx.Request.Headers[SubjectHeader].ToString(), "anonymous"),
            TenantId: "aarfid",
            PropertyId: Fallback(ctx.Request.Headers[PropertyHeader].ToString(), "riverside"),
            Scopes: scopes,
            CorrelationId: correlation,
            Purpose: Fallback(ctx.Request.Headers[PurposeHeader].ToString(), "operations"));
    }

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
