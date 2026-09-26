using Microsoft.AspNetCore.Http;
using Spms.SharedKernel;

namespace Spms.Web;

/// <summary>
/// Writes application/problem+json exactly as API_Error_Catalog.md specifies:
/// type, title, status, code, detail, correlation_id, retryable, and where
/// relevant field violations or conflict alternatives.
///
/// PII never appears here (API-004) — details describe the rule that failed,
/// never the value that failed it.
/// </summary>
public static class Problem
{
    /// <summary>
    /// The members an extension may never overwrite. Extensions were copied
    /// straight into the body dictionary, so a call site passing
    /// `new { status = appointment.Status }` replaced the HTTP status in the
    /// problem body with the string "Cancelled" — and could equally have
    /// shadowed type, code, detail or correlation_id.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "type", "title", "status", "code", "detail", "correlation_id", "retryable", "retry_after_seconds",
    };

    public static IResult From(
        ApiError error,
        string correlationId,
        string? detail = null,
        bool retryable = false,
        int? retryAfterSeconds = null,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = $"https://docs.aarfid.com/spa/errors/{error.Code.ToLowerInvariant()}",
            ["title"] = error.Title,
            ["status"] = error.Status,
            ["code"] = error.Code,
            ["detail"] = detail,
            ["correlation_id"] = correlationId,
            ["retryable"] = retryable,
        };

        if (retryAfterSeconds is not null) body["retry_after_seconds"] = retryAfterSeconds;

        if (extensions is not null)
        {
            foreach (var (name, value) in extensions)
            {
                if (Reserved.Contains(name))
                    throw new ArgumentException(
                        $"Problem extension '{name}' would shadow a reserved problem+json member.", nameof(extensions));
                body[name] = value;
            }
        }

        var json = Results.Json(body, Json.Options, statusCode: error.Status, contentType: "application/problem+json");

        // Retry-After is what clients and proxies actually honour; the body
        // field alone was advice nothing acted on.
        return retryAfterSeconds is null ? json : new WithRetryAfter(json, retryAfterSeconds.Value);
    }

    private sealed class WithRetryAfter(IResult inner, int seconds) : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => (inner as IStatusCodeHttpResult)?.StatusCode;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString();
            return inner.ExecuteAsync(httpContext);
        }
    }

    /// <summary>Builders, so call sites do not hand-roll dictionaries.</summary>
    public static Dictionary<string, object?> Ext(string name, object? value) =>
        new(StringComparer.Ordinal) { [name] = value };

    public static Dictionary<string, object?> Ext(
        string name1, object? value1, string name2, object? value2) =>
        new(StringComparer.Ordinal) { [name1] = value1, [name2] = value2 };

    public static Dictionary<string, object?> Ext(
        string name1, object? value1, string name2, object? value2, string name3, object? value3) =>
        new(StringComparer.Ordinal) { [name1] = value1, [name2] = value2, [name3] = value3 };
}
