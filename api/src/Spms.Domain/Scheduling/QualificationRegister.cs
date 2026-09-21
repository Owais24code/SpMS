namespace Spms.Domain.Scheduling;

/// <summary>
/// Stub qualification matrix, replaced by staff_qualification + credential.
///
/// Fails closed: a provider absent from the register is NOT qualified. The
/// previous version treated an unknown provider as qualified, which inverted
/// a licensing rule — exactly the case a new hire or a typo produces.
/// </summary>
public static class QualificationRegister
{
    private static readonly Dictionary<string, string[]> Qualified = new(StringComparer.Ordinal)
    {
        ["prov-lena"]  = ["svc-deep", "svc-aroma", "svc-hotstone", "svc-swedish"],
        ["prov-marco"] = ["svc-deep", "svc-swedish", "svc-hotstone"],
        ["prov-priya"] = ["svc-facial", "svc-peel"],
    };

    public static bool IsQualified(string providerId, string serviceId) =>
        Qualified.TryGetValue(providerId, out var svcs) && svcs.Contains(serviceId, StringComparer.Ordinal);

    public static bool IsKnown(string providerId) => Qualified.ContainsKey(providerId);
}
