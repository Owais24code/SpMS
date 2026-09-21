using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using static Spms.Tests.Harness;

namespace Spms.Tests;

/// <summary>
/// Exercises the real host over HTTP.
///
/// WebApplicationFactory would be tidier but lives in a NuGet package this
/// environment cannot reach, so the host is booted on a port and driven with
/// HttpClient. That has an upside: it tests the actual pipeline — headers,
/// content types, status codes — rather than an in-process shortcut.
/// </summary>
public static class HttpTests
{
    private const string BaseUrl = "http://127.0.0.1:5199";

    private static HttpRequestMessage Req(HttpMethod m, string path, string scopes)
    {
        var r = new HttpRequestMessage(m, BaseUrl + path);
        r.Headers.Add("X-Spms-Scopes", scopes);
        r.Headers.Add("X-Spms-Subject", "tester");
        r.Headers.Add("X-Correlation-Id", "corr-http");
        return r;
    }

    public static async Task RunAsync(HttpClient http)
    {
        Section("HTTP — health and contract basics");
        {
            var health = await http.GetAsync(BaseUrl + "/health");
            Equal("health returns 200", HttpStatusCode.OK, health.StatusCode);

            var denied = await http.SendAsync(Req(HttpMethod.Get, "/availability", ""));
            Equal("availability without a scope returns 403", HttpStatusCode.Forbidden, denied.StatusCode);
            Check("denial is problem+json",
                denied.Content.Headers.ContentType?.MediaType == "application/problem+json");

            var body = await denied.Content.ReadFromJsonAsync<JsonElement>();
            Equal("denial carries the stable code", "AUTHORIZATION_DENIED", body.GetProperty("code").GetString());
            Check("denial echoes the correlation id",
                body.GetProperty("correlation_id").GetString() == "corr-http");
            Check("response header carries the correlation id",
                denied.Headers.TryGetValues("X-Correlation-Id", out _));
        }

        Guid created;
        string etag;

        Section("HTTP — create with idempotency (API-001)");
        {
            var payload = new
            {
                guestAlias = "Guest 9001", serviceCode = "DEEP90", providerId = "lena",
                roomId = "suite-1", startUtc = "2026-09-22T14:00:00Z", durationMinutes = 90,
            };

            var first = Req(HttpMethod.Post, "/appointments", "spa.write spa.read");
            first.Headers.Add("Idempotency-Key", "key-alpha");
            first.Content = JsonContent.Create(payload);
            var r1 = await http.SendAsync(first);
            Equal("create returns 201", HttpStatusCode.Created, r1.StatusCode);

            var dto = await r1.Content.ReadFromJsonAsync<JsonElement>();
            created = dto.GetProperty("appointment_id").GetGuid();
            etag = r1.Headers.ETag?.Tag ?? dto.GetProperty("etag").GetString()!;
            Check("create returns an ETag", !string.IsNullOrWhiteSpace(etag));
            Equal("status is Confirmed", "Confirmed", dto.GetProperty("status").GetString());

            // Same key, same body.
            var second = Req(HttpMethod.Post, "/appointments", "spa.write spa.read");
            second.Headers.Add("Idempotency-Key", "key-alpha");
            second.Content = JsonContent.Create(payload);
            var r2 = await http.SendAsync(second);
            var dto2 = await r2.Content.ReadFromJsonAsync<JsonElement>();
            Check("replay returns the original appointment, not a new one",
                dto2.GetProperty("appointment_id").GetGuid() == created);

            // Same key, different body.
            var third = Req(HttpMethod.Post, "/appointments", "spa.write spa.read");
            third.Headers.Add("Idempotency-Key", "key-alpha");
            third.Content = JsonContent.Create(payload with { });
            third.Content = JsonContent.Create(new
            {
                guestAlias = "Someone else", serviceCode = "DEEP90", providerId = "lena",
                roomId = "suite-1", startUtc = "2026-09-22T14:00:00Z", durationMinutes = 90,
            });
            var r3 = await http.SendAsync(third);
            Equal("same key with a different body is 409", HttpStatusCode.Conflict, r3.StatusCode);
            var p3 = await r3.Content.ReadFromJsonAsync<JsonElement>();
            Equal("and carries IDEMPOTENCY_MISMATCH", "IDEMPOTENCY_MISMATCH", p3.GetProperty("code").GetString());
        }

        Section("HTTP — If-Match is mandatory (API-002)");
        {
            var noMatch = Req(HttpMethod.Post, $"/appointments/{created}/transitions", "spa.write");
            noMatch.Content = JsonContent.Create(new { to = "CheckedIn" });
            var r = await http.SendAsync(noMatch);
            Equal("a transition without If-Match is 422", HttpStatusCode.UnprocessableEntity, r.StatusCode);

            var withMatch = Req(HttpMethod.Post, $"/appointments/{created}/transitions", "spa.write");
            withMatch.Headers.TryAddWithoutValidation("If-Match", etag);
            withMatch.Content = JsonContent.Create(new { to = "CheckedIn" });
            var ok = await http.SendAsync(withMatch);
            Equal("a transition with If-Match succeeds", HttpStatusCode.OK, ok.StatusCode);

            var stale = Req(HttpMethod.Post, $"/appointments/{created}/transitions", "spa.write");
            stale.Headers.TryAddWithoutValidation("If-Match", etag);   // now one version behind
            stale.Content = JsonContent.Create(new { to = "Ready" });
            var s = await http.SendAsync(stale);
            Equal("a stale If-Match is 412", HttpStatusCode.PreconditionFailed, s.StatusCode);

            var sp = await s.Content.ReadFromJsonAsync<JsonElement>();
            Equal("and carries STALE_VERSION", "STALE_VERSION", sp.GetProperty("code").GetString());
            Check("412 returns the current record so the client can merge",
                sp.TryGetProperty("current", out _));
        }

        Section("HTTP — preflight then commit (SCH-020)");
        {
            var pfReq = Req(HttpMethod.Post, "/schedule/preflight", "spa.schedule");
            pfReq.Content = JsonContent.Create(new
            {
                appointmentId = created, providerId = "lena", roomId = "suite-1",
                startUtc = "2026-09-22T18:00:00Z", durationMinutes = 90, fromVersion = 2,
            });
            var pfRes = await http.SendAsync(pfReq);
            Equal("preflight returns 200", HttpStatusCode.OK, pfRes.StatusCode);

            var pf = await pfRes.Content.ReadFromJsonAsync<JsonElement>();
            var token = pf.GetProperty("token").GetString()!;
            Check("preflight issues a token", token.StartsWith("pf_"));
            Check("preflight reports commit_allowed", pf.TryGetProperty("commit_allowed", out _));

            // Board unchanged by the preflight itself.
            var check = await http.SendAsync(Req(HttpMethod.Get, $"/appointments/{created}", "spa.read"));
            var cur = await check.Content.ReadFromJsonAsync<JsonElement>();
            Equal("preflight did not move the appointment", "2026-09-22T14:00:00+00:00",
                  cur.GetProperty("start_utc").GetDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:sszzz"));

            var commit = Req(HttpMethod.Post, "/schedule/commit", "spa.schedule");
            commit.Headers.TryAddWithoutValidation("If-Match", cur.GetProperty("etag").GetString());
            commit.Content = JsonContent.Create(new { token, overrideReason = (string?)null });
            var committed = await http.SendAsync(commit);
            Equal("commit returns 200", HttpStatusCode.OK, committed.StatusCode);

            var after = await committed.Content.ReadFromJsonAsync<JsonElement>();
            Equal("the appointment moved", 18, after.GetProperty("start_utc").GetDateTimeOffset().Hour);

            var reuse = Req(HttpMethod.Post, "/schedule/commit", "spa.schedule");
            reuse.Headers.TryAddWithoutValidation("If-Match", after.GetProperty("etag").GetString());
            reuse.Content = JsonContent.Create(new { token, overrideReason = (string?)null });
            var reused = await http.SendAsync(reuse);
            Equal("a spent token is refused", HttpStatusCode.Conflict, reused.StatusCode);
        }

        Section("HTTP — hard conflict blocks the commit");
        {
            var blocker = Req(HttpMethod.Post, "/appointments", "spa.write");
            blocker.Content = JsonContent.Create(new
            {
                guestAlias = "Guest 9002", serviceCode = "AROMA60", providerId = "priya",
                roomId = "suite-7", startUtc = "2026-09-23T10:00:00Z", durationMinutes = 60,
            });
            await http.SendAsync(blocker);

            var victim = Req(HttpMethod.Post, "/appointments", "spa.write");
            victim.Content = JsonContent.Create(new
            {
                guestAlias = "Guest 9003", serviceCode = "AROMA60", providerId = "marco",
                roomId = "room-4", startUtc = "2026-09-23T16:00:00Z", durationMinutes = 60,
            });
            var vres = await victim.SendWith(http);
            var v = await vres.Content.ReadFromJsonAsync<JsonElement>();

            var pfReq = Req(HttpMethod.Post, "/schedule/preflight", "spa.schedule");
            pfReq.Content = JsonContent.Create(new
            {
                appointmentId = v.GetProperty("appointment_id").GetGuid(),
                providerId = "marco", roomId = "suite-7",
                startUtc = "2026-09-23T10:30:00Z", durationMinutes = 60, fromVersion = 1,
            });
            var pf = await (await http.SendAsync(pfReq)).Content.ReadFromJsonAsync<JsonElement>();
            Check("preflight reports the room clash", pf.GetProperty("conflicts").GetArrayLength() > 0);
            Check("commit_allowed is false", !pf.GetProperty("commit_allowed").GetBoolean());

            var commit = Req(HttpMethod.Post, "/schedule/commit", "spa.schedule spa.admin");
            commit.Headers.TryAddWithoutValidation("If-Match", v.GetProperty("etag").GetString());
            commit.Content = JsonContent.Create(new
            {
                token = pf.GetProperty("token").GetString(),
                overrideReason = "Manager says it is fine",
            });
            var res = await http.SendAsync(commit);
            Equal("a hard conflict is refused even with admin scope and a reason",
                  HttpStatusCode.Conflict, res.StatusCode);
            var p = await res.Content.ReadFromJsonAsync<JsonElement>();
            Equal("and reports HARD_CONFLICT", "HARD_CONFLICT", p.GetProperty("code").GetString());
            Check("the response carries resolutions for the client to offer",
                p.GetProperty("conflicts")[0].GetProperty("resolutions").GetArrayLength() > 0);
            Check("and states financial impact",
                !string.IsNullOrWhiteSpace(p.GetProperty("conflicts")[0].GetProperty("financial_impact").GetString()));
        }

        Section("HTTP — audit is admin-only");
        {
            var denied = await http.SendAsync(Req(HttpMethod.Get, "/audit", "spa.read"));
            Equal("audit without spa.admin is 403", HttpStatusCode.Forbidden, denied.StatusCode);

            var allowed = await http.SendAsync(Req(HttpMethod.Get, "/audit", "spa.admin"));
            Equal("audit with spa.admin is 200", HttpStatusCode.OK, allowed.StatusCode);
            var rows = await allowed.Content.ReadFromJsonAsync<JsonElement>();
            Check("audit has rows", rows.GetArrayLength() > 0);
        }
    }

    private static Task<HttpResponseMessage> SendWith(this HttpRequestMessage m, HttpClient c) => c.SendAsync(m);
}
