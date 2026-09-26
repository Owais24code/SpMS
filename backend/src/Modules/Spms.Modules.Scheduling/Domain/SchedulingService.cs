using Spms.SharedKernel;

namespace Spms.Modules.Scheduling.Domain;

/// <summary>
/// Conflict evaluation and commit rules, free of HTTP and storage concerns so
/// the same rules serve REST, the command bus and any batch import. CON-005
/// requires the complete post-change state to be evaluated, which only holds
/// if there is exactly one place that does it.
///
/// Note on numbering: CON-001..007 exist in two namespaces in the spec — the
/// numbered requirements (§1114-1127) and the conflict-code register
/// (§2953-2959). The codes below are the register.
/// </summary>
public sealed partial class SchedulingService(
    IAppointmentRepository repository,
    IPreflightStore preflights,
    IAuditSink audit,
    IUnitOfWork unitOfWork,
    IServiceCatalog services,
    IQualificationRegister qualifications,
    IPropertyDirectory properties,
    IGuestDirectory guests,
    IResourceCalendar calendar,
    IOutbox outbox,
    IClock clock,
    ISchedulingEffects? effects = null,
    SchedulingOptions? options = null)
{
    public static readonly TimeSpan PreflightTtl = TimeSpan.FromSeconds(90);

    private readonly ISchedulingEffects _effects = effects ?? NoSchedulingEffects.Instance;
    private readonly SchedulingOptions _options = options ?? new SchedulingOptions();

    /// <summary>CON-006: how long a committed reassign may be undone by the operator who made it.</summary>
    public TimeSpan UndoWindow => TimeSpan.FromSeconds(Math.Max(0, _options.UndoWindowSeconds));

    /// <summary>
    /// How far either side of the proposal to load neighbours, on top of the
    /// buffer padding. It must exceed the longest possible treatment, or an
    /// appointment that starts well before the proposal and runs into it would
    /// not be loaded and its overlap would be invisible. Four hours leaves room
    /// for the service master to grow without silently losing conflicts.
    /// </summary>
    private static readonly TimeSpan NeighbourScanSlack = TimeSpan.FromHours(4);

    /* ========================== evaluation ========================== */

    /// <summary>
    /// Evaluates a proposal without minting or storing anything. The pure
    /// half, so the commit path can re-run it and a create can use it without
    /// leaving a live token behind.
    /// </summary>
    public Task<IReadOnlyList<Conflict>> EvaluateAsync(
        string tenantId, string propertyId, Appointment target, MoveProposal proposal, CancellationToken ct = default) =>
        EvaluatePlacementAsync(tenantId, propertyId, target,
            new Placement(proposal.StartUtc, proposal.ProviderId ?? target.ProviderId, proposal.RoomId ?? target.RoomId),
            overlay: null, ct);

    /// <summary>
    /// Evaluates the target at an exact placement (a null provider or room
    /// means none, not "unchanged"). <paramref name="overlay"/> replaces stored
    /// neighbours with their post-change state, so a bulk move is judged
    /// against the board as it will be, not as it was (CON-005's complete
    /// post-change state).
    /// </summary>
    public async Task<IReadOnlyList<Conflict>> EvaluatePlacementAsync(
        string tenantId, string propertyId, Appointment target, Placement placement,
        IReadOnlyDictionary<string, Appointment>? overlay, CancellationToken ct = default)
    {
        var proposedStart = placement.StartUtc;
        var proposedEnd = proposedStart.AddMinutes(target.DurationMinutes);

        var providerId = placement.ProviderId;
        var roomId = placement.RoomId;

        var profile = await properties.FindAsync(tenantId, propertyId, ct);
        var buffers = profile?.BuffersFor(target.ServiceId) ?? BufferPolicy.Fallback;

        // Widen the window by the largest buffer plus the scan slack, so
        // touching intervals and turnover violations either side are loaded.
        var pad = TimeSpan.FromMinutes(Math.Max(buffers.RoomTurnoverMinutes, buffers.ProviderTransitionMinutes));
        var scanFrom = proposedStart - pad - NeighbourScanSlack;
        var scanTo = proposedEnd + pad + NeighbourScanSlack;
        IReadOnlyList<Appointment> neighbours = await repository.ListOverlappingAsync(tenantId, propertyId, scanFrom, scanTo, ct);
        if (overlay is { Count: > 0 })
        {
            // Stored rows that are moving are replaced by where they are going;
            // moved rows that land inside the scan are added.
            neighbours = neighbours.Where(n => !overlay.ContainsKey(n.AppointmentId))
                .Concat(overlay.Values.Where(o => o.Overlaps(scanFrom, scanTo)))
                .ToList();
        }

        var conflicts = new List<Conflict>();

        // CON-003 first: an unqualified provider is disqualifying whether or
        // not they are free, and an unknown provider fails closed.
        if (providerId is not null)
        {
            var qualified = await qualifications.IsQualifiedAsync(
                tenantId, propertyId, providerId, target.ServiceId, clock.UtcNow, ct);

            if (!qualified)
            {
                var known = await qualifications.IsKnownAsync(tenantId, propertyId, providerId, ct);
                conflicts.Add(known
                    ? ConflictCatalog.ProviderNotQualified(providerId, target.ServiceName)
                    : ConflictCatalog.ProviderUnknown(providerId));
            }
        }

        // The room must be one of this property's, and not closed for
        // maintenance across the interval. Both are hard: no role can put a
        // guest in a room that is not there or not usable.
        if (roomId is not null)
        {
            switch (await calendar.RoomAsync(tenantId, propertyId, roomId, proposedStart, proposedEnd, ct))
            {
                case RoomState.Unknown or RoomState.Retired:
                    conflicts.Add(ConflictCatalog.RoomUnknown(roomId));
                    break;
                case RoomState.OutOfService:
                    conflicts.Add(ConflictCatalog.RoomOutOfService(roomId));
                    break;
            }
        }

        // The roster: leave or no covering shift is soft (a manager may know
        // better than the roster), and a person with no roster at all is not
        // judged by one.
        if (providerId is not null)
        {
            var roster = await calendar.ProviderAsync(tenantId, propertyId, providerId, proposedStart, proposedEnd, ct);
            if (roster is RosterState.OnLeave or RosterState.OffShift)
                conflicts.Add(ConflictCatalog.ProviderUnavailable(providerId));
        }

        foreach (var other in neighbours)
        {
            if (other.AppointmentId == target.AppointmentId) continue;

            // Cancelled and no-show rows hold nothing. Completed rows still
            // occupied the room, so they do count — and the database's
            // exclusion constraint takes the same view.
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

            if (roomId is not null && other.RoomId == roomId && gap < buffers.RoomTurnoverMinutes)
                conflicts.Add(ConflictCatalog.RoomTurnoverCrossed(buffers.RoomTurnoverMinutes));

            if (providerId is not null && other.ProviderId == providerId && gap < buffers.ProviderTransitionMinutes)
                conflicts.Add(ConflictCatalog.ProviderTransitionCrossed(buffers.ProviderTransitionMinutes));
        }

        // CON-005 is tenant-wide: the same guest in a treatment at ANOTHER of
        // the tenant's properties at the same time. The neighbour scan above is
        // property-local, so this asks the tenant-wide interval function, which
        // returns intervals only and never another property's booking detail.
        var elsewhere = await repository.GuestBusyAsync(tenantId, target.GuestId, proposedStart, proposedEnd, ct);
        // The busy intervals are read from storage; a guest whose other booking
        // is moving in the same bulk operation is judged where it is going.
        if (overlay is not null)
            elsewhere = elsewhere.Where(b => !overlay.ContainsKey(b.AppointmentId)).ToList();
        if (elsewhere.Any(b => b.AppointmentId != target.AppointmentId
                               && !string.Equals(b.PropertyId, propertyId, StringComparison.Ordinal)))
            conflicts.Add(ConflictCatalog.GuestOverlapElsewhere(target.GuestAlias));

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
            // 128 bits from the CSPRNG: the token is a bearer capability for one commit.
            Token: "pf_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ExpiresUtc: clock.UtcNow.Add(PreflightTtl),
            Proposal: proposal,
            PropertyId: propertyId,
            ProposedStartUtc: proposal.StartUtc,
            ProposedEndUtc: proposal.StartUtc.AddMinutes(target.DurationMinutes),
            Conflicts: conflicts);

        await preflights.EvictExpiredAsync(clock.UtcNow, ct);
        await preflights.SaveAsync(tenantId, result, ct);
        return result;
    }

    /* =========================== create =========================== */

    public enum CreateOutcome { Created, HardConflict, ReasonRequired, IdCollision, UnknownService, UnknownProperty, UnknownGuest }

    public sealed record CreateResult(CreateOutcome Outcome, Appointment? Appointment, IReadOnlyList<Conflict> Conflicts);

    /// <summary>
    /// A booking request as the caller states it. The service's duration and
    /// name, and the property's time zone, are resolved here rather than by
    /// the endpoint — an HTTP handler that looks up reference data to build an
    /// aggregate is a second place where the rules live.
    /// </summary>
    public sealed record NewBooking(
        string AppointmentId,
        string GuestId,
        string GuestAlias,
        string ServiceId,
        DateTimeOffset StartUtc,
        string? ProviderId,
        string? RoomId,
        string? ConfirmationNumber,
        string CorrelationId,
        string Source = BookingSource.Desk,
        /// <summary>Set for an online slot hold: the booking is Held until then, then released.</summary>
        TimeSpan? HoldFor = null,
        string? VisitId = null,
        bool GuestRequestedProvider = false);

    /// <summary>
    /// Creates a booking, evaluating and inserting inside one transaction so
    /// the two cannot interleave. Create previously evaluated and inserted as
    /// separate operations, and two concurrent creates into one room both
    /// passed — a hard conflict, twice over, out of an endpoint that had just
    /// declared the room free.
    ///
    /// The transaction narrows the window; the database's exclusion constraint
    /// closes it. If a concurrent transaction commits the same room first, the
    /// insert here fails with 23P01 and is reported as CON-002 rather than as
    /// a 500.
    /// </summary>
    public async Task<CreateResult> CreateAsync(
        string tenantId, string propertyId, NewBooking booking,
        string? reason, CancellationToken ct = default)
    {
        var service = await services.FindAsync(tenantId, booking.ServiceId, ct);
        if (service is null) return new CreateResult(CreateOutcome.UnknownService, null, []);

        var profile = await properties.FindAsync(tenantId, propertyId, ct);
        if (profile is null) return new CreateResult(CreateOutcome.UnknownProperty, null, []);

        var alias = await guests.AliasAsync(tenantId, booking.GuestId, ct);
        if (alias is null) return new CreateResult(CreateOutcome.UnknownGuest, null, []);

        var now = clock.UtcNow;
        var candidate = Appointment.Create(new Appointment.NewAppointment(
            AppointmentId: booking.AppointmentId,
            TenantId: tenantId, PropertyId: propertyId, PropertyTimeZone: profile.TimeZoneId,
            GuestId: booking.GuestId, GuestAlias: alias,
            ServiceId: service.ServiceId, ServiceName: service.Name,
            DurationMinutes: service.DurationMinutes,
            ProviderId: booking.ProviderId, RoomId: booking.RoomId,
            StartUtc: booking.StartUtc, ConfirmationNumber: booking.ConfirmationNumber,
            CorrelationId: booking.CorrelationId, NowUtc: now,
            InitialStatus: booking.HoldFor is null ? AppointmentStatus.Confirmed : AppointmentStatus.Held,
            HoldExpiresUtc: booking.HoldFor is { } hold ? now.Add(hold) : null,
            Source: booking.Source,
            // Frozen at booking: later catalogue changes never reprice it.
            PriceMinor: service.PriceMinor, CurrencyCode: service.CurrencyCode,
            VisitId: booking.VisitId, GuestRequestedProvider: booking.GuestRequestedProvider));

        var correlationId = booking.CorrelationId;

        await using var tx = await unitOfWork.BeginAsync(ct);

        var conflicts = await EvaluateAsync(tenantId, propertyId, candidate,
            new MoveProposal(candidate.AppointmentId, candidate.StartUtc,
                candidate.ProviderId, candidate.RoomId, candidate.RowVersion), ct);

        if (conflicts.Any(c => !c.Overridable))
            return new CreateResult(CreateOutcome.HardConflict, null, conflicts);

        if (conflicts.Count > 0 && string.IsNullOrWhiteSpace(reason))
            return new CreateResult(CreateOutcome.ReasonRequired, null, conflicts);

        try
        {
            if (!await repository.TryAddAsync(candidate, ct))
                return new CreateResult(CreateOutcome.IdCollision, null, conflicts);
        }
        catch (RoomOverlapException e)
        {
            // The constraint saw a room collision our scan did not, because a
            // concurrent transaction committed between the two.
            return new CreateResult(CreateOutcome.HardConflict, null,
                Dedupe([.. conflicts, ConflictCatalog.ResourceOverlap(e.RoomId ?? candidate.RoomId ?? "(unknown)")]));
        }

        await audit.RecordAsync(new AuditEntry(
            Action: "appointment.create", EntityType: "appointment", EntityId: candidate.AppointmentId,
            EntityVersion: candidate.RowVersion, Purpose: "scheduling",
            ToStatus: candidate.Status.ToString(),
            AfterHash: StateHash.Of(candidate),
            ConflictCodes: conflicts.Select(c => c.Code).ToList(),
            ReasonText: reason), ct);

        outbox.Enqueue(new OutboxEvent(EventTypes.AppointmentCreated, "appointment", ParseId(candidate.AppointmentId),
            candidate.RowVersion, new
            {
                appointmentId = candidate.AppointmentId, status = candidate.Status.ToString(),
                startUtc = candidate.StartUtc, endUtc = candidate.EndUtc,
                providerId = candidate.ProviderId, roomId = candidate.RoomId, serviceId = candidate.ServiceId,
                guestId = candidate.GuestId, visitId = candidate.VisitId, source = candidate.Source,
            }));

        await tx.CommitAsync(ct);
        return new CreateResult(CreateOutcome.Created, candidate, conflicts);
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
        IReadOnlyList<Conflict> Conflicts)
    {
        /// <summary>CON-006: until when the move may be undone by a compensating reschedule.</summary>
        public DateTimeOffset? UndoUntilUtc { get; init; }
    }

    /// <summary>
    /// Commits a previously preflighted move.
    ///
    /// Conflicts are RE-EVALUATED here, inside the transaction, and the commit
    /// is refused if a hard conflict has appeared since the token was minted.
    /// The stored snapshot is kept only for the audit row and for what the
    /// operator was actually shown. Trusting the snapshot meant a room booked
    /// by anyone else during the 90-second window was invisible, and CON-002 —
    /// declared physically impossible and overridable by no role — committed
    /// with a 200.
    ///
    /// The token is read first and only consumed when a commit is actually
    /// attempted. Consuming it up front meant a refusal for a missing reason
    /// destroyed the token, so the reason prompt CON-001 exists to collect was
    /// a dead end. Consumption now happens inside the transaction, so a failed
    /// write returns the token rather than burning it.
    /// </summary>
    public async Task<CommitResult> CommitMoveAsync(
        string tenantId, string propertyId, string routeAppointmentId,
        string token, string? reason, string correlationId, CancellationToken ct = default,
        TimeSpan? undoWindow = null)
    {
        await using var tx = await unitOfWork.BeginAsync(ct);

        var pf = await preflights.FindAsync(tenantId, token, ct);
        if (pf is null || pf.IsExpired(clock.UtcNow))
            return new CommitResult(CommitOutcome.TokenInvalid, null, pf, []);

        // A token minted at one property must not commit at another.
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

        // A soft conflict that appeared after the operator decided needs its
        // own acknowledgement; the reason they gave was for a different set of
        // facts.
        var unseen = current.Where(c => pf.Conflicts.All(shown => shown.Rule != c.Rule)).ToList();
        if (unseen.Count > 0)
            return new CommitResult(CommitOutcome.BoardChanged, appointment, pf, current);

        var window = undoWindow ?? UndoWindow;
        var undoUntil = window > TimeSpan.Zero ? clock.UtcNow.Add(window) : (DateTimeOffset?)null;
        var previous = new Placement(appointment.StartUtc, appointment.ProviderId, appointment.RoomId);
        if (!await preflights.TryConsumeAsync(tenantId, token, clock.UtcNow, undoUntil, reason, previous, ct))
            return new CommitResult(CommitOutcome.TokenInvalid, appointment, pf, []);

        var beforeHash = StateHash.Of(appointment);
        var expected = appointment.RowVersion;

        appointment.ApplyMove(
            pf.ProposedStartUtc,
            Assignment.Set(pf.Proposal.ProviderId, pf.Proposal.RoomId),
            clock.UtcNow);

        try
        {
            if (!await repository.TryUpdateAsync(appointment, expected, ct))
            {
                var stored = await repository.GetAsync(tenantId, propertyId, pf.Proposal.AppointmentId, ct);
                return new CommitResult(CommitOutcome.StaleVersion, stored, pf, []);
            }
        }
        catch (RoomOverlapException e)
        {
            // Last line of defence, and the one that holds across instances.
            return new CommitResult(CommitOutcome.BoardChanged, appointment, pf,
                Dedupe([.. current, ConflictCatalog.ResourceOverlap(e.RoomId ?? "(unknown)")]));
        }

        await audit.RecordAsync(new AuditEntry(
            Action: "appointment.move", EntityType: "appointment", EntityId: appointment.AppointmentId,
            EntityVersion: appointment.RowVersion, Purpose: "scheduling",
            BeforeHash: beforeHash, AfterHash: StateHash.Of(appointment),
            ConflictCodes: pf.Conflicts.Select(c => c.Code).ToList(),
            ReasonText: reason), ct);

        outbox.Enqueue(new OutboxEvent(EventTypes.AppointmentRescheduled, "appointment", ParseId(appointment.AppointmentId),
            appointment.RowVersion, new
            {
                appointmentId = appointment.AppointmentId, startUtc = appointment.StartUtc, endUtc = appointment.EndUtc,
                providerId = appointment.ProviderId, roomId = appointment.RoomId, undoUntil,
            }));

        await tx.CommitAsync(ct);
        return new CommitResult(CommitOutcome.Committed, appointment, pf, pf.Conflicts) { UndoUntilUtc = undoUntil };
    }

    /* ========================= transition ========================= */

    public enum TransitionOutcome { Applied, NotFound, StaleVersion, Illegal }

    public sealed record TransitionResult(
        TransitionOutcome Outcome, Appointment? Appointment, IReadOnlyList<AppointmentStatus> Allowed);

    /// <summary>
    /// Applies a lifecycle transition. Lives here rather than in the endpoint
    /// so the state machine, the version bump and the audit row are one
    /// transaction with one owner.
    /// </summary>
    public async Task<TransitionResult> TransitionAsync(
        string tenantId, string propertyId, string appointmentId, AppointmentStatus to,
        int expectedRowVersion, string? reason, string correlationId, CancellationToken ct = default,
        string? reasonCode = null)
    {
        await using var tx = await unitOfWork.BeginAsync(ct);

        var appointment = await repository.GetAsync(tenantId, propertyId, appointmentId, ct);
        if (appointment is null) return new TransitionResult(TransitionOutcome.NotFound, null, []);

        if (appointment.RowVersion != expectedRowVersion)
            return new TransitionResult(TransitionOutcome.StaleVersion, appointment, []);

        if (!AppointmentTransitions.CanMove(appointment.Status, to))
            return new TransitionResult(TransitionOutcome.Illegal, appointment,
                AppointmentTransitions.NextFrom(appointment.Status));

        var beforeHash = StateHash.Of(appointment);
        var expected = appointment.RowVersion;
        var from = appointment.Status;
        appointment.ApplyTransition(to, clock.UtcNow, reasonCode);

        if (!await repository.TryUpdateAsync(appointment, expected, ct))
        {
            var stored = await repository.GetAsync(tenantId, propertyId, appointmentId, ct);
            return new TransitionResult(TransitionOutcome.StaleVersion, stored, []);
        }

        var profile = await properties.FindAsync(tenantId, propertyId, ct);
        await _effects.TransitionedAsync(appointment, from,
            profile?.BuffersFor(appointment.ServiceId) ?? BufferPolicy.Fallback, clock.UtcNow, ct);

        await audit.RecordAsync(new AuditEntry(
            Action: "appointment.transition", EntityType: "appointment", EntityId: appointment.AppointmentId,
            EntityVersion: appointment.RowVersion, Purpose: "scheduling",
            FromStatus: from.ToString(), ToStatus: to.ToString(),
            BeforeHash: beforeHash, AfterHash: StateHash.Of(appointment),
            ReasonCode: reasonCode, ReasonText: reason), ct);

        outbox.Enqueue(new OutboxEvent(EventTypes.AppointmentStatusChanged, "appointment", ParseId(appointment.AppointmentId),
            appointment.RowVersion, new
            {
                appointmentId = appointment.AppointmentId, from = from.ToString(), to = to.ToString(),
                visitId = appointment.VisitId, guestId = appointment.GuestId,
            }));

        await tx.CommitAsync(ct);
        return new TransitionResult(TransitionOutcome.Applied, appointment, []);
    }

    /// <summary>
    /// Deduplicates by RULE, not by code. Room turnover and provider
    /// transition share the code CON-004, so grouping by code discarded one of
    /// two genuinely different breaches along with its distinct resolutions.
    /// </summary>
    /// <summary>
    /// Outbox aggregate ids are uuids. In-memory test fixtures use readable
    /// ids; those map to a stable name-based uuid so the event still carries one.
    /// </summary>
    private static Guid ParseId(string id) =>
        Guid.TryParse(id, out var g) ? g : new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(id)));

    private static List<Conflict> Dedupe(IEnumerable<Conflict> conflicts) =>
        conflicts.GroupBy(c => c.Rule, StringComparer.Ordinal).Select(g => g.First()).ToList();
}
