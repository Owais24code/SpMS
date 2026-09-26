using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Core.Data;
using Spms.Modules.Guest.Data;
using Spms.Modules.Resources.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Modules.Workforce.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Scheduling.Infrastructure;

internal static class Ids
{
    /// <summary>Domain ids are uuid strings; anything else names nothing.</summary>
    public static bool TryParse(string? value, out Guid id) => Guid.TryParse(value, out id);
    public static string Of(Guid id) => id.ToString("D");
    public static string? Of(Guid? id) => id?.ToString("D");
    public static Guid? Nullable(string? value) => Guid.TryParse(value, out var g) ? g : null;
}

/// <summary>scheduling.appointment, with the guest alias, service name and property zone joined in.</summary>
public sealed class EfAppointmentRepository(SpmsDbContext db) : IAppointmentRepository
{
    // A class with init members, not a positional record: EF can translate a
    // later Where on a member-initialised projection, not on a constructor call.
    private sealed class Hit
    {
        public required AppointmentRow Row { get; init; }
        public required string TimeZone { get; init; }
        public string? Alias { get; init; }
        public string? Preferred { get; init; }
        public required string ServiceName { get; init; }
    }

    // Both arguments are honoured, not just trusted to the filters: a caller
    // naming the wrong tenant gets nothing back even inside a valid scope.
    private IQueryable<Hit> Query(Guid tenantId, Guid propertyId) =>
        from a in db.Set<AppointmentRow>().AsNoTracking()
        where a.TenantId == tenantId && a.PropertyId == propertyId
        join p in db.Set<PropertyRow>() on a.PropertyId equals p.PropertyId
        join g in db.Set<GuestRow>() on a.GuestId equals g.GuestId
        join s in db.Set<ServiceRow>() on a.ServiceId equals s.ServiceId
        select new Hit { Row = a, TimeZone = p.Timezone, Alias = g.DisplayAlias, Preferred = g.PreferredName, ServiceName = s.Name };

    public async Task<Appointment?> GetAsync(string tenantId, string propertyId, string appointmentId, CancellationToken ct = default)
    {
        if (!Ids.TryParse(tenantId, out var tid) || !Ids.TryParse(propertyId, out var pid) || !Ids.TryParse(appointmentId, out var aid)) return null;
        var hit = await Query(tid, pid).Where(h => h.Row.AppointmentId == aid).SingleOrDefaultAsync(ct);
        return hit is null ? null : ToDomain(hit);
    }

