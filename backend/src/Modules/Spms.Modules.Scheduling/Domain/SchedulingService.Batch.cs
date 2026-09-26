using Spms.SharedKernel;

namespace Spms.Modules.Scheduling.Domain;

/// <summary>Undo (CON-006 window), bulk move, and hold expiry.</summary>
public sealed partial class SchedulingService
{
    /* ============================= undo ============================= */

    public enum UndoOutcome { Undone, TokenInvalid, AppointmentMismatch, WindowClosed, StaleVersion, NotFound, NotReschedulable, HardConflict }

    public sealed record UndoResult(UndoOutcome Outcome, Appointment? Appointment, IReadOnlyList<Conflict> Conflicts);

    /// <summary>
    /// Puts a committed reassign back where it was, inside the undo window.
    ///
    /// Only if nothing else touched the appointment since: an undo that
    /// overwrote a later edit would silently destroy it. The way back is
    /// evaluated like any move — a room someone else booked in the meantime
    /// is still a hard conflict, and the undo is refused rather than forced.
    /// Soft conflicts are reported but not re-approved: the operator is
    /// restoring an arrangement the board already held.
    /// </summary>
    public async Task<UndoResult> UndoMoveAsync(
        string tenantId, string propertyId, string routeAppointmentId, string token, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await unitOfWork.BeginAsync(ct);

        var move = await preflights.FindCommittedAsync(tenantId, token, ct);
        if (move is null || !string.Equals(move.PropertyId, propertyId, StringComparison.Ordinal))
            return new UndoResult(UndoOutcome.TokenInvalid, null, []);
        if (!string.Equals(move.AppointmentId, routeAppointmentId, StringComparison.Ordinal))
            return new UndoResult(UndoOutcome.AppointmentMismatch, null, []);

        var now = clock.UtcNow;
        if (move.UndoneUtc is not null || move.UndoUntilUtc is not { } until || now >= until)
            return new UndoResult(UndoOutcome.WindowClosed, null, []);

        var appointment = await repository.GetAsync(tenantId, propertyId, move.AppointmentId, ct);
        if (appointment is null) return new UndoResult(UndoOutcome.NotFound, null, []);
        if (appointment.RowVersion != move.CommittedRowVersion)
            return new UndoResult(UndoOutcome.StaleVersion, appointment, []);
        if (!appointment.IsReschedulable)
            return new UndoResult(UndoOutcome.NotReschedulable, appointment, []);

        var conflicts = await EvaluatePlacementAsync(tenantId, propertyId, appointment, move.Previous, null, ct);
        if (conflicts.Any(c => !c.Overridable))
            return new UndoResult(UndoOutcome.HardConflict, appointment, conflicts);

        if (!await preflights.TryMarkUndoneAsync(tenantId, token, now, ct))
            return new UndoResult(UndoOutcome.WindowClosed, appointment, []);

        var beforeHash = StateHash.Of(appointment);
        var expected = appointment.RowVersion;
        appointment.ApplyMove(move.Previous.StartUtc,
            new Assignment(true, move.Previous.ProviderId, true, move.Previous.RoomId), now);

        try
        {
            if (!await repository.TryUpdateAsync(appointment, expected, ct))
                return new UndoResult(UndoOutcome.StaleVersion, await repository.GetAsync(tenantId, propertyId, move.AppointmentId, ct), []);
        }
        catch (RoomOverlapException e)
        {
            return new UndoResult(UndoOutcome.HardConflict, appointment,
                Dedupe([.. conflicts, ConflictCatalog.ResourceOverlap(e.RoomId ?? "(unknown)")]));
        }

        await audit.RecordAsync(new AuditEntry(
            Action: "appointment.move.undo", EntityType: "appointment", EntityId: appointment.AppointmentId,
            EntityVersion: appointment.RowVersion, Purpose: "scheduling",
            BeforeHash: beforeHash, AfterHash: StateHash.Of(appointment),
            ConflictCodes: conflicts.Select(c => c.Code).ToList(),
            ReasonCode: "CON-006-undo"), ct);

        outbox.Enqueue(new OutboxEvent(EventTypes.AppointmentRescheduled, "appointment", ParseId(appointment.AppointmentId),
            appointment.RowVersion, new
            {
                appointmentId = appointment.AppointmentId, startUtc = appointment.StartUtc, endUtc = appointment.EndUtc,
                providerId = appointment.ProviderId, roomId = appointment.RoomId, undo = true,
            }));

        await tx.CommitAsync(ct);
        return new UndoResult(UndoOutcome.Undone, appointment, conflicts);
    }

