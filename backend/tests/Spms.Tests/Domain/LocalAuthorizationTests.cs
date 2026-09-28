using System.Text.Json;
using Spms.Host.Authorization;
using Xunit;

namespace Spms.Tests.Domain;

/// <summary>
/// The in-process decider (Authorization:Mode = Local) against the same
/// assertions the OpenFGA CLI runs (authorization/model.fga.yaml, exported to
/// model.fga.tests.json): every check and list-objects case must agree.
/// </summary>
public class LocalAuthorizationTests
{
    private static readonly FgaEvaluator Evaluator = new(FgaModel.Embedded());

    private static JsonElement Fixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "authorization", "model.fga.tests.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "authorization", "model.fga.tests.json")));
        return doc.RootElement.Clone();
    }

    private static StoredTuple Tuple(JsonElement t)
    {
        string? condition = null;
        Dictionary<string, object?>? context = null;
        if (t.TryGetProperty("condition", out var c))
        {
            condition = c.GetProperty("name").GetString();
            context = c.TryGetProperty("context", out var cx)
                ? cx.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                : [];
        }
        return new StoredTuple(t.GetProperty("user").GetString()!, t.GetProperty("relation").GetString()!, t.GetProperty("object").GetString()!,
            condition, context);
    }

    private static Dictionary<string, object?> Context(JsonElement c) =>
        c.TryGetProperty("context", out var cx) ? cx.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()) : [];

    public static IEnumerable<object[]> Tests() =>
        Fixture().GetProperty("tests").EnumerateArray().Select(t => new object[] { t.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(Tests))]
    public void Agrees_with_the_model_tests(string name)
    {
        var fixture = Fixture();
        var test = fixture.GetProperty("tests").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);
        var all = fixture.GetProperty("tuples").EnumerateArray().Select(Tuple).ToList();
        if (test.TryGetProperty("tuples", out var extra)) all.AddRange(extra.EnumerateArray().Select(Tuple));
        var tuples = all.ToLookup(t => (t.Object, t.Relation));

        var failures = new List<string>();
        if (test.TryGetProperty("check", out var checks))
            foreach (var c in checks.EnumerateArray())
            {
                var user = c.GetProperty("user").GetString()!;
                var obj = c.GetProperty("object").GetString()!;
                foreach (var a in c.GetProperty("assertions").EnumerateObject())
                {
                    var got = Evaluator.Check(user, a.Name, obj, tuples, Context(c));
                    if (got != a.Value.GetBoolean()) failures.Add($"{user} {a.Name} {obj}: expected {a.Value.GetBoolean()}, got {got}");
                }
            }

        if (test.TryGetProperty("list_objects", out var lists))
            foreach (var l in lists.EnumerateArray())
            {
                var user = l.GetProperty("user").GetString()!;
                var type = l.GetProperty("type").GetString()!;
                var candidates = all.SelectMany(t => new[] { t.Object, t.User })
                    .Where(o => o.StartsWith(type + ":", StringComparison.Ordinal) && !o.Contains('#')).Distinct().ToList();
                foreach (var a in l.GetProperty("assertions").EnumerateObject())
                {
                    var expected = a.Value.EnumerateArray().Select(x => x.GetString()!).Order().ToList();
                    var got = candidates.Where(o => Evaluator.Check(user, a.Name, o, tuples, Context(l))).Order().ToList();
                    if (!expected.SequenceEqual(got)) failures.Add($"list {user} {a.Name} {type}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", got)}]");
                }
            }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Every_check_of_the_suite_runs()
    {
        var count = Fixture().GetProperty("tests").EnumerateArray()
            .Sum(t => t.TryGetProperty("check", out var c) ? c.EnumerateArray().Sum(x => x.GetProperty("assertions").EnumerateObject().Count()) : 0);
        Assert.True(count >= 150, $"only {count} checks found");
    }

    [Fact]
    public void An_unknown_relation_or_type_is_refused()
    {
        var none = Array.Empty<StoredTuple>().ToLookup(t => (t.Object, t.Relation));
        Assert.False(Evaluator.Check("user:x", "can_do_anything", "property:p", none, new Dictionary<string, object?>()));
        Assert.False(Evaluator.Check("user:x", "can_book", "spaceship:p", none, new Dictionary<string, object?>()));
    }

    [Fact]
    public void A_delegation_without_a_time_to_compare_is_refused()
    {
        var tuples = new[]
        {
            new StoredTuple("user:d", "delegate_book", "guest:g", "active_delegation",
                new Dictionary<string, object?> { ["grant_expires_at"] = DateTimeOffset.UtcNow.AddDays(1), ["allowed_property_ids"] = new List<string>() }),
        }.ToLookup(t => (t.Object, t.Relation));
        Assert.False(Evaluator.Check("user:d", "can_book", "guest:g", tuples, new Dictionary<string, object?>()));
        Assert.True(Evaluator.Check("user:d", "can_book", "guest:g", tuples, new Dictionary<string, object?> { ["current_time"] = DateTimeOffset.UtcNow }));
    }
}
