namespace Spms.SharedKernel;

/// <summary>
/// Who is acting, for which tenant and properties, in this unit of execution:
/// one HTTP request, one worker iteration, one test.
///
/// It is the single source the persistence layer reads to scope a transaction
/// (core.begin_scope), to filter queries and to stamp created_by/updated_by.
/// Registered as a scoped service; the request pipeline or the worker sets it
/// once, before the first query.
///
/// An unset scope means NO tenant, and every tenant table then returns
/// nothing: the database policies fail closed, and so does the EF filter.
/// </summary>
public sealed class ExecutionScope
{
    public Guid? TenantId { get; private set; }
    public IReadOnlyList<Guid> PropertyIds { get; private set; } = [];

    /// <summary>The property the caller is working at, when the operation is property-bound.</summary>
    public Guid? CurrentPropertyId { get; private set; }

    public Guid? PrincipalId { get; private set; }
    public ActorType ActorType { get; private set; } = ActorType.System;
    public string? CorrelationId { get; private set; }

    public bool IsSet => TenantId is not null;

    public void Set(Guid tenantId, IReadOnlyList<Guid> propertyIds, Guid? currentPropertyId,
                    Guid? principalId, ActorType actorType, string? correlationId)
    {
        if (currentPropertyId is { } p && !propertyIds.Contains(p))
            throw new ArgumentException("The current property must be one of the scoped properties.", nameof(currentPropertyId));

        TenantId = tenantId;
        PropertyIds = propertyIds.Distinct().ToArray();
        CurrentPropertyId = currentPropertyId;
        PrincipalId = principalId;
        ActorType = actorType;
        CorrelationId = correlationId;
    }

    /// <summary>Identity only, before a tenant is known (magic-link redemption, principal resolution).</summary>
    public void SetAnonymous(string? correlationId)
    {
        TenantId = null;
        PropertyIds = [];
        CurrentPropertyId = null;
        PrincipalId = null;
        ActorType = ActorType.System;
        CorrelationId = correlationId;
    }

    public Guid RequireTenant() =>
        TenantId ?? throw new InvalidOperationException("No tenant is in scope for this operation.");

    public Guid RequireProperty() =>
        CurrentPropertyId ?? throw new InvalidOperationException("No property is in scope for this operation.");
}

/// <summary>core.audit_event.actor_type.</summary>
public enum ActorType { Staff, Guest, Service, Device, System }
