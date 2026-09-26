using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Workforce.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Workforce;

public sealed record StaffInput(string? PreferredName, Guid? HomePropertyId, string? DepartmentCode, string? EmploymentStatus, bool? Bookable);
public sealed record HrInput(string? EmployeeNumber, string? FirstName, string? MiddleName, string? LastName, string? WorkEmail, string? PersonalEmail,
    string? MobilePhone, string? WorkerType, string? JobTitle, string? HireDate, string? EndDate, string[]? Languages, string? PublicBio, bool? PublicProfileConsent);
public sealed record RoleInput(string? RoleCode, Guid? PropertyId, bool? TenantWide);
public sealed record QualificationInput(Guid? ServiceId, Guid? CredentialId);
public sealed record CredentialInput(string? CredentialKind, string? LicenseTypeCode, string? Jurisdiction, string? IssuerName, string? Number,
    string? IssuedAt, string? ExpiresAt, string? Restrictions);
public sealed record RosterInput(Guid? StaffId, string? EntryType, string? LeaveType, string? StartsUtc, string? EndsUtc);
public sealed record ReasonInput(string? Reason);

public sealed record StaffView(StaffRow Staff, IReadOnlyList<StaffRoleAssignmentRow> Roles, IReadOnlyList<Guid> QualifiedServiceIds);

/// <summary>
/// Staff (§53.2), kept in two parts as SEC-007 requires: the operational
/// profile everyone at the property works with, and the HR profile only HR
/// and the person themselves may read. Roles are proposed and approved by two
/// different people (SEC-014) and reach OpenFGA through the outbox.
/// Qualifications decide who may perform what (CON-003, closed by default).
/// Credential numbers are encrypted; only the last four are ever shown.
/// </summary>
public sealed class WorkforceService(SpmsDbContext db, MasterData master, IOutbox outbox, IFieldProtector protector, IClock clock, IUnitOfWork uow)
{
    public static readonly string[] Roles =
    [
        "provider", "front_desk", "spa_manager", "hr_compliance", "finance", "platform_admin", "configuration_approver", "scheduler",
        "housekeeping", "inventory_manager", "marketing", "support", "operations_analyst", "executive", "release_manager", "security_admin",
        "integration_service",
    ];

    /* ------------------------------ operational ------------------------------ */

    public async Task<List<StaffView>> ListAsync(bool includeTerminated, CancellationToken ct)
    {
        var staff = await db.Set<StaffRow>().AsNoTracking().Where(s => includeTerminated || s.EmploymentStatus != "Terminated")
            .OrderBy(s => s.PreferredName).ToListAsync(ct);
        var ids = staff.Select(s => s.StaffId).ToList();
        var roles = await db.Set<StaffRoleAssignmentRow>().AsNoTracking()
            .Where(r => ids.Contains(r.StaffId) && (r.Status == "Active" || r.Status == "Proposed")).ToListAsync(ct);
        var quals = await db.Set<StaffQualificationRow>().AsNoTracking().Where(q => ids.Contains(q.StaffId) && q.Status == "Active")
            .Select(q => new { q.StaffId, q.ServiceId }).ToListAsync(ct);
        return staff.Select(s => new StaffView(s, roles.Where(r => r.StaffId == s.StaffId).ToList(),
            quals.Where(q => q.StaffId == s.StaffId).Select(q => q.ServiceId).ToList())).ToList();
    }

    public Task<StaffRow?> StaffAsync(Guid id, CancellationToken ct) => db.Set<StaffRow>().AsNoTracking().SingleOrDefaultAsync(s => s.StaffId == id, ct);

    public Task<Edit<StaffRow>> CreateAsync(StaffInput i, CancellationToken ct) =>
        master.CreateAsync(new StaffRow
        {
            StaffId = Uuid7.New(), PreferredName = i.PreferredName!.Trim(), HomePropertyId = i.HomePropertyId ?? db.Scope.CurrentPropertyId,
            DepartmentCode = i.DepartmentCode, EmploymentStatus = i.EmploymentStatus ?? "Active", Bookable = i.Bookable ?? true,
        }, "workforce.staff.create", "staff", s => s.StaffId, ct);

    public Task<Edit<StaffRow>> UpdateAsync(Guid id, int version, StaffInput i, CancellationToken ct) =>
        master.ChangeAsync<StaffRow>(s => s.StaffId == id, version, "workforce.staff.update", "staff", s => s.StaffId, s =>
        {
            if (i.PreferredName is not null) s.PreferredName = i.PreferredName.Trim();
            if (i.HomePropertyId is not null) s.HomePropertyId = i.HomePropertyId;
            if (i.DepartmentCode is not null) s.DepartmentCode = i.DepartmentCode.Length == 0 ? null : i.DepartmentCode;
            if (i.EmploymentStatus is not null) s.EmploymentStatus = i.EmploymentStatus;
            if (i.Bookable is { } b) s.Bookable = b;
            return null;
        }, ct);