    /* =========================== bulk move =========================== */

    public sealed record BulkItem(string AppointmentId, DateTimeOffset StartUtc, string? ProviderId, string? RoomId, int FromRowVersion);

    public enum BulkItemState { Ok, NotFound, NotReschedulable, StaleVersion, Duplicate }

    public sealed record BulkItemResult(string AppointmentId, BulkItemState State, IReadOnlyList<Conflict> Conflicts, Appointment? Appointment);

    public enum BulkOutcome { Evaluated, Committed, Invalid, HardConflict, ReasonRequired, StaleVersion, BoardChanged }

    public sealed record BulkResult(BulkOutcome Outcome, IReadOnlyList<BulkItemResult> Items)
    {
        public bool HasHard => Items.Any(i => i.Conflicts.Any(c => !c.Overridable));
        public bool HasSoft => Items.Any(i => i.Conflicts.Count > 0);
    }

    /// <summary>
    /// Moves several appointments as one decision, all or nothing — a
    /// provider going home sick moves their whole afternoon, and half of it
    /// moved is worse than none of it.
    ///
    /// Each item is evaluated against the board AFTER every item has moved
    /// (the overlay), so two appointments trading rooms is not a conflict and
    /// two landing in the same room is. The room exclusion is deferred to the
    /// end of the batch for the same reason, then checked before anything is
    /// committed.
    /// </summary>
    public async Task<BulkResult> BulkMoveAsync(
        string tenantId, string propertyId, IReadOnlyList<BulkItem> items, string? reason, bool dryRun,
        string correlationId, CancellationToken ct = default)
    {
        await using var tx = await unitOfWork.BeginAsync(ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var loaded = new List<(BulkItem Item, Appointment? Current, BulkItemState State)>();
        foreach (var item in items)
        {
            if (!seen.Add(item.AppointmentId)) { loaded.Add((item, null, BulkItemState.Duplicate)); continue; }
            var a = await repository.GetAsync(tenantId, propertyId, item.AppointmentId, ct);
            var state = a is null ? BulkItemState.NotFound
                : !a.IsReschedulable ? BulkItemState.NotReschedulable
                : a.RowVersion != item.FromRowVersion ? BulkItemState.StaleVersion
                : BulkItemState.Ok;
            loaded.Add((item, a, state));
        }

        if (loaded.Any(l => l.State != BulkItemState.Ok))
        {
            var outcome = loaded.All(l => l.State is BulkItemState.Ok or BulkItemState.StaleVersion)
                ? BulkOutcome.StaleVersion : BulkOutcome.Invalid;
            return new BulkResult(outcome, loaded.Select(l => new BulkItemResult(l.Item.AppointmentId, l.State, [], l.Current)).ToList());
        }

        // Where each will be. A null provider or room in the request means
        // "leave it", as for a single reassign.
        var overlay = new Dictionary<string, Appointment>(StringComparer.Ordinal);
        var placements = new Dictionary<string, Placement>(StringComparer.Ordinal);
        foreach (var (item, current, _) in loaded)
        {
            var placement = new Placement(item.StartUtc, item.ProviderId ?? current!.ProviderId, item.RoomId ?? current!.RoomId);
            placements[item.AppointmentId] = placement;
            var moved = current!.Copy();
            moved.ApplyMove(placement.StartUtc, new Assignment(true, placement.ProviderId, true, placement.RoomId), clock.UtcNow);
            overlay[item.AppointmentId] = moved;
        }

        var results = new List<BulkItemResult>();
        foreach (var (item, current, _) in loaded)
        {
            var conflicts = await EvaluatePlacementAsync(tenantId, propertyId, current!, placements[item.AppointmentId], overlay, ct);
            results.Add(new BulkItemResult(item.AppointmentId, BulkItemState.Ok, conflicts, overlay[item.AppointmentId]));
        }

        var evaluated = new BulkResult(BulkOutcome.Evaluated, results);
        if (dryRun) return evaluated;
        if (evaluated.HasHard) return evaluated with { Outcome = BulkOutcome.HardConflict };
        if (evaluated.HasSoft && string.IsNullOrWhiteSpace(reason)) return evaluated with { Outcome = BulkOutcome.ReasonRequired };

        await repository.DeferRoomExclusionAsync(ct);
        foreach (var (item, current, _) in loaded)
        {
            var moved = overlay[item.AppointmentId];
            try
            {
                if (!await repository.TryUpdateAsync(moved, current!.RowVersion, ct))
                    return evaluated with { Outcome = BulkOutcome.StaleVersion };
            }
            catch (RoomOverlapException)
            {
                return evaluated with { Outcome = BulkOutcome.BoardChanged };
            }
        }

        try
        {
            await repository.CheckRoomExclusionAsync(ct);
        }
        catch (RoomOverlapException e)
        {
            // Someone else's booking took one of the target rooms between the
            // evaluation and the writes.
            return new BulkResult(BulkOutcome.BoardChanged, results.Select(r =>
                overlay[r.AppointmentId].RoomId == e.RoomId
                    ? r with { Conflicts = Dedupe([.. r.Conflicts, ConflictCatalog.ResourceOverlap(e.RoomId ?? "(unknown)")]) }
                    : r).ToList());
        }

        foreach (var (item, current, _) in loaded)
        {
            var moved = overlay[item.AppointmentId];
            var conflicts = results.Single(r => r.AppointmentId == item.AppointmentId).Conflicts;
            await audit.RecordAsync(new AuditEntry(
                Action: "appointment.move", EntityType: "appointment", EntityId: moved.AppointmentId,
                EntityVersion: moved.RowVersion, Purpose: "scheduling.bulk",
                BeforeHash: StateHash.Of(current!), AfterHash: StateHash.Of(moved),
                ConflictCodes: conflicts.Select(c => c.Code).ToList(),
                ReasonText: reason), ct);
            outbox.Enqueue(new OutboxEvent(EventTypes.AppointmentRescheduled, "appointment", ParseId(moved.AppointmentId),
                moved.RowVersion, new
                {
                    appointmentId = moved.AppointmentId, startUtc = moved.StartUtc, endUtc = moved.EndUtc,
                    providerId = moved.ProviderId, roomId = moved.RoomId, bulk = true,
                }));
        }

        await tx.CommitAsync(ct);
        return evaluated with { Outcome = BulkOutcome.Committed };
    }

    /* ========================== hold expiry ========================== */

    public const string HoldExpiredReason = "HoldExpired";

    /// <summary>
    /// Releases online slot holds whose time ran out: Held → Cancelled with
    /// reason HoldExpired, audited and published like any cancellation, so
    /// the room and provider are free again for the next guest.
    /// </summary>
    public async Task<int> ReleaseExpiredHoldsAsync(string tenantId, string propertyId, int limit = 100, CancellationToken ct = default)
    {
        var expired = await repository.ListExpiredHoldsAsync(tenantId, propertyId, clock.UtcNow, limit, ct);
        var released = 0;
        foreach (var a in expired)
        {
            var r = await TransitionAsync(tenantId, propertyId, a.AppointmentId, AppointmentStatus.Cancelled,
                a.RowVersion, "The online hold expired before it was confirmed.", "hold-expiry", ct, HoldExpiredReason);
            if (r.Outcome == TransitionOutcome.Applied) released++;
        }
        return released;
    }
}
