using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Scheduling.Domain;
using Spms.Modules.Workforce.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Scheduling.Endpoints;

/// <summary>
/// The OpenFGA checks for scheduling. Property-level rights are checked on
/// property:&lt;id&gt;; appointment-level rights on appointment:&lt;id&gt; with the
/// row's own relationships passed as contextual tuples (property, guest,
/// assigned provider), so reassigning a provider never needs a tuple write.
/// </summary>
public sealed class AppointmentAccess(SpmsDbContext db, IAccessDecider access)
{
    public Task<IResult?> PropertyAsync(RequestContext ctx, string relation, CancellationToken ct) =>
        Guard.RequireAccessAsync(ctx, access, relation, Fga.Property(ctx.PropertyId), ct: ct);

    public async Task<IResult?> AppointmentAsync(RequestContext ctx, Appointment a, string relation, CancellationToken ct, bool strong = false)
    {
        var tuples = new List<FgaTuple>
        {
            new(Fga.Property(a.PropertyId), "property", Fga.Appointment(a.AppointmentId)),
            new(Fga.Guest(a.GuestId), "guest", Fga.Appointment(a.AppointmentId)),
        };
        if (Guid.TryParse(a.ProviderId, out var staffId))
        {
            var principal = await db.Set<StaffRow>().AsNoTracking()
                .Where(s => s.StaffId == staffId).Select(s => s.PrincipalId).SingleOrDefaultAsync(ct);
            if (principal is { } p) tuples.Add(new FgaTuple(Fga.User(p), "assigned_provider", Fga.Appointment(a.AppointmentId)));
        }
        var context = new Dictionary<string, object>
        {
            ["current_time"] = DateTimeOffset.UtcNow.ToString("O"),
            ["property_id"] = a.PropertyId,
        };
        return await Guard.RequireAccessAsync(ctx, access, relation, Fga.Appointment(a.AppointmentId), tuples, strong, context, ct);
    }
}
