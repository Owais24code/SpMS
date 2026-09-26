using Microsoft.EntityFrameworkCore;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Commerce.Data;
using Spms.Modules.Guest.Profiles;
using Spms.Modules.Resources.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Workforce.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Host.Operations;

public sealed record SearchHit(string Type, string Id, string Title, string? Subtitle, string Link);

/// <summary>
/// Universal search (the header box). One query, many kinds of record; each
/// section is searched only when the caller holds the right to see it, so the
/// box can never be used to learn that a guest or an order exists. Guests are
/// matched by the same keyed hash and privacy alias the guest screen uses;
/// results show the alias, never a contact value.
/// </summary>
public static class UniversalSearch
{
    private const int PerSection = 5;

    public static void MapSearch(this IEndpointRouteBuilder app)
    {
        app.MapGet("/search", async (HttpContext http, SpmsDbContext db, GuestProfileService guests, IAccessDecider access, string? q, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            var term = (q ?? "").Trim();
            if (term.Length is < 2 or > 100) return WebApi.Invalid(ctx, "q is 2–100 characters.");
            var property = Fga.Property(ctx.PropertyId);

            async Task<bool> May(string relation) =>
                await Guard.RequireAccessAsync(ctx, access, relation, property, ct: ct) is null;

            var like = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            var hits = new List<SearchHit>();

            if (await May("can_read_guest_profile"))
                foreach (var g in await guests.SearchAsync(term, PerSection, ct))
                    hits.Add(new SearchHit("guest", g.Guest.GuestId.ToString(), g.Guest.DisplayAlias ?? "Guest", g.Guest.PublicQueueId, $"/app/guests?id={g.Guest.GuestId}"));

            if (await May("can_view_board"))
            {
                var upper = term.ToUpperInvariant();
                var appts = await (from a in db.Set<AppointmentRow>().AsNoTracking()
                                   where a.ConfirmationNumber == upper
                                   join s in db.Set<ServiceRow>() on a.ServiceId equals s.ServiceId
                                   select new { a.AppointmentId, a.ConfirmationNumber, a.StartAt, a.Status, s.Name }).Take(PerSection).ToListAsync(ct);
                hits.AddRange(appts.Select(a => new SearchHit("appointment", a.AppointmentId.ToString(), $"{a.ConfirmationNumber} · {a.Name}",
                    $"{a.Status} · {a.StartAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC", $"/app/appointments?id={a.AppointmentId}")));
            }

            if (await May("can_read_payment_status"))
            {
                var orders = await db.Set<CommerceOrderRow>().AsNoTracking()
                    .Where(o => o.OrderNumber == term.ToUpperInvariant() || o.ReceiptNumber == term.ToUpperInvariant())
                    .Select(o => new { o.CommerceOrderId, o.OrderNumber, o.ReceiptNumber, o.Status }).Take(PerSection).ToListAsync(ct);
                hits.AddRange(orders.Select(o => new SearchHit("order", o.CommerceOrderId.ToString(), o.OrderNumber ?? o.ReceiptNumber ?? "Order", o.Status,
                    $"/app/checkout?order={o.CommerceOrderId}")));
            }

            var staff = await db.Set<StaffRow>().AsNoTracking().Where(s => EF.Functions.ILike(s.PreferredName, like) && s.EmploymentStatus != "Terminated")
                .OrderBy(s => s.PreferredName).Take(PerSection).Select(s => new { s.StaffId, s.PreferredName, s.EmploymentStatus }).ToListAsync(ct);
            hits.AddRange(staff.Select(s => new SearchHit("staff", s.StaffId.ToString(), s.PreferredName, s.EmploymentStatus, $"/app/staff?id={s.StaffId}")));

            var services = await db.Set<ServiceRow>().AsNoTracking().Where(s => (EF.Functions.ILike(s.Name, like) || EF.Functions.ILike(s.Code, like)) && s.Status != "Retired")
                .OrderBy(s => s.Name).Take(PerSection).Select(s => new { s.ServiceId, s.Name, s.DurationMinutes }).ToListAsync(ct);
            hits.AddRange(services.Select(s => new SearchHit("service", s.ServiceId.ToString(), s.Name, $"{s.DurationMinutes} min", $"/app/booking?service={s.ServiceId}")));

            var rooms = await db.Set<ResourceRow>().AsNoTracking().Where(r => (EF.Functions.ILike(r.Name, like) || EF.Functions.ILike(r.Code, like)) && r.Status != "Retired")
                .OrderBy(r => r.Code).Take(PerSection).Select(r => new { r.ResourceId, r.Code, r.Name, r.Status }).ToListAsync(ct);
            hits.AddRange(rooms.Select(r => new SearchHit("room", r.ResourceId.ToString(), $"{r.Code} · {r.Name}", r.Status, "/app/setup")));

            return Results.Json(new { q = term, hits }, Json.Options);
        });
    }
}
