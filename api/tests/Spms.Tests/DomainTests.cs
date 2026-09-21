using Spms.Application;
using Spms.Domain.Concurrency;
using Spms.Domain.Errors;
using Spms.Domain.Permissions;
using Spms.Domain.Scheduling;
using Spms.Infrastructure;
using static Spms.Tests.Harness;

namespace Spms.Tests;

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

public static class DomainTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 21, 14, 0, 0, TimeSpan.Zero);

    private static CallerContext Caller(params string[] scopes) =>
        new("tester", "aarfid", "riverside", scopes.ToHashSet(), "corr-1");

    private static (AppointmentService svc, PreflightService pf,
                    InMemoryAppointmentRepository repo, InMemoryAuditSink audit, FixedClock clock) Build()
    {
        var repo = new InMemoryAppointmentRepository();
        var audit = new InMemoryAuditSink();
        var clock = new FixedClock(Base);
        var pf = new PreflightService(repo, clock);
        return (new AppointmentService(repo, pf, audit, clock), pf, repo, audit, clock);
    }

    private static Appointment Seed(InMemoryAppointmentRepository repo, string provider, string room,
        DateTimeOffset start, int minutes = 60, AppointmentStatus status = AppointmentStatus.Confirmed)
    {
        var a = new Appointment
        {
            AppointmentId = Guid.NewGuid(), TenantId = "aarfid", PropertyId = "riverside",
            GuestAlias = "Guest 1", ServiceCode = "DEEP90", ProviderId = provider, RoomId = room,
            StartUtc = start, DurationMinutes = minutes, Status = status, RowVersion = 1,
        };
        repo.Upsert(a);
        return a;
    }

    public static void Run()
    {
        Section("State machine");
        {
            Check("Confirmed can move to Checked In",
                AppointmentTransitions.CanMove(AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn));
            Check("Completed is terminal",
                !AppointmentTransitions.CanMove(AppointmentStatus.Completed, AppointmentStatus.InService));
            Check("Draft cannot jump straight to In Service",
                !AppointmentTransitions.CanMove(AppointmentStatus.Draft, AppointmentStatus.InService));
            Check("Cancelled is terminal",
                !AppointmentTransitions.CanMove(AppointmentStatus.Cancelled, AppointmentStatus.Confirmed));
        }

        Section("Authorization (defaultEffect deny)");
        {
            var (svc, _, _, _, _) = Build();
            var denied = svc.Create(new CreateAppointment("G", "S", "p1", "r1", Base, 60), Caller(SpmsScopes.Read));
            Check("create without spa.write is denied",
                denied is WriteOutcome<Appointment>.Rejected { Problem.Code: "AUTHORIZATION_DENIED" });

            var (svc2, pf2, repo2, _, _) = Build();
            var seeded = Seed(repo2, "p1", "r1", Base);
            var token = pf2.Evaluate(new MoveProposal(seeded.AppointmentId, "p2", "r2", Base.AddHours(3), 60, 1), Caller());
            var moveDenied = svc2.CommitMove(token.Token, 1, null, Caller(SpmsScopes.Read));
            Check("commit without spa.schedule is denied",
                moveDenied is WriteOutcome<Appointment>.Rejected { Problem.Code: "AUTHORIZATION_DENIED" });
        }

        Section("Conflict detection (CON register)");
        {
            var (_, pf, repo, _, _) = Build();
            Seed(repo, "lena", "suite-1", Base);
            var moving = Seed(repo, "marco", "room-2", Base.AddHours(5));

            var roomClash = pf.Evaluate(
                new MoveProposal(moving.AppointmentId, "marco", "suite-1", Base.AddMinutes(30), 60, 1), Caller());
            Check("room overlap raises CON-002", roomClash.Conflicts.Any(c => c.Code == "CON-002"));
            Check("CON-002 is not overridable", roomClash.Conflicts.First(c => c.Code == "CON-002").Overridable == false);
            Check("commit_allowed is false when a hard conflict exists", !roomClash.CommitAllowed);

            var providerClash = pf.Evaluate(
                new MoveProposal(moving.AppointmentId, "lena", "room-9", Base.AddMinutes(30), 60, 1), Caller());
            Check("provider overlap raises CON-001", providerClash.Conflicts.Any(c => c.Code == "CON-001"));
            Check("CON-001 is overridable", providerClash.Conflicts.First(c => c.Code == "CON-001").Overridable);

            var clear = pf.Evaluate(
                new MoveProposal(moving.AppointmentId, "priya", "room-9", Base.AddHours(6), 60, 1), Caller());
            Check("a clear window raises no conflicts", clear.Conflicts.Count == 0);
            Check("every conflict states financial impact",
                roomClash.Conflicts.All(c => !string.IsNullOrWhiteSpace(c.FinancialImpact)));
        }

        Section("Preflight tokens (SCH-020)");
        {
            var (svc, pf, repo, _, clock) = Build();
            var a = Seed(repo, "lena", "suite-1", Base);

            var token = pf.Evaluate(new MoveProposal(a.AppointmentId, "priya", "room-3", Base.AddHours(4), 60, 1), Caller());
            Check("preflight writes nothing", repo.Get(a.AppointmentId)!.StartUtc == Base);

            var expired = pf.Evaluate(new MoveProposal(a.AppointmentId, "priya", "room-3", Base.AddHours(4), 60, 1), Caller());
            clock.UtcNow = Base.Add(PreflightService.Ttl).AddSeconds(1);
            var afterExpiry = svc.CommitMove(expired.Token, 1, null, Caller(SpmsScopes.Schedule));
            Check("an expired token is refused",
                afterExpiry is WriteOutcome<Appointment>.Rejected { Problem.Code: "PREFLIGHT_EXPIRED" });

            clock.UtcNow = Base;
            var ok = svc.CommitMove(token.Token, 1, null, Caller(SpmsScopes.Schedule));
            Check("a live token commits", ok is WriteOutcome<Appointment>.Committed);
            Check("the move actually applied", repo.Get(a.AppointmentId)!.RoomId == "room-3");

            var replay = svc.CommitMove(token.Token, 2, null, Caller(SpmsScopes.Schedule));
            Check("a spent token cannot be replayed",
                replay is WriteOutcome<Appointment>.Rejected { Problem.Code: "PREFLIGHT_EXPIRED" });
        }

        Section("Optimistic concurrency (API-002)");
        {
            var (svc, pf, repo, _, _) = Build();
            var a = Seed(repo, "lena", "suite-1", Base);
            var token = pf.Evaluate(new MoveProposal(a.AppointmentId, "priya", "room-3", Base.AddHours(4), 60, 1), Caller());

            // Someone else edits it first.
            repo.Upsert(a with { RowVersion = 2 });

            var stale = svc.CommitMove(token.Token, 1, null, Caller(SpmsScopes.Schedule));
            Check("a stale If-Match is refused", stale is WriteOutcome<Appointment>.Stale);
            Check("the stale result carries the current version",
                stale is WriteOutcome<Appointment>.Stale { CurrentVersion: 2 });
            Check("the stale result carries the current record for merging",
                stale is WriteOutcome<Appointment>.Stale { Current: not null });
        }

        Section("Soft conflict needs a reason (CON-004)");
        {
            var (svc, pf, repo, audit, _) = Build();
            Seed(repo, "lena", "suite-1", Base);
            var moving = Seed(repo, "marco", "room-2", Base.AddHours(5));

            var t1 = pf.Evaluate(new MoveProposal(moving.AppointmentId, "lena", "room-9", Base.AddMinutes(30), 60, 1), Caller());
            var noReason = svc.CommitMove(t1.Token, 1, null, Caller(SpmsScopes.Schedule));
            Check("a soft conflict without a reason is refused",
                noReason is WriteOutcome<Appointment>.Conflicted);

            var t2 = pf.Evaluate(new MoveProposal(moving.AppointmentId, "lena", "room-9", Base.AddMinutes(30), 60, 1), Caller());
            var withReason = svc.CommitMove(t2.Token, 1, "Guest agreed to wait", Caller(SpmsScopes.Schedule));
            Check("a soft conflict with a reason commits", withReason is WriteOutcome<Appointment>.Committed);
            Check("the override is audited against its conflict code",
                audit.Recent().Any(e => e.Code == "CON-001"));
            Check("the reason reaches the audit purpose",
                audit.Recent().Any(e => e.Purpose.Contains("Guest agreed to wait")));
        }

        Section("Hard conflict is never overridable (CON-003)");
        {
            var (svc, pf, repo, _, _) = Build();
            Seed(repo, "lena", "suite-1", Base);
            var moving = Seed(repo, "marco", "room-2", Base.AddHours(5));

            var t = pf.Evaluate(new MoveProposal(moving.AppointmentId, "marco", "suite-1", Base.AddMinutes(30), 60, 1), Caller());
            var withReason = svc.CommitMove(t.Token, 1, "Manager approved", Caller(SpmsScopes.Schedule, SpmsScopes.Admin));
            Check("a hard conflict is refused even with a reason and admin scope",
                withReason is WriteOutcome<Appointment>.Conflicted { AnyHard: true });
            Check("the room did not move", repo.Get(moving.AppointmentId)!.RoomId == "room-2");
        }

        Section("Transitions and audit");
        {
            var (svc, _, repo, audit, _) = Build();
            var a = Seed(repo, "lena", "suite-1", Base);

            var illegal = svc.Transition(a.AppointmentId, AppointmentStatus.Completed, 1, Caller(SpmsScopes.Write));
            Check("an illegal transition is a validation failure",
                illegal is WriteOutcome<Appointment>.Rejected { Problem.Code: "VALIDATION_FAILED" });

            var legal = svc.Transition(a.AppointmentId, AppointmentStatus.CheckedIn, 1, Caller(SpmsScopes.Write));
            Check("a legal transition commits", legal is WriteOutcome<Appointment>.Committed);
            Check("the version increments", repo.Get(a.AppointmentId)!.RowVersion == 2);

            var staleTransition = svc.Transition(a.AppointmentId, AppointmentStatus.Ready, 1, Caller(SpmsScopes.Write));
            Check("a stale transition is refused", staleTransition is WriteOutcome<Appointment>.Stale);

            // Exactly one write succeeded here. A rejected write must leave no
            // trace — an audit trail padded with attempts is hard to read and
            // implies changes that never happened.
            Equal("only the successful write is audited", 1, audit.Recent().Count);
            Check("the audited action names the transition",
                audit.Recent()[0].Action == "appointment.transition.CheckedIn");
            Check("audit records the version it produced", audit.Recent()[0].SubjectVersion == 2);
            Check("audit stores a hash, never the row itself",
                audit.Recent().All(e => e.AfterHash is null || e.AfterHash.Length == 32));
            Check("audit carries a correlation id",
                audit.Recent().All(e => !string.IsNullOrWhiteSpace(e.CorrelationId)));
        }
    }
}
