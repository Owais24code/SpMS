using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spms.Domain.Audit;
using Spms.Domain.Concurrency;
using Spms.Domain.Errors;
using Spms.Domain.Permissions;
using Spms.Domain.Scheduling;

namespace Spms.Application;

public sealed record CreateAppointment(
    string GuestAlias,
    string ServiceCode,
    string ProviderId,
    string RoomId,
    DateTimeOffset StartUtc,
    int DurationMinutes);

public sealed class AppointmentService(
    IAppointmentRepository repo,
    PreflightService preflight,
    IAuditSink audit,
    IClock clock)
{
    public Appointment? Get(Guid id) => repo.Get(id);

    public IReadOnlyList<Appointment> ForDay(string propertyId, DateOnly day) =>
        repo.ForProperty(propertyId, day);

    // ---------------------------------------------------------------- create

    public WriteOutcome<Appointment> Create(CreateAppointment cmd, CallerContext caller)
    {
        if (!caller.Has(SpmsScopes.Write))
            return new WriteOutcome<Appointment>.Rejected(SpmsProblem.AuthorizationDenied);

        if (cmd.DurationMinutes <= 0)
            return new WriteOutcome<Appointment>.Rejected(
                SpmsProblem.ValidationFailed, "durationMinutes must be greater than zero");

        var proposal = new MoveProposal(
            Guid.NewGuid(), cmd.ProviderId, cmd.RoomId, cmd.StartUtc, cmd.DurationMinutes, 0);

        var check = preflight.Evaluate(proposal, caller);
        if (check.Conflicts.Count > 0)
            return new WriteOutcome<Appointment>.Conflicted(check.Conflicts);

        var appointment = new Appointment
        {
            AppointmentId = proposal.AppointmentId,
            TenantId = caller.TenantId,
            PropertyId = caller.PropertyId,
            GuestAlias = cmd.GuestAlias,
            ServiceCode = cmd.ServiceCode,
            ProviderId = cmd.ProviderId,
            RoomId = cmd.RoomId,
            StartUtc = cmd.StartUtc,
            DurationMinutes = cmd.DurationMinutes,
            Status = AppointmentStatus.Confirmed,
            RowVersion = 1,
        };

        repo.Upsert(appointment);
        Audit("appointment.create", appointment, caller, null, appointment);
        return new WriteOutcome<Appointment>.Committed(appointment, appointment.RowVersion);
    }

    // ------------------------------------------------------------------ move

    /// <summary>
    /// Applies a previously preflighted move.
    ///
    /// Three gates, in order: the token must be live, the record must not have
    /// moved on, and a soft conflict must carry a reason. A hard conflict is
    /// refused regardless of role — CON-003.
    /// </summary>
    public WriteOutcome<Appointment> CommitMove(
        string token, int ifMatchVersion, string? overrideReason, CallerContext caller)
    {
        if (!caller.Has(SpmsScopes.Schedule))
            return new WriteOutcome<Appointment>.Rejected(SpmsProblem.AuthorizationDenied);

        var pf = preflight.Redeem(token);
        if (pf is null)
            return new WriteOutcome<Appointment>.Rejected(SpmsProblem.PreflightExpired);

        var current = repo.Get(pf.Proposal.AppointmentId);
        if (current is null)
            return new WriteOutcome<Appointment>.Rejected(SpmsProblem.NotFound);

        if (current.RowVersion != ifMatchVersion)
            return new WriteOutcome<Appointment>.Stale(current, current.RowVersion);

        if (pf.Conflicts.Any(c => !c.Overridable))
            return new WriteOutcome<Appointment>.Conflicted(pf.Conflicts);

        if (pf.Conflicts.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(overrideReason) || overrideReason.Trim().Length < 4)
                return new WriteOutcome<Appointment>.Conflicted(pf.Conflicts);

            if (!caller.Has(SpmsScopes.Schedule))
                return new WriteOutcome<Appointment>.Rejected(SpmsProblem.AuthorizationDenied);
        }

        var moved = current with
        {
            ProviderId = pf.Proposal.ProviderId,
            RoomId = pf.Proposal.RoomId,
            StartUtc = pf.Proposal.StartUtc,
            DurationMinutes = pf.Proposal.DurationMinutes,
            RowVersion = current.RowVersion + 1,
        };

        repo.Upsert(moved);
        Audit("appointment.move", moved, caller, current, moved,
            code: pf.Conflicts.FirstOrDefault()?.Code,
            detail: overrideReason);

        return new WriteOutcome<Appointment>.Committed(moved, moved.RowVersion);
    }

    // ------------------------------------------------------------ transition

    public WriteOutcome<Appointment> Transition(
        Guid id, AppointmentStatus to, int ifMatchVersion, CallerContext caller)
    {
        if (!caller.Has(SpmsScopes.Write))
            return new WriteOutcome<Appointment>.Rejected(SpmsProblem.AuthorizationDenied);

        var current = repo.Get(id);
        if (current is null)
            return new WriteOutcome<Appointment>.Rejected(SpmsProblem.NotFound);

        if (current.RowVersion != ifMatchVersion)
            return new WriteOutcome<Appointment>.Stale(current, current.RowVersion);

        if (!AppointmentTransitions.CanMove(current.Status, to))
            return new WriteOutcome<Appointment>.Rejected(
                SpmsProblem.ValidationFailed,
                $"{current.Status} cannot move to {to}. Allowed: " +
                string.Join(", ", AppointmentTransitions.NextFrom(current.Status)));

        var next = current with { Status = to, RowVersion = current.RowVersion + 1 };
        repo.Upsert(next);
        Audit($"appointment.transition.{to}", next, caller, current, next);
        return new WriteOutcome<Appointment>.Committed(next, next.RowVersion);
    }

    // ----------------------------------------------------------------- audit

    private void Audit(
        string action, Appointment subject, CallerContext caller,
        Appointment? before, Appointment? after, string? code = null, string? detail = null)
    {
        audit.Write(new AuditEvent
        {
            AuditId = Guid.NewGuid(),
            OccurredAtUtc = clock.UtcNow,
            TenantId = caller.TenantId,
            PropertyId = caller.PropertyId,
            Actor = caller.Subject,
            Action = action,
            SubjectType = "appointment",
            SubjectId = subject.AppointmentId.ToString(),
            SubjectVersion = subject.RowVersion,
            Purpose = detail is null ? caller.Purpose : $"{caller.Purpose}: {detail}",
            BeforeHash = Hash(before),
            AfterHash = Hash(after),
            Code = code,
            CorrelationId = caller.CorrelationId,
        });
    }

    /// <summary>
    /// Hash rather than store the row: the audit trail must prove a change
    /// happened without becoming a second copy of the data it describes.
    /// </summary>
    internal static string? Hash(Appointment? a)
    {
        if (a is null) return null;
        var json = JsonSerializer.Serialize(a);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32];
    }
}
