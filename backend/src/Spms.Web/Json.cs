using System.Text.Json;

namespace Spms.Web;

/// <summary>
/// The one JsonSerializerOptions instance for the whole API surface. It lived
/// at the bottom of the appointments endpoint file, which is not where anyone
/// looks for a solution-wide serializer.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
