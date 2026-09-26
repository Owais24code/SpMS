using Microsoft.AspNetCore.Http;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Web;

/// <summary>The HTTP face of <see cref="Edit{T}"/>: status codes, ETag and the current row on a stale write.</summary>
public static class EditResults
{
    public static IResult ToHttp<T>(this Edit<T> e, HttpContext http, RequestContext ctx, Func<T, object> dto, int okStatus = 200)
        where T : class, IVersioned
    {
        switch (e.Outcome)
        {
            case EditOutcome.Ok:
                http.Response.Headers.ETag = $"\"{e.Row!.Version}\"";
                return Results.Json(dto(e.Row), Json.Options, statusCode: okStatus);
            case EditOutcome.NotFound:
                return Problem.From(ApiError.NotFound, ctx.CorrelationId, e.Detail);
            case EditOutcome.StaleVersion:
                return Problem.From(ApiError.StaleVersion, ctx.CorrelationId, e.Detail ?? "The record changed since you read it.",
                    extensions: e.Row is null ? null : Problem.Ext("current", dto(e.Row)));
            case EditOutcome.Conflict:
            case EditOutcome.Illegal:
                return Problem.From(ApiError.HardConflict, ctx.CorrelationId, e.Detail);
            default:
                return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, e.Detail);
        }
    }

    public static IResult Ok<T>(HttpContext http, T row, Func<T, object> dto) where T : IVersioned
    {
        http.Response.Headers.ETag = $"\"{row.Version}\"";
        return Results.Json(dto(row), Json.Options);
    }
}

/// <summary>The opening lines every write endpoint shares: scope, relationship, body.</summary>
public static class WebApi
{
    /// <summary>Checks the scope and, when a relation is named, the caller's relationship to the object.</summary>
    public static async Task<IResult?> GateAsync(RequestContext ctx, IAccessDecider access, string scope, string? relation, string? @object,
        CancellationToken ct, IReadOnlyList<FgaTuple>? contextual = null)
    {
        if (Guard.RequireScope(ctx, scope) is { } denied) return denied;
        if (relation is not null && @object is not null
            && await Guard.RequireAccessAsync(ctx, access, relation, @object, contextual, ct: ct) is { } refused) return refused;
        return null;
    }

    public static async Task<(T? Value, IResult? Failure)> BodyAsync<T>(HttpContext http, RequestContext ctx, CancellationToken ct)
    {
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return (default, fail);
        return Guard.TryParse<T>(body, ctx, out var v, out var f) ? (v, null) : (default, f);
    }

    public static IResult Invalid(RequestContext ctx, string detail) => Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, detail);

    public static IResult NotFound(RequestContext ctx) => Problem.From(ApiError.NotFound, ctx.CorrelationId);
}