    /* --------------------------------- HR ---------------------------------- */

    public Task<SpaServiceProviderRow?> HrAsync(Guid staffId, CancellationToken ct) =>
        db.Set<SpaServiceProviderRow>().AsNoTracking().SingleOrDefaultAsync(h => h.StaffId == staffId, ct);

    /// <summary>What the audit trail may keep of an HR change: never the personal contact fields.</summary>
    private static object HrAudit(SpaServiceProviderRow h) => new { h.StaffId, h.EmployeeNumber, h.JobTitle, h.WorkerType, h.HireDate, h.EndDate };

    public async Task<Edit<SpaServiceProviderRow>> SaveHrAsync(Guid staffId, int? version, HrInput i, CancellationToken ct)
    {
        DateOnly? hire = null, end = null;
        if (i.HireDate is { Length: > 0 } hd && !DateOnly.TryParse(hd, out var h)) return Edit<SpaServiceProviderRow>.Refused(EditOutcome.Invalid, "hireDate is yyyy-MM-dd.");
        else if (i.HireDate is { Length: > 0 }) hire = DateOnly.Parse(i.HireDate);
        if (i.EndDate is { Length: > 0 } ed && !DateOnly.TryParse(ed, out _)) return Edit<SpaServiceProviderRow>.Refused(EditOutcome.Invalid, "endDate is yyyy-MM-dd.");
        else if (i.EndDate is { Length: > 0 }) end = DateOnly.Parse(i.EndDate);

        void Apply(SpaServiceProviderRow r)
        {
            if (i.EmployeeNumber is not null) r.EmployeeNumber = i.EmployeeNumber.Trim();
            if (i.FirstName is not null) r.FirstName = i.FirstName.Trim();
            if (i.MiddleName is not null) r.MiddleName = i.MiddleName.Length == 0 ? null : i.MiddleName;
            if (i.LastName is not null) r.LastName = i.LastName.Trim();
            if (i.WorkEmail is not null) r.WorkEmail = i.WorkEmail.Length == 0 ? null : i.WorkEmail;
            if (i.PersonalEmail is not null) r.PersonalEmail = i.PersonalEmail.Length == 0 ? null : i.PersonalEmail;
            if (i.MobilePhone is not null) r.MobilePhone = i.MobilePhone.Length == 0 ? null : i.MobilePhone;
            if (i.WorkerType is not null) r.WorkerType = i.WorkerType;
            if (i.JobTitle is not null) r.JobTitle = i.JobTitle.Trim();
            if (i.HireDate is not null) r.HireDate = hire;
            if (i.EndDate is not null) r.EndDate = end;
            if (i.Languages is not null) r.Languages = i.Languages;
            if (i.PublicBio is not null) r.PublicBio = i.PublicBio.Length == 0 ? null : i.PublicBio;
            if (i.PublicProfileConsent is { } c) r.PublicProfileConsent = c;
        }

        if (version is null)
        {
            if (i.EmployeeNumber is null || i.FirstName is null || i.LastName is null || i.WorkerType is null || i.JobTitle is null)
                return Edit<SpaServiceProviderRow>.Refused(EditOutcome.Invalid, "employeeNumber, firstName, lastName, workerType and jobTitle are required.");
            var row = new SpaServiceProviderRow { SpaServiceProviderId = Uuid7.New(), StaffId = staffId };
            Apply(row);
            return await master.CreateAsync(row, "workforce.hr.create", "spa_service_provider", r => r.SpaServiceProviderId, ct, after: HrAudit);
        }
        return await master.ChangeAsync<SpaServiceProviderRow>(r => r.StaffId == staffId, version.Value, "workforce.hr.update", "spa_service_provider",
            r => r.SpaServiceProviderId, r => { Apply(r); return null; }, ct, after: HrAudit);
    }

    /* -------------------------------- roles --------------------------------- */

    public Task<List<StaffRoleAssignmentRow>> RolesAsync(Guid staffId, CancellationToken ct) =>
        db.Set<StaffRoleAssignmentRow>().AsNoTracking().Where(r => r.StaffId == staffId).OrderByDescending(r => r.EffectiveFrom).ToListAsync(ct);

    public Task<Edit<StaffRoleAssignmentRow>> ProposeRoleAsync(Guid staffId, string role, Guid? propertyId, CancellationToken ct) =>
        master.CreateAsync(new StaffRoleAssignmentRow
        {
            StaffRoleAssignmentId = Uuid7.New(), StaffId = staffId, RoleCode = role, PropertyId = propertyId, Status = "Proposed", EffectiveFrom = clock.UtcNow,
        }, "workforce.role.propose", "staff_role_assignment", r => r.StaffRoleAssignmentId, ct,
            async () => await db.Set<StaffRow>().AnyAsync(s => s.StaffId == staffId, ct) ? null : (string?)"No such staff member.");