    public async Task<IReadOnlyList<Appointment>> ListOverlappingAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        if (!Ids.TryParse(tenantId, out var tid) || !Ids.TryParse(propertyId, out var pid)) return [];
        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        var rows = await Query(tid, pid)
            .Where(h => h.Row.StartAt < to && from < h.Row.EndAt)
            .OrderBy(h => h.Row.StartAt)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<Appointment>> ListPageAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int offset, int limit, CancellationToken ct = default)
    {
        if (!Ids.TryParse(tenantId, out var tid) || !Ids.TryParse(propertyId, out var pid)) return [];
        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        var rows = await Query(tid, pid)
            .Where(h => h.Row.StartAt < to && from < h.Row.EndAt)
            .OrderBy(h => h.Row.StartAt).ThenBy(h => h.Row.AppointmentId)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public Task<int> CountOverlappingAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        if (!Ids.TryParse(tenantId, out var tid) || !Ids.TryParse(propertyId, out var pid)) return Task.FromResult(0);
        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        return db.Set<AppointmentRow>().AsNoTracking()
            .CountAsync(a => a.TenantId == tid && a.PropertyId == pid && a.StartAt < to && from < a.EndAt, ct);
    }

    public async Task<bool> TryAddAsync(Appointment appointment, CancellationToken ct = default)
    {
        var row = new AppointmentRow();
        Apply(row, appointment.Snapshot(), insert: true);
        db.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException e) when (PostgresErrors.Is(e, PostgresErrors.ExclusionViolation, "room"))
        {
            db.Entry(row).State = EntityState.Detached;
            throw new RoomOverlapException(appointment.RoomId, e);
        }
        catch (DbUpdateException e) when (PostgresErrors.Is(e, PostgresErrors.UniqueViolation, "appointment_pkey"))
        {
            db.Entry(row).State = EntityState.Detached;
            return false;
        }
        finally
        {
            // Repository reads are untracked; a tracked row left behind would be
            // flushed again by the next SaveChanges on this request.
            if (db.Entry(row).State != EntityState.Detached) db.Entry(row).State = EntityState.Detached;
        }
    }

    public async Task<bool> TryUpdateAsync(Appointment appointment, int expectedRowVersion, CancellationToken ct = default)
    {
        if (appointment.RowVersion != expectedRowVersion + 1)
            throw new InvalidOperationException(
                $"An appointment advances one version per write; {expectedRowVersion} -> {appointment.RowVersion} is not one.");
        if (!Ids.TryParse(appointment.AppointmentId, out var aid) || !Ids.TryParse(appointment.PropertyId, out var pid))
            return false;

        var s = appointment.Snapshot();
        var principal = db.Scope.PrincipalId;
        var correlation = db.Scope.CorrelationId ?? s.CorrelationId;
        try
        {
            // One conditional statement: the version check and the write cannot
            // be separated, so two writers cannot both pass the check. The
            // trigger independently refuses anything but version + 1.
            var affected = await db.Set<AppointmentRow>()
                .Where(a => a.AppointmentId == aid && a.PropertyId == pid && a.Version == expectedRowVersion)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(a => a.StartAt, s.StartUtc.ToUniversalTime())
                    .SetProperty(a => a.ProviderId, Ids.Nullable(s.ProviderId))
                    .SetProperty(a => a.RoomId, Ids.Nullable(s.RoomId))
                    .SetProperty(a => a.VisitId, Ids.Nullable(s.VisitId))
                    .SetProperty(a => a.Status, s.Status.ToString())
                    .SetProperty(a => a.HoldExpiresAt, s.HoldExpiresUtc)
                    .SetProperty(a => a.CheckedInAt, s.CheckedInUtc)
                    .SetProperty(a => a.CancelledAt, s.CancelledUtc)
                    .SetProperty(a => a.CancellationReasonCode, s.CancellationReasonCode)
                    .SetProperty(a => a.CompletedAt, s.CompletedUtc)
                    .SetProperty(a => a.Version, expectedRowVersion + 1)
                    .SetProperty(a => a.UpdatedBy, principal)
                    .SetProperty(a => a.CorrelationId, correlation), ct);
            return affected == 1;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrors.ExclusionViolation)
        {
            throw new RoomOverlapException(appointment.RoomId, e);
        }
    }

    public Task<bool> ConfirmationNumberExistsAsync(string tenantId, string confirmationNumber, CancellationToken ct = default)
    {
        if (!Ids.TryParse(tenantId, out var tid)) return Task.FromResult(false);
        // The unique index (tenant_id, confirmation_number) is the real guarantee;
        // RLS limits this pre-check to the scoped properties and the index covers the rest.
        return db.Set<AppointmentRow>().AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(a => a.TenantId == tid && a.ConfirmationNumber == confirmationNumber, ct);
    }

    private sealed record BusyRow(Guid PropertyId, Guid AppointmentId, DateTimeOffset StartAt, DateTimeOffset EndAt);

    public async Task<IReadOnlyList<GuestBusyInterval>> GuestBusyAsync(
        string tenantId, string guestId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        if (!Ids.TryParse(guestId, out var gid)) return [];
        var rows = await db.Database.SqlQueryRaw<BusyRow>(
                "SELECT property_id AS \"PropertyId\", appointment_id AS \"AppointmentId\", start_at AS \"StartAt\", end_at AS \"EndAt\" " +
                "FROM scheduling.guest_busy_intervals({0}, {1}, {2})",
                gid, fromUtc.ToUniversalTime(), toUtc.ToUniversalTime())
            .ToListAsync(ct);
        return rows.Select(r => new GuestBusyInterval(Ids.Of(r.PropertyId), Ids.Of(r.AppointmentId), r.StartAt, r.EndAt)).ToList();
    }

    private static Appointment ToDomain(Hit h)
    {
        var r = h.Row;
        return Appointment.Rehydrate(new AppointmentSnapshot(
            Ids.Of(r.AppointmentId), Ids.Of(r.TenantId), Ids.Of(r.PropertyId), h.TimeZone,
            Ids.Of(r.GuestId), h.Alias ?? h.Preferred ?? "Guest",
            Ids.Of(r.ServiceId), h.ServiceName, r.DurationMinutes,
            Ids.Of(r.ProviderId), Ids.Of(r.RoomId),
            r.StartAt.ToUniversalTime(), Enum.Parse<AppointmentStatus>(r.Status), r.Version,
            r.ConfirmationNumber, r.CorrelationId ?? string.Empty,
            r.CreatedAt.ToUniversalTime(), r.UpdatedAt.ToUniversalTime(),
            r.Source, r.PriceMinor, r.CurrencyCode.Trim(), Ids.Of(r.VisitId), r.GuestRequestedProvider,
            r.HoldExpiresAt?.ToUniversalTime(), r.CheckedInAt?.ToUniversalTime(), r.CancelledAt?.ToUniversalTime(),
            r.CancellationReasonCode, r.CompletedAt?.ToUniversalTime(), r.Options));
    }

    private static void Apply(AppointmentRow r, AppointmentSnapshot s, bool insert)
    {
        if (insert)
        {
            r.AppointmentId = Guid.Parse(s.AppointmentId);
            r.TenantId = Guid.Parse(s.TenantId);
            r.PropertyId = Guid.Parse(s.PropertyId);
            r.GuestId = Guid.Parse(s.GuestId);
            r.ServiceId = Guid.Parse(s.ServiceId);
            r.DurationMinutes = s.DurationMinutes;
            r.EnteredTimezone = s.PropertyTimeZone;
            r.Source = s.Source;
            r.PriceMinor = s.PriceMinor;
            r.CurrencyCode = s.CurrencyCode;
            r.Options = s.OptionsJson;
            r.ConfirmationNumber = s.ConfirmationNumber;
            r.GuestRequestedProvider = s.GuestRequestedProvider;
            r.Version = s.RowVersion;
            r.CorrelationId = s.CorrelationId;
        }
        r.StartAt = s.StartUtc.ToUniversalTime();
        // end_at is maintained by a trigger from start_at + duration; never written here.
        r.ProviderId = Ids.Nullable(s.ProviderId);
        r.RoomId = Ids.Nullable(s.RoomId);
        r.VisitId = Ids.Nullable(s.VisitId);
        r.Status = s.Status.ToString();
        r.HoldExpiresAt = s.HoldExpiresUtc;
        r.CheckedInAt = s.CheckedInUtc;
        r.CancelledAt = s.CancelledUtc;
        r.CancellationReasonCode = s.CancellationReasonCode;
        r.CompletedAt = s.CompletedUtc;
    }
}

