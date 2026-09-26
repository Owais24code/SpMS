using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Spms.Modules.Core.Data;
using Spms.Modules.Reporting.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Reporting;

/// <summary>A report: its id, the right it needs, the right its export needs, and one SQL statement per table it returns.</summary>
public sealed record ReportDefinition(string Id, string Title, string Description, string ReadRelation, string ExportRelation, string Version,
    IReadOnlyList<(string Name, string Sql)> Tables);

public sealed record ReportTable(string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);
public sealed record ReportResult(string ReportId, string Date, string TimeZone, IReadOnlyList<ReportTable> Tables);

public sealed record RunInput(string? Date);

/// <summary>
/// Operational reports (R1 reads the operational tables; RPT21315/21316).
/// A run is saved with its canonical result and that result's SHA-256, and
/// the definition's hash, so a report can later prove what it showed and
/// under which definition. A day that has ended is a closed snapshot.
/// Reports carry no guest identities: counts, minutes and money only.
/// Each statement takes @from/@to (the property's day in UTC) and runs
/// under the request's row-level security.
/// </summary>
public sealed class ReportService(SpmsDbContext db, IUnitOfWork uow, IClock clock)
{
    public static readonly IReadOnlyList<ReportDefinition> Catalog =
    [
        new("operations.daily", "Daily operations", "Bookings by status, room and provider utilisation, no-shows.",
            "can_read_operational_reports", "can_export_operational_reports", "1",
        [
            ("by_status", """
                SELECT status, count(*)::int AS appointments, coalesce(sum(duration_minutes), 0)::int AS minutes
                  FROM scheduling.appointment WHERE start_at >= @from AND start_at < @to GROUP BY status ORDER BY status
                """),
            ("by_room", """
                SELECT r.code AS room, r.name, count(a.appointment_id)::int AS appointments,
                       coalesce(sum(a.duration_minutes) FILTER (WHERE a.status NOT IN ('Cancelled', 'NoShow')), 0)::int AS booked_minutes
                  FROM resources.resource r
                  LEFT JOIN scheduling.appointment a ON a.room_id = r.resource_id AND a.start_at >= @from AND a.start_at < @to
                 WHERE r.status <> 'Retired'
                 GROUP BY r.code, r.name HAVING count(a.appointment_id) > 0 ORDER BY r.code
                """),
            ("by_provider", """
                SELECT s.preferred_name AS provider, count(*)::int AS appointments,
                       coalesce(sum(a.duration_minutes) FILTER (WHERE a.status NOT IN ('Cancelled', 'NoShow')), 0)::int AS booked_minutes,
                       count(*) FILTER (WHERE a.status = 'NoShow')::int AS no_shows
                  FROM scheduling.appointment a JOIN workforce.staff s ON s.staff_id = a.provider_id
                 WHERE a.start_at >= @from AND a.start_at < @to GROUP BY s.preferred_name ORDER BY s.preferred_name
                """),
        ]),
        new("finance.daily", "Daily takings", "Money by tender and type, tax and tips on orders paid, refunds.",
            "can_read_financial_reports", "can_read_financial_reports", "1",
        [
            ("by_tender", """
                SELECT tender_code AS tender, transaction_type AS type, count(*)::int AS transactions, sum(amount_minor)::bigint AS amount_minor
                  FROM commerce.payment_transaction WHERE outcome = 'Approved' AND processed_at >= @from AND processed_at < @to
                 GROUP BY tender_code, transaction_type ORDER BY tender_code, transaction_type
                """),
            ("orders", """
                SELECT status, count(*)::int AS orders, coalesce(sum(subtotal_minor), 0)::bigint AS subtotal_minor,
                       coalesce(sum(tax_total_minor), 0)::bigint AS tax_minor, coalesce(sum(tip_total_minor), 0)::bigint AS tips_minor,
                       coalesce(sum(total_minor), 0)::bigint AS total_minor
                  FROM commerce.commerce_order WHERE ordered_at >= @from AND ordered_at < @to GROUP BY status ORDER BY status
                """),
        ]),
        new("inventory.low-stock", "Low stock", "Variants whose usable stock is at or under the reorder point.",
            "can_read_inventory_reports", "can_read_inventory_reports", "1",
        [
            ("low", """
                SELECT i.item_name AS item, v.variant_code AS variant, coalesce(sum(b.on_hand), 0)::numeric(18,2) AS usable, i.reorder_point::numeric(18,2) AS reorder_point
                  FROM inventory.inventory_item_variant v JOIN inventory.inventory_item i ON i.inventory_item_id = v.inventory_item_id
                  LEFT JOIN inventory.inventory_location_balance b ON b.inventory_item_variant_id = v.inventory_item_variant_id AND b.stock_state IN ('Saleable', 'Clean')
                 WHERE v.status = 'Active' AND v.inventory_enabled AND @from IS NOT NULL AND @to IS NOT NULL
                 GROUP BY i.item_name, v.variant_code, i.reorder_point
                HAVING coalesce(sum(b.on_hand), 0) <= i.reorder_point ORDER BY i.item_name
                """),
        ]),
    ];