    public enum RoleOutcome { Ok, NotFound, StaleVersion, Illegal, SameApprover, NoSignIn }

    public async Task<(RoleOutcome Outcome, StaffRoleAssignmentRow? Row, string? Detail)> DecideRoleAsync(Guid id, int version, string to, string? reason, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var row = await db.Set<StaffRoleAssignmentRow>().SingleOrDefaultAsync(r => r.StaffRoleAssignmentId == id, ct);
        if (row is null) return (RoleOutcome.NotFound, null, null);
        if (row.Version != version) { db.ChangeTracker.Clear(); return (RoleOutcome.StaleVersion, row, null); }
        var allowed = (row.Status, to) switch
        {
            ("Proposed", "Active") or ("Proposed", "Revoked") or ("Active", "Revoked") => true,
            _ => false,
        };
        if (!allowed) { db.ChangeTracker.Clear(); return (RoleOutcome.Illegal, row, $"A {row.Status} assignment cannot become {to}."); }
        var principal = await db.Set<StaffRow>().Where(s => s.StaffId == row.StaffId).Select(s => s.PrincipalId).SingleAsync(ct);
        if (to == "Active")
        {
            if (row.CreatedBy is { } proposer && proposer == db.Scope.PrincipalId)
            { db.ChangeTracker.Clear(); return (RoleOutcome.SameApprover, row, "The person who proposed a role cannot approve it (SEC-014)."); }
            if (principal is null) { db.ChangeTracker.Clear(); return (RoleOutcome.NoSignIn, row, "This person has no sign-in yet; a role needs one."); }
            row.ApprovedBy = db.Scope.PrincipalId;
            row.ApprovedAt = clock.UtcNow;
        }
        var from = row.Status;
        row.Status = to;
        if (to == "Revoked") row.EffectiveTo = clock.UtcNow > row.EffectiveFrom ? clock.UtcNow : row.EffectiveFrom.AddSeconds(1);
        row.FgaSyncedAt = null;
        await db.SaveChangesAsync(ct);
        if (principal is { } p && (to == "Active" || from == "Active"))
            outbox.Enqueue(new OutboxEvent(EventTypes.RoleAssignmentChanged, "staff_role_assignment", row.StaffRoleAssignmentId, row.Version,
                new { assignmentId = row.StaffRoleAssignmentId, principalId = p, roleCode = row.RoleCode, propertyId = row.PropertyId, status = row.Status }));
        await master.AuditAsync(new AuditEntry($"workforce.role.{(to == "Active" ? "approve" : "revoke")}", "staff_role_assignment",
            row.StaffRoleAssignmentId.ToString(), row.Version, FromStatus: from, ToStatus: to, ReasonText: reason,
            AfterData: new { row.StaffId, row.RoleCode, row.PropertyId }), ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return (RoleOutcome.Ok, row, null);
    }

    /* ---------------------------- qualifications ---------------------------- */

    public Task<List<StaffQualificationRow>> QualificationsAsync(Guid staffId, CancellationToken ct) =>
        db.Set<StaffQualificationRow>().AsNoTracking().Where(q => q.StaffId == staffId).ToListAsync(ct);

    public Task<Edit<StaffQualificationRow>> GrantAsync(Guid staffId, Guid serviceId, Guid? credentialId, CancellationToken ct) =>
        master.CreateAsync(new StaffQualificationRow
        {
            QualificationId = Uuid7.New(), StaffId = staffId, ServiceId = serviceId, CredentialId = credentialId,
            EffectiveRange = new NpgsqlRange<DateTime>(clock.UtcNow.UtcDateTime, true, false, default, false, true),
            GrantedBy = db.Scope.PrincipalId, Status = "Active",
        }, "workforce.qualification.grant", "staff_qualification", q => q.QualificationId, ct, async () =>
        {
            if (!await db.Set<StaffRow>().AnyAsync(s => s.StaffId == staffId, ct)) return "No such staff member.";
            var service = await db.Set<ServiceRow>().AsNoTracking().Where(s => s.ServiceId == serviceId)
                .Select(s => new { s.RequiredLicenseTypeCodes }).SingleOrDefaultAsync(ct);
            if (service is null) return "No such service.";
            // CON-003: a licensed service needs a verified credential of each type it names.
            foreach (var type in service.RequiredLicenseTypeCodes)
                if (!await db.Set<CredentialRow>().AnyAsync(c => c.StaffId == staffId && c.LicenseTypeCode == type && c.Status == "Verified"
                                                                 && (c.ExpiresAt == null || c.ExpiresAt >= DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)), ct))
                    return $"The service needs a verified {type} credential first.";
            return null;
        });