/// <summary>
/// scheduling.schedule_change_proposal. Only the SHA-256 of the token is
/// stored; a leaked table row cannot be replayed as a commit. Consumption is a
/// status change (Open → Committed), because the runtime role holds no DELETE.
/// </summary>
public sealed class EfPreflightStore(SpmsDbContext db) : IPreflightStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record StoredConflict(
        string Code, string Rule, string Severity, string Subject, bool OverrideAllowed, bool Overridden,
        string OperationalImpact, string FinancialImpact, IReadOnlyList<string> Resolutions);

    public static string HashToken(string token) => Hashing.Sha256Hex(token);

    public async Task SaveAsync(string tenantId, PreflightResult r, CancellationToken ct = default)
    {
        var conflicts = r.Conflicts.Select(c => new
        {
            code = c.Code, rule = c.Rule, severity = c.Severity.ToString(), subject = c.Summary,
            override_allowed = c.Overridable, overridden = false,
            operationalImpact = c.OperationalImpact, financialImpact = c.FinancialImpact, resolutions = c.Resolutions,
        });
        var row = new ScheduleChangeProposalRow
        {
            PropertyId = Guid.Parse(r.PropertyId),
            AppointmentId = Guid.Parse(r.Proposal.AppointmentId),
            TokenHash = HashToken(r.Token),
            ProposedStart = r.ProposedStartUtc.ToUniversalTime(),
            ProposedEnd = r.ProposedEndUtc.ToUniversalTime(),
            ProposedProviderId = Ids.Nullable(r.Proposal.ProviderId),
            ProposedRoomId = Ids.Nullable(r.Proposal.RoomId),
            FromVersion = r.Proposal.FromRowVersion,
            Conflicts = JsonSerializer.Serialize(conflicts),
            ExpiresAt = r.ExpiresUtc.ToUniversalTime(),
            Status = ScheduleChangeProposalStatuses.Open,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        db.Entry(row).State = EntityState.Detached;
    }

    public async Task<PreflightResult?> FindAsync(string tenantId, string token, CancellationToken ct = default)
    {
        var hash = HashToken(token);
        var row = await db.Set<ScheduleChangeProposalRow>().AsNoTracking()
            .SingleOrDefaultAsync(p => p.TokenHash == hash && p.Status == ScheduleChangeProposalStatuses.Open, ct);
        if (row is null) return null;

        var shown = JsonSerializer.Deserialize<List<JsonElement>>(row.Conflicts) ?? [];
        var conflicts = shown.Select(e => new Conflict(
            e.GetProperty("code").GetString()!, e.GetProperty("rule").GetString()!,
            Enum.Parse<ConflictSeverity>(e.GetProperty("severity").GetString()!),
            e.GetProperty("subject").GetString()!,
            e.GetProperty("operationalImpact").GetString()!, e.GetProperty("financialImpact").GetString()!,
            e.GetProperty("resolutions").EnumerateArray().Select(x => x.GetString()!).ToList())).ToList();

        return new PreflightResult(
            token, row.ExpiresAt.ToUniversalTime(),
            new MoveProposal(Ids.Of(row.AppointmentId), row.ProposedStart.ToUniversalTime(),
                Ids.Of(row.ProposedProviderId), Ids.Of(row.ProposedRoomId), row.FromVersion),
            Ids.Of(row.PropertyId), row.ProposedStart.ToUniversalTime(), row.ProposedEnd.ToUniversalTime(), conflicts);
    }

    public async Task<bool> TryConsumeAsync(string tenantId, string token, DateTimeOffset nowUtc,
        DateTimeOffset? undoUntilUtc, string? reason, CancellationToken ct = default)
    {
        var hash = HashToken(token);
        var principal = db.Scope.PrincipalId;
        // Conditional on status Open: in a race exactly one caller moves it.
        var n = await db.Set<ScheduleChangeProposalRow>()
            .Where(p => p.TokenHash == hash && p.Status == ScheduleChangeProposalStatuses.Open)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, ScheduleChangeProposalStatuses.Committed)
                .SetProperty(p => p.CommittedAt, nowUtc.ToUniversalTime())
                .SetProperty(p => p.UndoUntil, undoUntilUtc)
                .SetProperty(p => p.OverrideReason, reason)
                .SetProperty(p => p.Version, p => p.Version + 1)
                .SetProperty(p => p.UpdatedBy, principal), ct);
        return n == 1;
    }

    public Task<int> EvictExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var now = nowUtc.ToUniversalTime();
        return db.Set<ScheduleChangeProposalRow>()
            .Where(p => p.Status == ScheduleChangeProposalStatuses.Open && p.ExpiresAt < now)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, ScheduleChangeProposalStatuses.Expired)
                .SetProperty(p => p.Version, p => p.Version + 1), ct);
    }
}

