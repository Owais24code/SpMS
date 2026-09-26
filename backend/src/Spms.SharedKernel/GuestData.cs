namespace Spms.SharedKernel;

/// <summary>
/// What a module holds about one guest, for a privacy request (IDN-006).
/// The guest module owns the request; every module above it that stores
/// guest data contributes its part to an access export and carries out its
/// part of an erasure, so no module reaches into another's tables.
/// </summary>
public interface IGuestDataContributor
{
    /// <summary>A short, stable section name in the export ("appointments", "intake").</summary>
    string Section { get; }

    /// <summary>The guest's data held by this module, as plain serialisable values.</summary>
    Task<object?> ExportAsync(Guid guestId, CancellationToken ct);

    /// <summary>Removes or neutralises what this module holds; returns how many records it changed.</summary>
    Task<int> EraseAsync(Guid guestId, CancellationToken ct);
}