    public Task<Edit<StaffQualificationRow>> RevokeQualificationAsync(Guid id, int version, string? reason, CancellationToken ct) =>
        master.ChangeAsync<StaffQualificationRow>(q => q.QualificationId == id, version, "workforce.qualification.revoke", "staff_qualification",
            q => q.QualificationId, q =>
            {
                if (q.Status != "Active") return $"A {q.Status} qualification is not active.";
                q.Status = "Revoked";
                return null;
            }, ct, reason);

    /* ----------------------------- credentials ------------------------------ */

    public Task<List<CredentialRow>> CredentialsAsync(Guid staffId, CancellationToken ct) =>
        db.Set<CredentialRow>().AsNoTracking().Where(c => c.StaffId == staffId).OrderBy(c => c.ExpiresAt).ToListAsync(ct);

    private static object CredentialAudit(CredentialRow c) =>
        new { c.StaffId, c.CredentialKind, c.LicenseTypeCode, c.Jurisdiction, c.NumberLast4, c.ExpiresAt, c.Status };

    public Task<Edit<CredentialRow>> AddCredentialAsync(Guid staffId, CredentialInput i, DateOnly? issued, DateOnly? expires, CancellationToken ct)
    {
        var row = new CredentialRow
        {
            CredentialId = Uuid7.New(), StaffId = staffId, CredentialKind = i.CredentialKind!, LicenseTypeCode = i.LicenseTypeCode,
            Jurisdiction = i.Jurisdiction, IssuerName = i.IssuerName, IssuedAt = issued, ExpiresAt = expires, Restrictions = i.Restrictions, Status = "Pending",
        };
        if (!string.IsNullOrWhiteSpace(i.Number))
        {
            var number = i.Number.Trim();
            var sealed_ = protector.Protect(System.Text.Encoding.UTF8.GetBytes(number), $"credential:{row.CredentialId}");
            row.NumberCipher = sealed_.Cipher;
            row.NumberKeyVersion = sealed_.KeyVersion;
            row.NumberLast4 = number.Length >= 4 ? number[^4..] : number.PadLeft(4, '0');
        }
        return master.CreateAsync(row, "workforce.credential.create", "credential", c => c.CredentialId, ct,
            async () => await db.Set<StaffRow>().AnyAsync(s => s.StaffId == staffId, ct) ? null : (string?)"No such staff member.", CredentialAudit);
    }

    public Task<Edit<CredentialRow>> VerifyCredentialAsync(Guid id, int version, bool verify, string? reason, CancellationToken ct) =>
        master.ChangeAsync<CredentialRow>(c => c.CredentialId == id, version, verify ? "workforce.credential.verify" : "workforce.credential.reject",
            "credential", c => c.CredentialId, c =>
            {
                if (c.Status != "Pending") return $"A {c.Status} credential has already been decided.";
                if (verify && c.ExpiresAt is { } e && e < DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)) return "That credential has already expired.";
                c.Status = verify ? "Verified" : "Rejected";
                if (verify) { c.VerifiedBy = db.Scope.PrincipalId; c.VerifiedAt = clock.UtcNow; }
                return null;
            }, ct, reason, CredentialAudit);

    /// <summary>
    /// Expired credentials stop qualifying: the credential becomes Expired and
    /// every qualification granted on it is Expired with it.
    /// </summary>
    public async Task<int> ExpireCredentialsAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var lapsed = await db.Set<CredentialRow>().Where(c => c.Status == "Verified" && c.ExpiresAt != null && c.ExpiresAt < today).ToListAsync(ct);
        if (lapsed.Count == 0) return 0;
        var ids = lapsed.Select(c => c.CredentialId).ToList();
        foreach (var c in lapsed) c.Status = "Expired";
        var quals = await db.Set<StaffQualificationRow>().Where(q => q.CredentialId != null && ids.Contains(q.CredentialId.Value) && q.Status == "Active").ToListAsync(ct);
        foreach (var q in quals) q.Status = "Expired";
        await db.SaveChangesAsync(ct);
        foreach (var c in lapsed)
            await master.AuditAsync(new AuditEntry("workforce.credential.expire", "credential", c.CredentialId.ToString(), c.Version,
                FromStatus: "Verified", ToStatus: "Expired", AfterData: CredentialAudit(c)), ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        return lapsed.Count;
    }

    /* -------------------------------- roster -------------------------------- */

    public Task<List<WorkScheduleRow>> RosterAsync(DateTimeOffset from, DateTimeOffset to, Guid? staffId, CancellationToken ct) =>
        db.Set<WorkScheduleRow>().AsNoTracking()
            .Where(w => w.StartsAt < to && from < w.EndsAt && (staffId == null || w.StaffId == staffId))
            .OrderBy(w => w.StartsAt).ToListAsync(ct);

    public Task<Edit<WorkScheduleRow>> AddEntryAsync(Guid staffId, string type, string? leaveType, DateTimeOffset starts, DateTimeOffset ends, CancellationToken ct) =>
        master.CreateAsync(new WorkScheduleRow
        {
            WorkScheduleId = Uuid7.New(), PropertyId = db.Scope.RequireProperty(), StaffId = staffId, EntryType = type, LeaveType = leaveType,
            StartsAt = starts, EndsAt = ends, Status = type == "Leave" ? "Requested" : "Draft",
        }, "workforce.roster.add", "work_schedule", w => w.WorkScheduleId, ct,
            async () => await db.Set<StaffRow>().AnyAsync(s => s.StaffId == staffId, ct) ? null : (string?)"No such staff member.");

    /// <summary>Draft → Published (a shift), Requested → Approved/Rejected (leave), or Cancelled.</summary>
    public Task<Edit<WorkScheduleRow>> MoveEntryAsync(Guid id, int version, string to, string? reason, CancellationToken ct) =>
        master.ChangeAsync<WorkScheduleRow>(w => w.WorkScheduleId == id, version, $"workforce.roster.{to.ToLowerInvariant()}", "work_schedule",
            w => w.WorkScheduleId, w =>
            {
                var ok = (w.EntryType, w.Status, to) switch
                {
                    ("Shift" or "OnCall", "Draft", "Published") => true,
                    ("Leave", "Requested", "Approved" or "Rejected") => true,
                    (_, "Draft" or "Published" or "Requested" or "Approved", "Cancelled") => true,
                    _ => false,
                };
                if (!ok) return $"A {w.Status} {w.EntryType.ToLowerInvariant()} cannot become {to}.";
                if (to is "Approved" or "Rejected" && w.CreatedBy is { } asker && asker == db.Scope.PrincipalId)
                    return "Leave is decided by someone other than the person who entered it.";
                w.Status = to;
                if (to == "Approved") { w.ApprovedBy = db.Scope.PrincipalId; w.ApprovedAt = clock.UtcNow; }
                return null;
            }, ct, reason);
}

