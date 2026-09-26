namespace Spms.SharedKernel;

/// <summary>
/// Staged into core.event_outbox on the request's DbContext, so an event is
/// published if and only if the change it announces commits (NFR-001).
///
/// Payloads carry ids and non-restricted facts only: no secrets, card data or
/// clinical content, because the outbox is read by integrations and the FGA
/// tuple sync.
/// </summary>
public interface IOutbox
{
    void Enqueue(OutboxEvent e);
}

public sealed record OutboxEvent(
    string EventType,
    string AggregateType,
    Guid AggregateId,
    int? AggregateVersion,
    object Payload,
    /// <summary>Null = the scope's current property; tenant-wide events set TenantWide.</summary>
    Guid? PropertyId = null,
    bool TenantWide = false,
    int SchemaVersion = 1,
    string? CausationId = null);

/// <summary>Event type names. Consumers key on these strings; they are append-only.</summary>
public static class EventTypes
{
    public const string AppointmentCreated = "scheduling.appointment.created.v1";
    public const string AppointmentRescheduled = "scheduling.appointment.rescheduled.v1";
    public const string AppointmentStatusChanged = "scheduling.appointment.status_changed.v1";
    public const string RoleAssignmentChanged = "workforce.role_assignment.changed.v1";
    public const string GuestOwnershipChanged = "guest.ownership.changed.v1";
    public const string DelegationChanged = "guest.delegation.changed.v1";
    public const string DeviceRegistrationChanged = "core.device_registration.changed.v1";
    public const string SettingActivated = "core.setting.activated.v1";
    public const string PaymentIntentChanged = "commerce.payment_intent.changed.v1";
    public const string OrderChanged = "commerce.order.changed.v1";
    public const string MessageScheduled = "messaging.message.scheduled.v1";
    public const string StockMoved = "inventory.stock.moved.v1";
    public const string VisitChanged = "scheduling.visit.changed.v1";
}