/// <summary>
/// catalog.service with the current property's catalog.property_service:
/// a service is bookable at a property only if the property offers it, and
/// the property's price overrides the tenant base price.
/// </summary>
public sealed class EfServiceCatalog(SpmsDbContext db) : IServiceCatalog
{
    private IQueryable<CatalogService> Offered(Guid? only = null)
    {
        var pid = db.Scope.CurrentPropertyId ?? Guid.Empty;
        return from s in db.Set<ServiceRow>().AsNoTracking()
               where s.Status == ServiceStatuses.Active && (only == null || s.ServiceId == only)
               join ps in db.Set<PropertyServiceRow>() on s.ServiceId equals ps.ServiceId
               where ps.PropertyId == pid && ps.Status == PropertyServiceStatuses.Active
               orderby s.Name
               select new CatalogService(
                   s.ServiceId.ToString(), s.Name, s.DurationMinutes,
                   ps.PriceMinor ?? s.BasePriceMinor,
                   (ps.CurrencyCode ?? s.CurrencyCode).Trim(),
                   ps.OnlineBookable ?? s.OnlineBookable,
                   s.DepositRequired, s.RequiresIntake, s.Code);
    }

    public async Task<CatalogService?> FindAsync(string tenantId, string serviceId, CancellationToken ct = default)
    {
        if (!Ids.TryParse(serviceId, out var sid)) return null;
        return await Offered(sid).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<CatalogService>> ListAsync(string tenantId, CancellationToken ct = default) =>
        await Offered().ToListAsync(ct);
}

/// <summary>
/// CON-003, failing closed three ways: no active qualification row means not
/// qualified; the service's required license types each need a Verified,
/// unexpired credential; and the person must be an active, bookable member of
/// staff at this property.
/// </summary>
public sealed class EfQualificationRegister(SpmsDbContext db) : IQualificationRegister
{
    public async Task<bool> IsQualifiedAsync(
        string tenantId, string propertyId, string providerId, string serviceId,
        DateTimeOffset asOfUtc, CancellationToken ct = default)
    {
        if (!Ids.TryParse(providerId, out var staffId) || !Ids.TryParse(serviceId, out var sid)) return false;
        if (!await IsKnownAsync(tenantId, propertyId, providerId, ct)) return false;

        var at = asOfUtc.UtcDateTime;
        var staff = await db.Set<StaffRow>().AsNoTracking().SingleOrDefaultAsync(s => s.StaffId == staffId, ct);
        if (staff is null || !staff.Bookable || staff.EmploymentStatus != "Active") return false;

        var qualified = await db.Set<StaffQualificationRow>().AsNoTracking()
            .AnyAsync(q => q.StaffId == staffId && q.ServiceId == sid
                           && q.Status == StaffQualificationStatuses.Active && q.EffectiveRange.Contains(at), ct);
        if (!qualified) return false;

        var required = await db.Set<ServiceRow>().AsNoTracking()
            .Where(s => s.ServiceId == sid).Select(s => s.RequiredLicenseTypeCodes).SingleOrDefaultAsync(ct) ?? [];
        if (required.Length == 0) return true;

        var today = DateOnly.FromDateTime(at);
        var held = await db.Set<CredentialRow>().AsNoTracking()
            .Where(c => c.StaffId == staffId && c.Status == CredentialStatuses.Verified
                        && c.LicenseTypeCode != null && (c.ExpiresAt == null || c.ExpiresAt >= today))
            .Select(c => c.LicenseTypeCode!)
            .ToListAsync(ct);
        return required.All(held.Contains);
    }