public sealed class CredentialExpiryJob(WorkforceService workforce) : IPropertyJob
{
    public string Name => "workforce.credential-expiry";
    public TimeSpan Interval => TimeSpan.FromHours(6);
    public Task<int> RunAsync(CancellationToken ct) => workforce.ExpireCredentialsAsync(ct);
}

public static class WorkforceEndpoints
{
    private static object StaffDto(StaffRow s, IReadOnlyList<StaffRoleAssignmentRow>? roles = null, IReadOnlyList<Guid>? services = null) => new
    {
        staffId = s.StaffId, s.PreferredName, s.PrincipalId, s.HomePropertyId, s.DepartmentCode, s.EmploymentStatus, s.Bookable,
        hasSignIn = s.PrincipalId != null,
        roles = roles?.Select(Role), qualifiedServiceIds = services, rowVersion = s.Version, eTag = $"\"{s.Version}\"",
    };

    private static object Role(StaffRoleAssignmentRow r) => new
    {
        assignmentId = r.StaffRoleAssignmentId, r.StaffId, r.RoleCode, r.PropertyId, tenantWide = r.PropertyId == null, r.Status,
        proposedBy = r.CreatedBy, r.ApprovedBy, synced = r.FgaSyncedAt != null, effectiveFrom = r.EffectiveFrom.ToUniversalTime().ToString("O"),
        rowVersion = r.Version, eTag = $"\"{r.Version}\"",
    };

    private static object Hr(SpaServiceProviderRow h, bool full) => new
    {
        h.StaffId, h.EmployeeNumber, h.FirstName, h.MiddleName, h.LastName, h.WorkEmail,
        personalEmail = full ? h.PersonalEmail : null, mobilePhone = full ? h.MobilePhone : null,
        h.WorkerType, h.JobTitle, hireDate = h.HireDate?.ToString("yyyy-MM-dd"), endDate = h.EndDate?.ToString("yyyy-MM-dd"),
        h.Languages, h.PublicBio, h.PublicProfileConsent, rowVersion = h.Version, eTag = $"\"{h.Version}\"",
    };

    private static object Qualification(StaffQualificationRow q) => new
    {
        qualificationId = q.QualificationId, q.StaffId, q.ServiceId, q.CredentialId, q.Status, q.GrantedBy, rowVersion = q.Version, eTag = $"\"{q.Version}\"",
    };

    private static object Credential(CredentialRow c) => new
    {
        credentialId = c.CredentialId, c.StaffId, c.CredentialKind, c.LicenseTypeCode, c.Jurisdiction, c.IssuerName,
        numberMasked = c.NumberLast4 is null ? null : $"•••• {c.NumberLast4}",
        issuedAt = c.IssuedAt?.ToString("yyyy-MM-dd"), expiresAt = c.ExpiresAt?.ToString("yyyy-MM-dd"), c.Restrictions, c.Status,
        c.VerifiedBy, verifiedUtc = c.VerifiedAt?.ToUniversalTime().ToString("O"), rowVersion = c.Version, eTag = $"\"{c.Version}\"",
    };

