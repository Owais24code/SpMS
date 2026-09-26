using Microsoft.EntityFrameworkCore;

namespace Spms.Persistence;

/*
 * Marker contracts the generated row classes implement. They are how the
 * context applies tenancy filters and how the save interceptor stamps and
 * guards rows, without a hand-written list of tables anywhere.
 */

/// <summary>A row with tenant_id (every table but core.tenant).</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>A PROPERTY-scoped row: property_id NOT NULL.</summary>
public interface IPropertyOwned : ITenantOwned
{
    Guid PropertyId { get; set; }
}

/// <summary>A TENANT_OPT row: property_id NULL means tenant-wide.</summary>
public interface IOptionalPropertyOwned : ITenantOwned
{
    Guid? PropertyId { get; set; }
}

/// <summary>MASTER / AGGREGATE rows: version advances by exactly one per update (DEC-005).</summary>
public interface IVersioned
{
    int Version { get; set; }
}

/// <summary>LEDGER rows: UPDATE and DELETE raise in the database; the interceptor refuses them earlier.</summary>
public interface IAppendOnly;

public interface ICreatedBy
{
    Guid? CreatedBy { get; set; }
}

public interface IUpdatedBy
{
    Guid? UpdatedBy { get; set; }
}

public interface ICorrelated
{
    string? CorrelationId { get; set; }
}

/// <summary>
/// One per module: maps that module's schema into the single context. The
/// generated <c>&lt;Module&gt;ModelContributor</c> classes implement it.
/// </summary>
public interface IModelContributor
{
    string Schema { get; }
    void Configure(ModelBuilder b);
}