    public async Task<bool> IsKnownAsync(string tenantId, string propertyId, string providerId, CancellationToken ct = default)
    {
        if (!Ids.TryParse(providerId, out var staffId) || !Ids.TryParse(propertyId, out var pid)) return false;
        var home = await db.Set<StaffRow>().AsNoTracking()
            .AnyAsync(s => s.StaffId == staffId && s.HomePropertyId == pid, ct);
        if (home) return true;
        return await db.Set<StaffRoleAssignmentRow>().AsNoTracking()
            .AnyAsync(r => r.StaffId == staffId && r.Status == StaffRoleAssignmentStatuses.Active
                           && (r.PropertyId == null || r.PropertyId == pid), ct);
    }
}

public sealed class EfGuestDirectory(SpmsDbContext db) : IGuestDirectory
{
    public async Task<string?> AliasAsync(string tenantId, string guestId, CancellationToken ct = default)
    {
        if (!Ids.TryParse(guestId, out var gid)) return null;
        var g = await db.Set<GuestRow>().AsNoTracking()
            .Where(x => x.GuestId == gid && (x.Status == GuestStatuses.Active || x.Status == GuestStatuses.Restricted))
            .Select(x => new { x.DisplayAlias, x.PreferredName, x.PublicQueueId })
            .SingleOrDefaultAsync(ct);
        return g is null ? null : g.DisplayAlias ?? g.PreferredName ?? g.PublicQueueId ?? "Guest";
    }
}

/// <summary>resources.resource + maintenance_window for rooms; workforce.work_schedule for people.</summary>
public sealed class EfResourceCalendar(SpmsDbContext db) : IResourceCalendar
{
    public async Task<RoomState> RoomAsync(string tenantId, string propertyId, string roomId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        if (!Ids.TryParse(propertyId, out var pid) || !Ids.TryParse(roomId, out var rid)) return RoomState.Unknown;
        var status = await db.Set<ResourceRow>().AsNoTracking()
            .Where(r => r.ResourceId == rid && r.PropertyId == pid).Select(r => r.Status).SingleOrDefaultAsync(ct);
        if (status is null) return RoomState.Unknown;
        if (status == ResourceStatuses.Retired) return RoomState.Retired;
        if (status == ResourceStatuses.OutOfService) return RoomState.OutOfService;

        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        var closed = await db.Set<MaintenanceWindowRow>().AsNoTracking()
            .AnyAsync(m => m.ResourceId == rid
                           && (m.Status == MaintenanceWindowStatuses.Planned || m.Status == MaintenanceWindowStatuses.Active)
                           && m.StartsAt < to && from < m.EndsAt, ct);
        return closed ? RoomState.OutOfService : RoomState.Available;
    }

