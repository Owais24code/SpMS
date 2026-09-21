using Spms.Domain.Abstractions;

namespace Spms.Domain.Scheduling;

/// <summary>
/// Conflict evaluation and commit rules, free of HTTP concerns so the same
/// rules serve REST, the command bus and any batch import. CON-005 requires
/// the complete post-change state to be evaluated, which only holds if there
/// is exactly one place that does it.
///
/// Note on numbering: CON-001..007 exist in two namespaces in the spec — the
/// numbered requirements (§1114-1127) and the conflict-code register
/// (§2953-2959). The codes below are the register.
/// </summary>
public sealed class SchedulingService(
    IAppointmentRepository repository,
    IPreflightStore preflights,
    IAuditSink audit,
    IClock clock)
{
    public static readonly TimeSpan PreflightTtl = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How far either side of the proposal to load neighbours, on top of the
    /// buffer padding. It must exceed the longest possible treatment, or an
    /// appointment that starts well before the proposal and runs into it would
    /// not be loaded and its overlap would be invisible. The catalogue's
    /// longest service is 90 minutes; four hours leaves room for the service
    /// master to grow without silently losing conflicts.
    /// </summary>
    private static readonly TimeSpan NeighbourScanSlack = TimeSpan.FromHours(4);

    private readonly BufferPolicy _buffers = BufferPolicy.Default;

    /* ===================== write serialisation ===================== */

    /// <summary>
    /// One gate per tenant+property, held across evaluate-then-write.
    ///
    /// Without it, evaluation and the write are two separate store operations:
    /// two operators could each see a free room, and both commit into it. The
    /// conflict register calls that "physically impossible", and a RowVersion
    /// check cannot catch it, because the invariant is over the SET of
    /// appointments sharing a room, not over the row being written — nothing
    /// bumps the moved row's version when a different row appears beside it.
    ///
    /// This is a single-node measure and is not sufficient behind a load
    /// balancer. The durable fix is a Postgres exclusion constraint:
    ///   EXCLUDE USING gist (property_id WITH =, room_id WITH =,
    ///                       tstzrange(start_utc, end_utc) WITH &amp;&amp;)
    ///   WHERE (status NOT IN ('Cancelled','NoShow'))
    /// translated back to CON-002 on violation. Until that exists, this gate
    /// is the only thing enforcing the constraint, so it is deliberately
    /// coarse rather than clever.
    /// </summary>
    private static readonly Dictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly object GatesLock = new();

    private static SemaphoreSlim Gate(string tenantId, string propertyId)
    {
        var key = $"{tenantId}/{propertyId}";
        lock (GatesLock)
        {
            if (!Gates.TryGetValue(key, out var gate)) Gates[key] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    /* ========================== evaluation ========================== */

    /// <summary>
    /// Evaluates a proposal without minting or storing anything. The pure
    /// half, so the commit path can re-run it and a create can use it without
    /// leaving a live token behind.
    /// </summary>
    public async Task<IReadOnlyList<Conflict>> EvaluateAsync(
        string tenantId, string propertyId, Appointment target, MoveProposal proposal, CancellationToken ct = default)
    {
        var proposedStart = proposal.StartUtc;
        var proposedEnd = proposedStart.AddMinutes(target.DurationMinutes);

        var providerId = proposal.ProviderId ?? target.ProviderId;
        var roomId = proposal.RoomId ?? target.RoomId;

        // Widen the window by the largest buffer plus the scan slack, so
        // touching intervals and turnover violations either side are loaded.
        var pad = TimeSpan.FromMinutes(Math.Max(_buffers.RoomTurnoverMinutes, _buffers.ProviderTransitionMinutes));
        var neighbours = await repository.ListOverlappingAsync(
            tenantId, propertyId,
            proposedStart - pad - NeighbourScanSlack,
            proposedEnd + pad + NeighbourScanSlack, ct);

        var conflicts = new List<Conflict>();

        // CON-003 first: an unqualified provider is disqualifying whether or
        // not they are free, and an unknown provider fails closed.
        if (providerId is not null && !QualificationRegister.IsQualified(providerId, target.ServiceId))
        {
            conflicts.Add(QualificationRegister.IsKnown(providerId)
                ? ConflictCatalog.ProviderNotQualified(providerId, target.ServiceName)
                : ConflictCatalog.ProviderUnknown(providerId));
        }

        foreach (var other in neighbours)
        {
            if (other.AppointmentId == target.AppointmentId) continue;

            // Cancelled and no-show rows hold nothing. Completed rows still
            // occupied the room, so they do count.
            if (other.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow) continue;

            if (other.Overlaps(proposedStart, proposedEnd))
            {
                if (providerId is not null && other.ProviderId == providerId)
                    conflicts.Add(ConflictCatalog.ProviderOverlap(providerId));

                if (roomId is not null && other.RoomId == roomId)
                    conflicts.Add(ConflictCatalog.ResourceOverlap(roomId));

                // Guest identity, never the display alias.
                if (other.GuestId == target.GuestId)
                    conflicts.Add(ConflictCatalog.GuestOverlap(target.GuestAlias));

                continue;
            }

            // CON-004 buffers, checked for rooms AND providers. Both are
            // CON-004 in the register but they are different rules, so both
            // can fire against one neighbour and both must be reported.
            var gap = Gap(proposedStart, proposedEnd, other);

            if (roomId is not null && other.RoomId == roomId && gap < _buffers.RoomTurnoverMinutes)
                conflicts.Add(ConflictCatalog.RoomTurnoverCrossed(_buffers.RoomTurnoverMinutes));

            if (providerId is not null && other.ProviderId == providerId && gap < _buffers.ProviderTransitionMinutes)
                conflicts.Add(ConflictCatalog.ProviderTransitionCrossed(_buffers.ProviderTransitionMinutes));
        }

        return Dedupe(conflicts);
    }

    /// <summary>
    /// Minutes of clear air between the proposal and a neighbour. Touching
    /// gives 0; an overlap gives a NEGATIVE number, which trips every
    /// `gap &lt; buffer` check.
    ///
    /// The previous version returned double.MaxValue for an overlapping pair,
    /// so a caller reaching it without the overlap branch first would read
    /// "an enormous gap" and every buffer conflict would vanish. In a
    /// licensing-adjacent path the failure mode has to be a false positive,
    /// not a silent all-clear.
    /// </summary>
    private static double Gap(DateTimeOffset start, DateTimeOffset end, Appointment other)
    {
        // Exactly one term is non-negative for disjoint intervals, and both are
        // negative when they overlap, so Max picks the real gap either way.
        var after = (start - other.EndUtc).TotalMinutes;
        var before = (other.StartUtc - end).TotalMinutes;
        return Math.Max(after, before);
    }

    /* ========================== preflight ========================== */

    public async Task<PreflightResult> PreflightAsync(
        string tenantId, string propertyId, Appointment target, MoveProposal proposal, CancellationToken ct = default)
    {
        var conflicts = await EvaluateAsync(tenantId, propertyId, target, proposal, ct);

        var result = new PreflightResult(
            Token: "pf_" + Guid.NewGuid().ToString("n")[..16],
            ExpiresUtc: clock.UtcNow.Add(PreflightTtl),
            Proposal: proposal,
            PropertyId: propertyId,
            ProposedStartUtc: proposal.StartUtc,
            ProposedEndUtc: proposal.StartUtc.AddMinutes(target.DurationMinutes),
            Conflicts: conflicts);

        preflights.EvictExpired(clock.UtcNow);
        await preflights.SaveAsync(tenantId, result, ct);
        return result;
    }

    /* =========================== create =========================== */

    public enum CreateOutcome { Created, HardConflict, ReasonRequired, IdCollision }

    public sealed record CreateResult(CreateOutcome Outcome, Appointment? Appointment, IReadOnlyList<Conflict> Conflicts);

    /// <summary>
    /// Creates a booking, evaluating and inserting under the property gate so
    /// the two cannot interleave. Create previously evaluated and inserted as
    /// two separate operations, and concurrent creates into one room both
    /// passed — a hard conflict, twice over, out of an endpoint that had just
    /// declared the room free.
    /// </summary>
    public async Task<CreateResult> CreateAsync(
        string tenantId, string propertyId, Appointment candidate,
        string? reason, string actor, string correlationId, CancellationToken ct = default)
    {
        var gate = Gate(tenantId, propertyId);
        await gate.WaitAsync(ct);
        try
        {
            var conflicts = await EvaluateAsync(tenantId, propertyId, candidate,
                new MoveProposal(candidate.AppointmentId, candidate.StartUtc,
                    candidate.ProviderId, candidate.RoomId, candidate.RowVersion), ct);

            if (conflicts.Any(c => !c.Overridable))
                return new CreateResult(CreateOutcome.HardConflict, null, conflicts);

            if (conflicts.Count > 0 && string.IsNullOrWhiteSpace(reason))
                return new CreateResult(CreateOutcome.ReasonRequired, null, conflicts);

            if (!await repository.TryAddAsync(candidate, ct))
                return new CreateResult(CreateOutcome.IdCollision, null, conflicts);

            await audit.RecordAsync(new AuditEntry(
                AtUtc: clock.UtcNow, TenantId: tenantId, PropertyId: propertyId, Actor: actor,
                Action: "appointment.create", Purpose: "scheduling",
                SubjectType: "appointment", SubjectId: candidate.AppointmentId,
                SubjectVersion: candidate.RowVersion,
                BeforeHash: null, AfterHash: StateHash.Of(candidate),
                ConflictCodes: conflicts.Select(c => c.Code).ToList(),
                SelectedResolution: null, TargetStatus: candidate.Status.ToString(),
                Reason: reason, CorrelationId: correlationId), ct);

            return new CreateResult(CreateOutcome.Created, candidate, conflicts);
        }
        finally
        {
            gate.Release();
        }
    }

    /* =========================== commit =========================== */

    public enum CommitOutcome
    {
        Committed, TokenInvalid, HardConflict, ReasonRequired,
        StaleVersion, NotFound, NotReschedulable, AppointmentMismatch,
        BoardChanged,
    }

    public sealed record CommitResult(
        CommitOutcome Outcome,
        Appointment? Appointment,
        PreflightResult? Preflight,
        IReadOnlyList<Conflict> Conflicts);

    /// <summary>
    /// Commits a previously preflighted move.
    ///
    /// Conflicts are RE-EVALUATED here, inside the property gate, and the
    /// commit is refused if a hard conflict has appeared since the token was
    /// minted. The stored snapshot is kept only for the audit row and for what
    /// the operator was actually shown. Trusting the snapshot meant a room
    /// booked by anyone else during the 90-second window was invisible, and
    /// CON-002 — declared physically impossible and overridable by no role —
    /// committed with a 200.
    ///
    /// The token is read first and only consumed when a commit is actually
    /// attempted. Consuming it up front meant a refusal for a missing reason
    /// destroyed the token, so the reason prompt CON-001 exists to collect was
    /// a dead end.
    /// </summary>
    public async Task<CommitResult> CommitMoveAsync(
        string tenantId, string propertyId, string routeAppointmentId,
        string token, string? reason, string actor, string correlationId, CancellationToken ct = default)
    {
        var gate = Gate(tenantId, propertyId);
        await gate.WaitAsync(ct);
        try
        {
            var pf = await preflights.FindAsync(tenantId, token, ct);
            if (pf is null || pf.IsExpired(clock.UtcNow))
                return new CommitResult(CommitOutcome.TokenInvalid, null, pf, []);

            // A token minted at one property must not commit at another. The
            // tenant-scoped store blocked this only incidentally, through the
            // repository's property filter, rather than by any designed check.
            if (!string.Equals(pf.PropertyId, propertyId, StringComparison.Ordinal))
                return new CommitResult(CommitOutcome.TokenInvalid, null, pf, []);

            // The URL must name the appointment the token was minted for, or a
            // client bug silently reschedules a different guest.
            if (!string.Equals(pf.Proposal.AppointmentId, routeAppointmentId, StringComparison.Ordinal))
                return new CommitResult(CommitOutcome.AppointmentMismatch, null, pf, []);

            if (!pf.CommitAllowed)
                return new CommitResult(CommitOutcome.HardConflict, null, pf, pf.Conflicts);

            if (pf.RequiresReason && string.IsNullOrWhiteSpace(reason))
                return new CommitResult(CommitOutcome.ReasonRequired, null, pf, pf.Conflicts);

            var appointment = await repository.GetAsync(tenantId, propertyId, pf.Proposal.AppointmentId, ct);
            if (appointment is null)
                return new CommitResult(CommitOutcome.NotFound, null, pf, []);

            if (!appointment.IsReschedulable)
                return new CommitResult(CommitOutcome.NotReschedulable, appointment, pf, []);

            if (appointment.RowVersion != pf.Proposal.FromRowVersion)
                return new CommitResult(CommitOutcome.StaleVersion, appointment, pf, []);

            // The board as it is NOW, not as it was when the token was minted.
            var current = await EvaluateAsync(tenantId, propertyId, appointment, pf.Proposal, ct);

            if (current.Any(c => !c.Overridable))
                return new CommitResult(CommitOutcome.BoardChanged, appointment, pf, current);

            // A soft conflict that appeared after the operator decided needs
            // its own acknowledgement; the reason they gave was for a
            // different set of facts.
            var unseen = current.Where(c => pf.Conflicts.All(shown => shown.Rule != c.Rule)).ToList();
            if (unseen.Count > 0)
                return new CommitResult(CommitOutcome.BoardChanged, appointment, pf, current);

            // Only now is the token spent. A loser in a race gets TokenInvalid
            // rather than both requests applying the same move.
            if (!await preflights.TryConsumeAsync(tenantId, token, ct))
                return new CommitResult(CommitOutcome.TokenInvalid, appointment, pf, []);

            var beforeHash = StateHash.Of(appointment);
            var expected = appointment.RowVersion;

            appointment.ApplyMove(
                pf.ProposedStartUtc,
                Assignment.Set(pf.Proposal.ProviderId, pf.Proposal.RoomId),
                clock.UtcNow);

            if (!await repository.TryUpdateAsync(appointment, expected, ct))
            {
                // Re-read so the 412 body shows the real current state rather
                // than the proposal we had already applied in memory.
                var stored = await repository.GetAsync(tenantId, propertyId, pf.Proposal.AppointmentId, ct);
                return new CommitResult(CommitOutcome.StaleVersion, stored, pf, []);
            }

            await audit.RecordAsync(new AuditEntry(
                AtUtc: clock.UtcNow, TenantId: tenantId, PropertyId: propertyId, Actor: actor,
                Action: "appointment.move", Purpose: "scheduling",
                SubjectType: "appointment", SubjectId: appointment.AppointmentId,
                SubjectVersion: appointment.RowVersion,
                BeforeHash: beforeHash, AfterHash: StateHash.Of(appointment),
                ConflictCodes: pf.Conflicts.Select(c => c.Code).ToList(),
                SelectedResolution: null, TargetStatus: null,
                Reason: reason, CorrelationId: correlationId), ct);

            return new CommitResult(CommitOutcome.Committed, appointment, pf, pf.Conflicts);
        }
        finally
        {
            gate.Release();
        }
    }

    /* ========================= transition ========================= */

    public enum TransitionOutcome { Applied, NotFound, StaleVersion, Illegal }

    public sealed record TransitionResult(
        TransitionOutcome Outcome, Appointment? Appointment, IReadOnlyList<AppointmentStatus> Allowed);

    /// <summary>
    /// Applies a lifecycle transition. Lives here rather than in the endpoint
    /// so the state machine, the version bump and the audit row are one
    /// operation with one owner.
    /// </summary>
    public async Task<TransitionResult> TransitionAsync(
        string tenantId, string propertyId, string appointmentId, AppointmentStatus to,
        int expectedRowVersion, string? reason, string actor, string correlationId, CancellationToken ct = default)
    {
        var gate = Gate(tenantId, propertyId);
        await gate.WaitAsync(ct);
        try
        {
            var appointment = await repository.GetAsync(tenantId, propertyId, appointmentId, ct);
            if (appointment is null) return new TransitionResult(TransitionOutcome.NotFound, null, []);

            if (appointment.RowVersion != expectedRowVersion)
                return new TransitionResult(TransitionOutcome.StaleVersion, appointment, []);

            if (!AppointmentTransitions.CanMove(appointment.Status, to))
                return new TransitionResult(TransitionOutcome.Illegal, appointment,
                    AppointmentTransitions.NextFrom(appointment.Status));

            var beforeHash = StateHash.Of(appointment);
            var expected = appointment.RowVersion;
            appointment.ApplyTransition(to, clock.UtcNow);

            if (!await repository.TryUpdateAsync(appointment, expected, ct))
            {
                var stored = await repository.GetAsync(tenantId, propertyId, appointmentId, ct);
                return new TransitionResult(TransitionOutcome.StaleVersion, stored, []);
            }

            await audit.RecordAsync(new AuditEntry(
                AtUtc: clock.UtcNow, TenantId: tenantId, PropertyId: propertyId, Actor: actor,
                Action: "appointment.transition", Purpose: "scheduling",
                SubjectType: "appointment", SubjectId: appointment.AppointmentId,
                SubjectVersion: appointment.RowVersion,
                BeforeHash: beforeHash, AfterHash: StateHash.Of(appointment),
                ConflictCodes: [], SelectedResolution: null,
                // The target status has its own field. It used to be written
                // into SelectedResolution, which means "which alternative the
                // operator picked" — so the trail said "CheckedIn" where no
                // resolution existed and null where one did.
                TargetStatus: to.ToString(),
                Reason: reason, CorrelationId: correlationId), ct);

            return new TransitionResult(TransitionOutcome.Applied, appointment, []);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Deduplicates by RULE, not by code. Room turnover and provider
    /// transition share the code CON-004, so grouping by code discarded one of
    /// two genuinely different breaches along with its distinct resolutions.
    /// </summary>
    private static List<Conflict> Dedupe(List<Conflict> conflicts) =>
        conflicts.GroupBy(c => c.Rule, StringComparer.Ordinal).Select(g => g.First()).ToList();
}