    private static object Entry(WorkScheduleRow w) => new
    {
        workScheduleId = w.WorkScheduleId, w.StaffId, w.EntryType, w.LeaveType, startsUtc = w.StartsAt.ToUniversalTime().ToString("O"),
        endsUtc = w.EndsAt.ToUniversalTime().ToString("O"), w.Status, requestedBy = w.CreatedBy, w.ApprovedBy, rowVersion = w.Version, eTag = $"\"{w.Version}\"",
    };

    /// <summary>The staff record object, with the facts OpenFGA needs passed in: its tenant and its subject.</summary>
    private static (string Object, IReadOnlyList<FgaTuple> Contextual) Record(RequestContext ctx, StaffRow s)
    {
        var key = s.PrincipalId ?? s.StaffId;
        var obj = Fga.StaffRecord(key);
        var tuples = new List<FgaTuple> { new(Fga.Tenant(ctx.TenantId), "tenant", obj) };
        if (s.PrincipalId is { } p) tuples.Add(new FgaTuple(Fga.User(p), "subject", obj));
        return (obj, tuples);
    }

    private static Task<IResult?> ManageStaff(RequestContext ctx, IAccessDecider access, CancellationToken ct) =>
        WebApi.GateAsync(ctx, access, SpaScopes.WorkforceRead, "can_manage_staff", Fga.Tenant(ctx.TenantId), ct);