    public async Task<RosterState> ProviderAsync(string tenantId, string propertyId, string providerId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        if (!Ids.TryParse(propertyId, out var pid) || !Ids.TryParse(providerId, out var sid)) return RosterState.NotRostered;
        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        var near = await db.Set<WorkScheduleRow>().AsNoTracking()
            .Where(w => w.StaffId == sid && w.PropertyId == pid
                        && (w.Status == WorkScheduleStatuses.Published || w.Status == WorkScheduleStatuses.Approved)
                        && w.StartsAt < to.AddHours(12) && from.AddHours(-12) < w.EndsAt)
            .Select(w => new { w.EntryType, w.StartsAt, w.EndsAt })
            .ToListAsync(ct);

        if (near.Any(w => w.EntryType == "Leave" && w.StartsAt < to && from < w.EndsAt)) return RosterState.OnLeave;
        var shifts = near.Where(w => w.EntryType is "Shift" or "OnCall").ToList();
        if (shifts.Count == 0) return RosterState.NotRostered;
        return shifts.Any(w => w.StartsAt <= from && to <= w.EndsAt) ? RosterState.OnShift : RosterState.OffShift;
    }
}

/// <summary>core.property: zone, weekly hours and the default buffers; catalog.property_service: per-service buffers.</summary>
public sealed class EfPropertyDirectory(SpmsDbContext db) : IPropertyDirectory
{
    public async Task<PropertyProfile?> FindAsync(string tenantId, string propertyId, CancellationToken ct = default)
    {
        if (!Ids.TryParse(propertyId, out var pid)) return null;
        var p = await db.Set<PropertyRow>().AsNoTracking().SingleOrDefaultAsync(x => x.PropertyId == pid, ct);
        if (p is null) return null;

        var defaults = new BufferPolicy(p.RoomTurnoverMinutes, p.ProviderTransitionMinutes);
        var overrides = await db.Set<PropertyServiceRow>().AsNoTracking()
            .Where(x => x.PropertyId == pid && (x.RoomTurnoverMinutes != null || x.ProviderTransitionMinutes != null))
            .ToListAsync(ct);
        var byService = overrides.ToDictionary(
            x => x.ServiceId.ToString(),
            x => new BufferPolicy(x.RoomTurnoverMinutes ?? defaults.RoomTurnoverMinutes,
                                  x.ProviderTransitionMinutes ?? defaults.ProviderTransitionMinutes),
            StringComparer.Ordinal);

        var weekly = ParseHours(p.OpeningHours);
        var (open, close) = weekly is { Count: > 0 } ? weekly.Values.First() : (0, 24 * 60);
        return new PropertyProfile(Ids.Of(p.PropertyId), p.Timezone, open, close, defaults, byService,
            p.CurrencyCode.Trim(), p.OperatingMode, weekly);
    }

    /// <summary>[{"day": 1, "opens": "09:00", "closes": "21:00"}]; an empty array means "not configured" (no limit).</summary>
    public static IReadOnlyDictionary<int, (int Open, int Close)>? ParseHours(string json)
    {
        var items = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? [];
        if (items.Count == 0) return null;
        var d = new Dictionary<int, (int, int)>();
        foreach (var i in items)
        {
            var day = i.GetProperty("day").GetInt32();
            d[day] = (Minutes(i.GetProperty("opens").GetString()!), Minutes(i.GetProperty("closes").GetString()!));
        }
        return d;

        static int Minutes(string hhmm) => int.Parse(hhmm[..2]) * 60 + int.Parse(hhmm[3..5]);
    }
}