    /// <summary>Local midnight of the property's day, as an instant.</summary>
    public static DateTimeOffset DayStartUtc(DateOnly day, string timeZoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        if (tz.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return new DateTimeOffset(midnight, tz.GetUtcOffset(midnight)).ToUniversalTime();
    }

    public static ReportDefinition? Find(string id) => Catalog.FirstOrDefault(r => r.Id == id);

    public static string DefinitionHash(ReportDefinition d) =>
        Hex(SHA256.HashData(Encoding.UTF8.GetBytes(d.Id + "\n" + d.Version + "\n" + string.Join("\n--\n", d.Tables.Select(t => t.Name + "\n" + t.Sql)))));

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<ReportRunRow> RunAsync(ReportDefinition d, DateOnly day, string timeZone, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var now = clock.UtcNow;
        var run = new ReportRunRow
        {
            ReportRunId = Uuid7.New(), PropertyId = db.Scope.RequireProperty(), ReportId = d.Id, DefinitionVersion = d.Version,
            DefinitionSha256 = DefinitionHash(d), Filters = JsonSerializer.Serialize(new { date = day.ToString("yyyy-MM-dd") }), AsOf = now,
            StartedAt = now, ExpiresAt = now.AddDays(30), Status = "Running",
            IsClosedSnapshot = to <= now,
        };
        var tables = new List<ReportTable>();
        foreach (var (name, sql) in d.Tables) tables.Add(await QueryAsync(name, sql, from, to, ct));
        var json = JsonSerializer.Serialize(new ReportResult(d.Id, day.ToString("yyyy-MM-dd"), timeZone, tables), Canonical);
        run.ResultJson = json;
        db.Add(run);
        await db.SaveChangesAsync(ct);
        db.Entry(run).State = EntityState.Detached;
        // The hash is over the result as the database keeps it (jsonb's own canonical text), so reading it back verifies.
        var stored = await db.Set<ReportRunRow>().SingleAsync(r => r.ReportRunId == run.ReportRunId, ct);
        stored.ResultSha256 = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(stored.ResultJson!)));
        stored.CompletedAt = clock.UtcNow;
        stored.Status = "Succeeded";
        await db.SaveChangesAsync(ct);
        db.Entry(stored).State = EntityState.Detached;
        await tx.CommitAsync(ct);
        return stored;
    }

    private async Task<ReportTable> QueryAsync(string name, string sql, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = sql;
        Add(cmd, "from", from.UtcDateTime);
        Add(cmd, "to", to.UtcDateTime);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(ct))
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToList());
        return new ReportTable(name, columns, rows);
    }

    private static void Add(DbCommand cmd, string name, DateTime value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        cmd.Parameters.Add(p);
    }

    public Task<List<ReportRunRow>> RunsAsync(string? reportId, CancellationToken ct) =>
        db.Set<ReportRunRow>().AsNoTracking().Where(r => reportId == null || r.ReportId == reportId)
            .OrderByDescending(r => r.AsOf).Take(50).ToListAsync(ct);

    public Task<ReportRunRow?> RunAsync(Guid id, CancellationToken ct) => db.Set<ReportRunRow>().AsNoTracking().SingleOrDefaultAsync(r => r.ReportRunId == id, ct);

    /// <summary>Whether the stored result still hashes to what was recorded, and was made by today's definition.</summary>
    public static (bool ResultIntact, bool DefinitionCurrent) Verify(ReportRunRow run) =>
        (run.ResultJson is { } j && Hex(SHA256.HashData(Encoding.UTF8.GetBytes(j))) == run.ResultSha256,
         Find(run.ReportId) is { } d && DefinitionHash(d) == run.DefinitionSha256);

    public static string Csv(ReportResult result)
    {
        var sb = new StringBuilder();
        foreach (var t in result.Tables)
        {
            sb.Append("# ").Append(t.Name).Append('\n');
            sb.Append(string.Join(',', t.Columns.Select(Cell))).Append('\n');
            foreach (var row in t.Rows) sb.Append(string.Join(',', row.Select(v => Cell(v is JsonElement e ? e.ToString() : Convert.ToString(v, CultureInfo.InvariantCulture))))).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();

        // Quoted when needed; a leading formula character is neutralised so a spreadsheet does not execute it.
        static string Cell(string? v)
        {
            v ??= "";
            if (v.Length > 0 && "=+-@".Contains(v[0]) && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) v = "'" + v;
            return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }
}

public static class ReportEndpoints
{
    private static object Run(ReportRunRow r, bool withResult)
    {
        var (intact, current) = ReportService.Verify(r);
        return new
        {
            runId = r.ReportRunId, r.ReportId, r.DefinitionVersion, r.DefinitionSha256, filters = JsonDocument.Parse(r.Filters).RootElement.Clone(),
            asOfUtc = r.AsOf.ToUniversalTime().ToString("O"), r.Status, r.ResultSha256, closed = r.IsClosedSnapshot, resultIntact = intact, definitionCurrent = current,
            result = withResult && r.ResultJson is { } j ? JsonDocument.Parse(j).RootElement.Clone() : (JsonElement?)null,
        };
    }

    public static IEndpointRouteBuilder MapReports(this IEndpointRouteBuilder app)
    {
        app.MapGet("/reports", (HttpContext http) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json(ReportService.Catalog.Select(d => new { reportId = d.Id, d.Title, d.Description, requires = d.ReadRelation, d.Version }), Json.Options);
        });

        app.MapPost("/reports/{reportId}/runs", async (HttpContext http, ReportService reports, IAccessDecider access, SpmsDbContext db, string reportId, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (ReportService.Find(reportId) is not { } d) return WebApi.NotFound(ctx);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Read, d.ReadRelation, Fga.Property(ctx.PropertyId), ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<RunInput>(http, ctx, ct);
            if (i is null) return fail!;
            var zone = await db.Set<PropertyRow>().AsNoTracking().Where(p => p.PropertyId == ctx.PropertyId).Select(p => p.Timezone).SingleOrDefaultAsync(ct);
            if (zone is null) return WebApi.NotFound(ctx);
            var day = i.Date is null ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime)
                : DateOnly.TryParse(i.Date, out var parsed) ? parsed : (DateOnly?)null;
            if (day is not { } date) return WebApi.Invalid(ctx, "date is yyyy-MM-dd.");
            var run = await reports.RunAsync(d, date, zone, ReportService.DayStartUtc(date, zone), ReportService.DayStartUtc(date.AddDays(1), zone), ct);
            return Results.Json(Run(run, withResult: true), Json.Options, statusCode: 201);
        });

        app.MapGet("/reports/runs", async (HttpContext http, ReportService reports, string? reportId, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await reports.RunsAsync(reportId, ct)).Select(r => Run(r, withResult: false)), Json.Options);
        });

        app.MapGet("/reports/runs/{id:guid}", async (HttpContext http, ReportService reports, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await reports.RunAsync(id, ct) is not { } run || ReportService.Find(run.ReportId) is not { } d) return WebApi.NotFound(ctx);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Read, d.ReadRelation, Fga.Property(ctx.PropertyId), ct) is { } refused) return refused;
            return Results.Json(Run(run, withResult: true), Json.Options);
        });

        app.MapGet("/reports/runs/{id:guid}/export", async (HttpContext http, ReportService reports, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await reports.RunAsync(id, ct) is not { } run || ReportService.Find(run.ReportId) is not { } d) return WebApi.NotFound(ctx);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Read, d.ExportRelation, Fga.Property(ctx.PropertyId), ct) is { } refused) return refused;
            var result = JsonSerializer.Deserialize<ReportResult>(run.ResultJson!, Json.Options)!;
            http.Response.Headers["X-Report-Sha256"] = run.ResultSha256;
            return Results.File(Encoding.UTF8.GetBytes(ReportService.Csv(result)), "text/csv; charset=utf-8", $"{run.ReportId}-{result.Date}.csv");
        });

        return app;
    }
}