    private static IResult RoleResult(HttpContext http, RequestContext ctx, (WorkforceService.RoleOutcome Outcome, StaffRoleAssignmentRow? Row, string? Detail) r) => r.Outcome switch
    {
        WorkforceService.RoleOutcome.Ok => EditResults.Ok(http, r.Row!, Role),
        WorkforceService.RoleOutcome.NotFound => WebApi.NotFound(ctx),
        WorkforceService.RoleOutcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, extensions: Problem.Ext("current", Role(r.Row!))),
        WorkforceService.RoleOutcome.SameApprover => Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, r.Detail),
        _ => Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail),
    };

    private static bool TryDate(string? raw, out DateOnly? value)
    {
        value = null;
        if (string.IsNullOrEmpty(raw)) return true;
        if (!DateOnly.TryParse(raw, out var d)) return false;
        value = d;
        return true;
    }

    public static IEndpointRouteBuilder MapWorkforce(this IEndpointRouteBuilder app)
    {
        /* operational profile */

        app.MapGet("/staff", async (HttpContext http, WorkforceService svc, bool? includeTerminated, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await svc.ListAsync(includeTerminated == true, ct)).Select(v => StaffDto(v.Staff, v.Roles, v.QualifiedServiceIds)), Json.Options);
        });

        app.MapPost("/staff", async (HttpContext http, WorkforceService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await ManageStaff(ctx, access, ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<StaffInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (string.IsNullOrWhiteSpace(i.PreferredName) || i.PreferredName.Length > 80) return WebApi.Invalid(ctx, "preferredName is 1–80 characters.");
            return (await svc.CreateAsync(i, ct)).ToHttp(http, ctx, s => StaffDto(s), 201);
        });

        app.MapPatch("/staff/{id:guid}", async (HttpContext http, WorkforceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await ManageStaff(ctx, access, ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<StaffInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.EmploymentStatus is not (null or "Pending" or "Active" or "OnLeave" or "Suspended" or "Terminated"))
                return WebApi.Invalid(ctx, "employmentStatus is Pending, Active, OnLeave, Suspended or Terminated.");
            return (await svc.UpdateAsync(id, version, i, ct)).ToHttp(http, ctx, s => StaffDto(s));
        });

        /* HR profile (SEC-007) */

        app.MapGet("/staff/{id:guid}/hr", async (HttpContext http, WorkforceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await svc.StaffAsync(id, ct) is not { } s) return WebApi.NotFound(ctx);
            // A person reads their own file with ordinary access; anyone else needs the workforce scope and HR's right.
            if (Guard.RequireScope(ctx, s.PrincipalId == ctx.PrincipalId ? SpaScopes.Read : SpaScopes.WorkforceRead) is { } denied) return denied;
            var (obj, contextual) = Record(ctx, s);
            if (await Guard.RequireAccessAsync(ctx, access, "can_read_hr_profile", obj, contextual, ct: ct) is { } refused) return refused;
            var hr = await svc.HrAsync(id, ct);
            return hr is null ? WebApi.NotFound(ctx) : EditResults.Ok(http, hr, h => Hr(h, full: true));
        });

        app.MapPut("/staff/{id:guid}/hr", async (HttpContext http, WorkforceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.WorkforceRead) is { } denied) return denied;
            if (await svc.StaffAsync(id, ct) is not { } s) return WebApi.NotFound(ctx);
            var (obj, contextual) = Record(ctx, s);
            if (await Guard.RequireAccessAsync(ctx, access, "can_update_hr_profile", obj, contextual, ct: ct) is { } refused) return refused;
            int? version = null;
            if (http.Request.Headers.IfMatch.Count > 0)
            {
                if (Guard.RequireIfMatch(http, ctx, out var v) is { } noMatch) return noMatch;
                version = v;
            }
            var (i, fail) = await WebApi.BodyAsync<HrInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.WorkerType is not (null or "Employee" or "Contractor" or "Agency")) return WebApi.Invalid(ctx, "workerType is Employee, Contractor or Agency.");
            return (await svc.SaveHrAsync(id, version, i, ct)).ToHttp(http, ctx, h => Hr(h, full: true), version is null ? 201 : 200);
        });

        /* roles (SEC-014) */

        app.MapGet("/staff/{id:guid}/roles", async (HttpContext http, WorkforceService svc, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await svc.RolesAsync(id, ct)).Select(Role), Json.Options);
        });

        app.MapPost("/staff/{id:guid}/roles", async (HttpContext http, WorkforceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_propose_role", Fga.Tenant(ctx.TenantId), ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<RoleInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.RoleCode is null || !WorkforceService.Roles.Contains(i.RoleCode)) return WebApi.Invalid(ctx, "roleCode is one of the SpMS roles.");
            Guid? property = i.TenantWide == true ? null : i.PropertyId ?? ctx.PropertyId;
            if (property is { } p && !ctx.PropertyIds.Contains(p)) return WebApi.Invalid(ctx, "propertyId is not one of your properties.");
            return (await svc.ProposeRoleAsync(id, i.RoleCode, property, ct)).ToHttp(http, ctx, Role, 201);
        });

        app.MapPost("/role-assignments/{id:guid}/approve", async (HttpContext http, WorkforceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_approve_role", Fga.Tenant(ctx.TenantId), ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            return RoleResult(http, ctx, await svc.DecideRoleAsync(id, version, "Active", null, ct));
        });

        app.MapPost("/role-assignments/{id:guid}/revoke", async (HttpContext http, WorkforceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_propose_role", Fga.Tenant(ctx.TenantId), ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<ReasonInput>(http, ctx, ct);
            if (i is null) return fail!;
            return RoleResult(http, ctx, await svc.DecideRoleAsync(id, version, "Revoked", i.Reason, ct));
        });

        /* qualifications (CON-003) */

        app.MapGet("/staff/{id:guid}/qualifications", async (HttpContext http, WorkforceService svc, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await svc.QualificationsAsync(id, ct)).Select(Qualification), Json.Options);
        });

        app.MapPost("/staff/{id:guid}/qualifications", async (HttpContext http, WorkforceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.WorkforceRead, "can_manage_qualifications", Fga.Property(ctx.PropertyId), ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<QualificationInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.ServiceId is not { } service) return WebApi.Invalid(ctx, "serviceId is required.");
            return (await svc.GrantAsync(id, service, i.CredentialId, ct)).ToHttp(http, ctx, Qualification, 201);
        });

        app.MapPost("/qualifications/{id:guid}/revoke", async (HttpContext http, WorkforceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.WorkforceRead, "can_manage_qualifications", Fga.Property(ctx.PropertyId), ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<ReasonInput>(http, ctx, ct);
            if (i is null) return fail!;
            return (await svc.RevokeQualificationAsync(id, version, i.Reason, ct)).ToHttp(http, ctx, Qualification);
        });

        /* credentials */

        app.MapGet("/staff/{id:guid}/credentials", async (HttpContext http, WorkforceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await svc.StaffAsync(id, ct) is not { } s) return WebApi.NotFound(ctx);
            // A person reads their own file with ordinary access; anyone else needs the workforce scope and HR's right.
            if (Guard.RequireScope(ctx, s.PrincipalId == ctx.PrincipalId ? SpaScopes.Read : SpaScopes.WorkforceRead) is { } denied) return denied;
            var (obj, contextual) = Record(ctx, s);
            if (await Guard.RequireAccessAsync(ctx, access, "can_read_hr_profile", obj, contextual, ct: ct) is { } refused) return refused;
            return Results.Json((await svc.CredentialsAsync(id, ct)).Select(Credential), Json.Options);
        });

        app.MapPost("/staff/{id:guid}/credentials", async (HttpContext http, WorkforceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.WorkforceRead) is { } denied) return denied;
            if (await svc.StaffAsync(id, ct) is not { } s) return WebApi.NotFound(ctx);
            var (obj, contextual) = Record(ctx, s);
            if (await Guard.RequireAccessAsync(ctx, access, "can_update_hr_profile", obj, contextual, ct: ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<CredentialInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.CredentialKind is not ("License" or "Certification" or "Training" or "BackgroundCheck"))
                return WebApi.Invalid(ctx, "credentialKind is License, Certification, Training or BackgroundCheck.");
            if (i.CredentialKind is "License" or "Certification" && string.IsNullOrWhiteSpace(i.LicenseTypeCode))
                return WebApi.Invalid(ctx, "A license or certification names its licenseTypeCode.");
            if (!TryDate(i.IssuedAt, out var issued) || !TryDate(i.ExpiresAt, out var expires)) return WebApi.Invalid(ctx, "Dates are yyyy-MM-dd.");
            return (await svc.AddCredentialAsync(id, i, issued, expires, ct)).ToHttp(http, ctx, Credential, 201);
        });

        foreach (var (path, verify) in new[] { ("verify", true), ("reject", false) })
            app.MapPost($"/credentials/{{id:guid}}/{path}", async (HttpContext http, WorkforceService svc, IAccessDecider access, SpmsDbContext db, Guid id, CancellationToken ct) =>
            {
                var ctx = RequestContext.From(http);
                if (Guard.RequireScope(ctx, SpaScopes.WorkforceRead) is { } denied) return denied;
                var staff = await (from c in db.Set<CredentialRow>().AsNoTracking() where c.CredentialId == id
                                   join st in db.Set<StaffRow>() on c.StaffId equals st.StaffId select st).SingleOrDefaultAsync(ct);
                if (staff is null) return WebApi.NotFound(ctx);
                var (obj, contextual) = Record(ctx, staff);
                if (await Guard.RequireAccessAsync(ctx, access, "can_verify_credentials", obj, contextual, ct: ct) is { } refused) return refused;
                if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
                var (i, fail) = await WebApi.BodyAsync<ReasonInput>(http, ctx, ct);
                if (i is null) return fail!;
                return (await svc.VerifyCredentialAsync(id, version, verify, i.Reason, ct)).ToHttp(http, ctx, Credential);
            });

        /* roster */

        app.MapGet("/roster", async (HttpContext http, WorkforceService svc, string? from, string? to, Guid? staffId, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            if (!Guard.TryParseInstant(from, out var f) || !Guard.TryParseInstant(to, out var t) || t <= f || t - f > TimeSpan.FromDays(62))
                return WebApi.Invalid(ctx, "from and to are ISO 8601 instants, at most 62 days apart.");
            return Results.Json((await svc.RosterAsync(f, t, staffId, ct)).Select(Entry), Json.Options);
        });

        app.MapPost("/roster", async (HttpContext http, WorkforceService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            var (i, fail) = await WebApi.BodyAsync<RosterInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.StaffId is not { } staff || i.EntryType is not ("Shift" or "OnCall" or "Leave")) return WebApi.Invalid(ctx, "staffId and entryType (Shift, OnCall, Leave) are required.");
            // Anyone may ask for their own leave; shifts, and leave for someone else, are the scheduler's.
            var self = i.EntryType == "Leave" && (await svc.StaffAsync(staff, ct))?.PrincipalId is { } p && p == ctx.PrincipalId;
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Read, self ? null : "can_manage_roster", Fga.Property(ctx.PropertyId), ct) is { } refused) return refused;
            if ((i.EntryType == "Leave") != (i.LeaveType is "Planned" or "Unplanned" or "Training" or "Other"))
                return WebApi.Invalid(ctx, "Leave carries a leaveType (Planned, Unplanned, Training, Other); a shift does not.");
            if (!Guard.TryParseInstant(i.StartsUtc, out var starts) || !Guard.TryParseInstant(i.EndsUtc, out var ends) || ends <= starts || ends - starts > TimeSpan.FromDays(31))
                return WebApi.Invalid(ctx, "startsUtc and endsUtc are ISO 8601 instants, end after start, at most 31 days.");
            return (await svc.AddEntryAsync(staff, i.EntryType, i.LeaveType, starts, ends, ct)).ToHttp(http, ctx, Entry, 201);
        });

        foreach (var (path, to, relation) in new[] { ("publish", "Published", "can_manage_roster"), ("approve", "Approved", "can_approve_leave"),
                                                     ("reject", "Rejected", "can_approve_leave"), ("cancel", "Cancelled", "can_manage_roster") })
            app.MapPost($"/roster/{{id:guid}}/{path}", async (HttpContext http, WorkforceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
            {
                var ctx = RequestContext.From(http);
                if (await WebApi.GateAsync(ctx, access, SpaScopes.Read, relation, Fga.Property(ctx.PropertyId), ct) is { } refused) return refused;
                if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
                var (i, fail) = await WebApi.BodyAsync<ReasonInput>(http, ctx, ct);
                if (i is null) return fail!;
                return (await svc.MoveEntryAsync(id, version, to, i.Reason, ct)).ToHttp(http, ctx, Entry);
            });

        return app;
    }
}
