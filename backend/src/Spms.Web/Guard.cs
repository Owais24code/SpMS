using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Spms.SharedKernel;

namespace Spms.Web;

/// <summary>
/// The checks every endpoint repeats. Centralised so 401-vs-403, the body
/// limit and timestamp parsing cannot drift between endpoints — they did, and
/// one endpoint answering 403 to an unauthenticated caller told an attacker
/// the scope name without asking them to authenticate.
/// </summary>
public static class Guard
{
    /// <summary>A body larger than this is refused (API-004 DoS guard).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// Returns the response to send, or null when the caller may proceed.
    /// Unauthenticated is 401 and carries no scope name; authenticated but
    /// unscoped is 403 and names what was missing, because that caller is
    /// entitled to know what to ask their administrator for.
    /// </summary>
    public static IResult? RequireScope(RequestContext ctx, string scope)
    {
        if (!ctx.Authenticated)
            return Problem.From(ApiError.AuthenticationRequired, ctx.CorrelationId,
                "Present a bearer token. In Development, set X-Spa-Scopes.");

        return ctx.Has(scope)
            ? null
            : Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, $"Requires the {scope} scope.");
    }

    /// <summary>
    /// Reads the body, refusing anything over the cap. Returns the body, or a
    /// failure to return instead — matching TryParse's shape rather than the
    /// callback the previous version used, which needed a null-forgiving
    /// operator at all three call sites.
    /// </summary>
    public static async Task<(string? Body, IResult? Failure)> ReadBodyAsync(
        HttpContext http, RequestContext ctx, CancellationToken ct)
    {
        if (http.Request.ContentLength > MaxBodyBytes) return (null, TooLarge(ctx));

        // Pooled: allocating a fresh 64KB buffer per request would make the
        // DoS guard the allocation pressure it exists to prevent. One byte
        // over the cap is read deliberately, so a chunked request with no
        // declared length is still caught.
        var buffer = ArrayPool<byte>.Shared.Rent(MaxBodyBytes + 1);
        try
        {
            var read = 0;
            while (read <= MaxBodyBytes)
            {
                var n = await http.Request.Body.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
                if (n == 0) break;
                read += n;
            }

            if (read > MaxBodyBytes) return (null, TooLarge(ctx));
            return (System.Text.Encoding.UTF8.GetString(buffer, 0, read), null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IResult TooLarge(RequestContext ctx) =>
        Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
            $"Request body exceeds {MaxBodyBytes} bytes.");

    /// <summary>
    /// Parses a body, answering VALIDATION_FAILED rather than letting a
    /// JsonException escape into the 500 handler.
    /// </summary>
    public static bool TryParse<T>(string body, RequestContext ctx, out T? value, out IResult? failure)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(body, Json.Options);
            if (value is null)
            {
                failure = Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "A JSON object body is required.");
                return false;
            }
            failure = null;
            return true;
        }
        catch (JsonException)
        {
            value = default;
            failure = Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "Body was not valid JSON.");
            return false;
        }
    }

    /// <summary>
    /// Parses a timestamp as an instant, in UTC.
    ///
    /// A plain DateTimeOffset.TryParse assumes the SERVER's zone for a value
    /// with no offset, so the same request landed on different instants
    /// depending on where the process ran — in a field named startUtc, whose
    /// own violation rule says iso8601_required. AssumeUniversal removes the
    /// ambiguity and AdjustToUniversal normalises an explicit offset, so what
    /// is stored and echoed is genuinely UTC.
    /// </summary>
    public static bool TryParseInstant(string? raw, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    /// <summary>
    /// A timestamp far enough out to overflow DateTimeOffset arithmetic is a
    /// 422, not a 500. Adding DurationMinutes to DateTimeOffset.MaxValue threw
    /// ArgumentOutOfRangeException from deep inside the conflict scan.
    ///
    /// Inclusive at the lower bound, because every message says "between 2000
    /// and 2100" and a strict comparison rejected 2000-01-01T00:00:00Z itself.
    /// </summary>
    public static bool IsSaneInstant(DateTimeOffset value) =>
        value >= new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
        && value < new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Compares an If-Match header to an ETag, tolerating the weak prefix and
    /// a list of candidates.
    ///
    /// "*" is deliberately NOT accepted on a consequential write. RFC 7232
    /// reads it as "if the resource exists", but API-002 requires the caller
    /// to assert the version they read; honouring "*" made 412 unreachable for
    /// any client that always sent it, i.e. last-write-wins by default.
    /// </summary>
    public static bool ETagMatches(string ifMatch, string etag)
    {
        foreach (var raw in ifMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*") continue;
            var candidate = raw.StartsWith("W/", StringComparison.Ordinal) ? raw[2..] : raw;
            if (string.Equals(candidate, etag, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Extracts the row version a caller asserted through If-Match.</summary>
    public static bool TryParseIfMatchVersion(string ifMatch, out int rowVersion)
    {
        foreach (var raw in ifMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = raw.StartsWith("W/", StringComparison.Ordinal) ? raw[2..] : raw;
            if (int.TryParse(candidate.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out rowVersion))
                return true;
        }
        rowVersion = 0;
        return false;
    }

    public static (int Offset, int Limit) Page(int? offset, int? limit) =>
        (Math.Max(0, offset ?? 0), Math.Clamp(limit ?? PageLimits.Default, 1, PageLimits.Max));
}

/// <summary>
/// Non-generic so nobody has to instantiate Page&lt;object&gt; just to read a
/// constant, which is what the previous arrangement required.
/// </summary>
public static class PageLimits
{
    public const int Default = 100;
    public const int Max = 500;
}
