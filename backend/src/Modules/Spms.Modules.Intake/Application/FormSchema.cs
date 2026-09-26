using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spms.Modules.Intake.Application;

/// <summary>
/// A form's fields (intake.form_definition.schema_json). "summary" marks the
/// minimum-necessary subset the assigned provider sees (DEC-004); "review"
/// marks an answer that, when yes, needs a clinician's review before treatment.
/// </summary>
public sealed record FormField(
    string Key, string Label, string Type, bool Required = false, bool Summary = false, bool Review = false,
    bool MustBeTrue = false, IReadOnlyList<string>? Options = null, int? MaxLength = null);

public sealed record FormSchema(IReadOnlyList<FormField> Fields)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { "boolean", "text", "select" };

    public static FormSchema Parse(string json) => JsonSerializer.Deserialize<FormSchema>(json, Json) ?? new FormSchema([]);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>A problem with the schema itself, or null.</summary>
    public string? Problem()
    {
        if (Fields.Count == 0) return "A form needs at least one field.";
        if (Fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() != Fields.Count) return "Field keys must be unique.";
        foreach (var f in Fields)
        {
            if (string.IsNullOrWhiteSpace(f.Key) || string.IsNullOrWhiteSpace(f.Label)) return "Every field needs a key and a label.";
            if (!Types.Contains(f.Type)) return $"Field {f.Key}: type must be boolean, text or select.";
            if (f.Type == "select" && (f.Options is null || f.Options.Count == 0)) return $"Field {f.Key}: a select needs options.";
        }
        return null;
    }

    /// <summary>
    /// Validates answers. A draft only checks what was given; a submission
    /// also needs every required field, and every must-be-true box ticked.
    /// Unknown keys are refused: nothing is stored that the form did not ask.
    /// </summary>
    public IReadOnlyList<string> Violations(IReadOnlyDictionary<string, JsonElement> answers, bool submitting)
    {
        var v = new List<string>();
        var byKey = Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        foreach (var k in answers.Keys.Where(k => !byKey.ContainsKey(k))) v.Add($"{k}: not a field of this form");
        foreach (var f in Fields)
        {
            var has = answers.TryGetValue(f.Key, out var a) && a.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            if (!has)
            {
                if (submitting && (f.Required || f.MustBeTrue)) v.Add($"{f.Key}: required");
                continue;
            }
            switch (f.Type)
            {
                case "boolean" when a.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                    v.Add($"{f.Key}: must be true or false"); break;
                case "boolean" when submitting && f.MustBeTrue && a.ValueKind != JsonValueKind.True:
                    v.Add($"{f.Key}: must be confirmed"); break;
                case "text" when a.ValueKind != JsonValueKind.String || a.GetString()!.Length > (f.MaxLength ?? 1000):
                    v.Add($"{f.Key}: text up to {f.MaxLength ?? 1000} characters"); break;
                case "text" when submitting && f.Required && string.IsNullOrWhiteSpace(a.GetString()):
                    v.Add($"{f.Key}: required"); break;
                case "select" when a.ValueKind != JsonValueKind.String || !f.Options!.Contains(a.GetString()!):
                    v.Add($"{f.Key}: must be one of {string.Join(", ", f.Options!)}"); break;
            }
        }
        return v;
    }

    /// <summary>The minimum-necessary subset the provider sees.</summary>
    public Dictionary<string, JsonElement> Summary(IReadOnlyDictionary<string, JsonElement> answers) =>
        Fields.Where(f => f.Summary && answers.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => answers[f.Key], StringComparer.Ordinal);

    /// <summary>A "yes" on a review field means a clinician looks before treatment.</summary>
    public bool NeedsReview(IReadOnlyDictionary<string, JsonElement> answers) =>
        Fields.Any(f => f.Review && answers.TryGetValue(f.Key, out var a) && a.ValueKind == JsonValueKind.True);
}
