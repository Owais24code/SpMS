using Microsoft.AspNetCore.Mvc;
using Spms.Domain.Errors;
using Spms.Domain.Scheduling;

namespace Spms.Api.Http;

/// <summary>
/// Every failure leaves through here, so the response shape is uniform:
/// application/problem+json carrying the stable code, the correlation id and
/// whether a retry is safe (API_Error_Catalog.md).
/// </summary>
public static class ProblemResults
{
    public static IResult From(SpmsProblem p, string correlationId, string? detail = null,
        IDictionary<string, object?>? extensions = null)
    {
        var pd = new ProblemDetails
        {
            Type = $"https://docs.aarfid.com/spa/errors/{p.Code.ToLowerInvariant()}",
            Title = p.Title,
            Status = p.Status,
            Detail = detail,
        };

        pd.Extensions["code"] = p.Code;
        pd.Extensions["correlation_id"] = correlationId;
        pd.Extensions["retryable"] = p.Retryable;

        if (extensions is not null)
            foreach (var (k, v) in extensions) pd.Extensions[k] = v;

        return Results.Json(pd, statusCode: p.Status, contentType: "application/problem+json");
    }

    /// <summary>
    /// A conflict response carries the alternatives, because CON-002 requires
    /// the client to offer one-click resolutions without a second round trip.
    /// </summary>
    public static IResult FromConflicts(IReadOnlyList<Conflict> conflicts, string correlationId)
    {
        var anyHard = conflicts.Any(c => !c.Overridable);
        var problem = anyHard ? SpmsProblem.HardConflict : SpmsProblem.SoftConflictApprovalRequired;

        return From(problem, correlationId,
            detail: conflicts[0].Summary,
            extensions: new Dictionary<string, object?>
            {
                ["conflicts"] = conflicts.Select(c => new
                {
                    code = c.Code,
                    severity = c.Severity.ToString().ToLowerInvariant(),
                    summary = c.Summary,
                    operational_impact = c.OperationalImpact,
                    financial_impact = c.FinancialImpact,
                    overridable = c.Overridable,
                    resolutions = c.Resolutions,
                }).ToArray(),
            });
    }
}
