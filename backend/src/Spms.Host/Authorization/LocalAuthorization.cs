using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Host.Authorization;

/// <summary>A relationship as the model sees it, with its condition when it has one.</summary>
public sealed record StoredTuple(string User, string Relation, string Object,
    string? Condition = null, IReadOnlyDictionary<string, object?>? ConditionContext = null)
{
    public static StoredTuple From(FgaTuple t) => new(t.User, t.Relation, t.Object);
    public FgaTuple Plain => new(User, Relation, Object);
}

/// <summary>
/// authorization/model.json, parsed once: for each type and relation, how the
/// relation is computed. Only what the SpMS model uses is supported — direct
/// tuples (with conditions), computed usersets, tuple-to-userset and union —
/// and loading refuses anything else, so a model change that needs more cannot
/// silently evaluate to something weaker.
/// </summary>
public sealed class FgaModel
{
    public abstract record Rewrite;
    public sealed record Direct : Rewrite;
    public sealed record Computed(string Relation) : Rewrite;
    public sealed record TupleToUserset(string Tupleset, string Relation) : Rewrite;
    public sealed record Union(IReadOnlyList<Rewrite> Children) : Rewrite;

    private readonly Dictionary<string, Dictionary<string, Rewrite>> _types = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Conditions { get; }

    private FgaModel(JsonElement root)
    {
        foreach (var t in root.GetProperty("type_definitions").EnumerateArray())
        {
            var relations = new Dictionary<string, Rewrite>(StringComparer.Ordinal);
            if (t.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Object)
                foreach (var r in rels.EnumerateObject()) relations[r.Name] = Parse(r.Value);
            _types[t.GetProperty("type").GetString()!] = relations;
        }
        Conditions = root.TryGetProperty("conditions", out var c) && c.ValueKind == JsonValueKind.Object
            ? c.EnumerateObject().Select(x => x.Name).ToArray()
            : [];
        foreach (var name in Conditions)
            if (!LocalConditions.Known(name))
                throw new InvalidOperationException($"The in-process decider does not know the condition '{name}' in authorization/model.json.");
    }

    public static FgaModel Load(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        return new FgaModel(doc.RootElement.Clone());
    }

    public static FgaModel Embedded()
    {
        using var s = typeof(FgaModel).Assembly.GetManifestResourceStream("Spms.Authorization.model.json")
                      ?? throw new InvalidOperationException("authorization/model.json is not embedded.");
        return Load(s);
    }

    public Rewrite? Find(string type, string relation) =>
        _types.TryGetValue(type, out var rels) && rels.TryGetValue(relation, out var r) ? r : null;

    private static Rewrite Parse(JsonElement u)
    {
        var p = u.EnumerateObject().Single();
        return p.Name switch
        {
            "this" => new Direct(),
            "computedUserset" => new Computed(p.Value.GetProperty("relation").GetString()!),
            "tupleToUserset" => new TupleToUserset(
                p.Value.GetProperty("tupleset").GetProperty("relation").GetString()!,
                p.Value.GetProperty("computedUserset").GetProperty("relation").GetString()!),
            "union" => new Union(p.Value.GetProperty("child").EnumerateArray().Select(Parse).ToArray()),
            _ => throw new InvalidOperationException($"The in-process decider does not support '{p.Name}' in authorization/model.json."),
        };
    }
}

/// <summary>The model's two conditions, in C#. Unknown or incomplete context is a refusal.</summary>
public static class LocalConditions
{
    public static bool Known(string name) => name is "active_delegation" or "active_grant";

    public static bool Evaluate(string name, IReadOnlyDictionary<string, object?>? stored, IReadOnlyDictionary<string, object?> request)
    {
        object? Get(string key) =>
            stored is not null && stored.TryGetValue(key, out var v) && v is not null ? v
            : request.TryGetValue(key, out var r) ? r : null;

        if (Time(Get("current_time")) is not { } now || Time(Get("grant_expires_at")) is not { } expires) return false;
        if (!(now < expires)) return false;
        if (name == "active_grant") return true;
        if (name != "active_delegation") return false;

        var allowed = Strings(Get("allowed_property_ids"));
        if (allowed is null) return false;
        if (allowed.Count == 0) return true;
        return Get("property_id") is { } p && allowed.Contains(Normalise(p), StringComparer.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? Time(object? v) => v switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) => d,
        JsonElement { ValueKind: JsonValueKind.String } e when DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var d) => d,
        _ => null,
    };

    private static List<string>? Strings(object? v) => v switch
    {
        null => null,
        string s => [s],
        JsonElement { ValueKind: JsonValueKind.Array } e => e.EnumerateArray().Select(x => Normalise(x)).ToList(),
        System.Collections.IEnumerable items => items.Cast<object?>().Where(x => x is not null).Select(x => Normalise(x!)).ToList(),
        _ => null,
    };

    private static string Normalise(object v) => v switch
    {
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        Guid g => g.ToString("D"),
        _ => v.ToString() ?? "",
    };
}

/// <summary>
/// Check(user, relation, object) over a set of tuples, with OpenFGA's
/// semantics for the rewrites the SpMS model uses. Deterministic and
/// side-effect free: the model tests (authorization/model.fga.yaml) run
/// through it in the test suite.
/// </summary>
public sealed class FgaEvaluator(FgaModel model)
{
    private const int MaxDepth = 25;

    public bool Check(string user, string relation, string @object, ILookup<(string Object, string Relation), StoredTuple> tuples,
        IReadOnlyDictionary<string, object?> context) =>
        Check(user, relation, @object, tuples, context, 0, []);

    private bool Check(string user, string relation, string @object, ILookup<(string, string), StoredTuple> tuples,
        IReadOnlyDictionary<string, object?> context, int depth, HashSet<string> path)
    {
        if (depth > MaxDepth) throw new AccessUnavailableException("The authorization model resolved too deeply.");
        var type = @object[..Math.Max(0, @object.IndexOf(':'))];
        if (model.Find(type, relation) is not { } rewrite) return false;
        var key = $"{relation}@{@object}";
        if (!path.Add(key)) return false; // a cycle proves nothing
        try
        {
            return Eval(rewrite);
        }
        finally
        {
            path.Remove(key);
        }

        bool Eval(FgaModel.Rewrite r) => r switch
        {
            FgaModel.Direct => tuples[(@object, relation)].Any(t => Matches(t)),
            FgaModel.Computed c => Check(user, c.Relation, @object, tuples, context, depth + 1, path),
            FgaModel.TupleToUserset ttu => tuples[(@object, ttu.Tupleset)]
                .Any(t => ConditionHolds(t) && Check(user, ttu.Relation, t.User, tuples, context, depth + 1, path)),
            FgaModel.Union u => u.Children.Any(Eval),
            _ => false,
        };

        bool Matches(StoredTuple t)
        {
            if (!ConditionHolds(t)) return false;
            if (t.User == user) return true;
            var hash = t.User.IndexOf('#');
            if (hash > 0) return Check(user, t.User[(hash + 1)..], t.User[..hash], tuples, context, depth + 1, path);
            return t.User.EndsWith(":*", StringComparison.Ordinal) && user.StartsWith(t.User[..^1], StringComparison.Ordinal);
        }

        bool ConditionHolds(StoredTuple t) => t.Condition is null || LocalConditions.Evaluate(t.Condition, t.ConditionContext, context);
    }
}

/// <summary>
/// Every stored relationship of one tenant, read from the SpMS tables — the
/// same facts the outbox would otherwise write into OpenFGA. The OpenFGA
/// reconciler writes this set; the in-process decider evaluates against it.
/// </summary>
public sealed class TenantTuples(NpgsqlDataSource dataSource, PersistenceOptions persistence)
{
    public async Task<List<StoredTuple>> ReadAsync(Guid tenant, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var scope = new ExecutionScope();
        scope.Set(tenant, [], null, null, ActorType.System, "fga-read");
        await ScopeSql.ApplyAsync(conn, tx, scope, persistence.RuntimeRole, ct);

        var properties = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("SELECT property_id FROM core.property", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) properties.Add(r.GetGuid(0));

        scope.Set(tenant, properties, null, null, ActorType.System, "fga-read");
        await ScopeSql.ApplyAsync(conn, tx, scope, null, ct);

        var tuples = properties.Select(p => new StoredTuple(Fga.Tenant(tenant), "tenant", Fga.Property(p))).ToList();

        await using (var cmd = new NpgsqlCommand("""
            SELECT s.principal_id, a.role_code, a.property_id
              FROM workforce.staff_role_assignment a
              JOIN workforce.staff s ON s.staff_id = a.staff_id
             WHERE a.status = 'Active' AND s.principal_id IS NOT NULL
               AND a.effective_from <= now() AND (a.effective_to IS NULL OR a.effective_to > now())
            """, conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                tuples.Add(new StoredTuple(Fga.User(r.GetGuid(0)), r.GetString(1), r.IsDBNull(2) ? Fga.Tenant(tenant) : Fga.Property(r.GetGuid(2))));

        await using (var cmd = new NpgsqlCommand(
            "SELECT guest_id, principal_id FROM guest.guest WHERE principal_id IS NOT NULL AND status IN ('Active', 'Restricted')", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                tuples.Add(new StoredTuple(Fga.User(r.GetGuid(1)), "owner", Fga.Guest(r.GetGuid(0))));
                tuples.Add(new StoredTuple(Fga.Tenant(tenant), "tenant", Fga.Guest(r.GetGuid(0))));
            }

        await using (var cmd = new NpgsqlCommand("""
            SELECT guest_id, delegate_principal_id, allowed_actions, property_ids, effective_to
              FROM guest.delegated_authority
             WHERE status = 'Active' AND revoked_at IS NULL AND delegate_principal_id IS NOT NULL
               AND effective_from <= now() AND (effective_to IS NULL OR effective_to > now())
            """, conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var context = new Dictionary<string, object?>
                {
                    // An open-ended delegation is modelled as one that expires far in the future: the condition needs a time.
                    ["grant_expires_at"] = r.IsDBNull(4) ? DateTimeOffset.MaxValue.AddYears(-1) : r.GetFieldValue<DateTimeOffset>(4),
                    ["allowed_property_ids"] = r.IsDBNull(3) ? new List<string>() : r.GetFieldValue<Guid[]>(3).Select(g => g.ToString("D")).ToList(),
                };
                foreach (var action in r.GetFieldValue<string[]>(2))
                    tuples.Add(new StoredTuple(Fga.User(r.GetGuid(1)), FgaTupleSyncHandler.DelegateRelation(action), Fga.Guest(r.GetGuid(0)),
                        "active_delegation", context));
            }

        await using (var cmd = new NpgsqlCommand(
            "SELECT device_registration_id, property_id FROM core.device_registration WHERE status = 'Active'", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                tuples.Add(new StoredTuple(Fga.Property(r.GetGuid(1)), "property", Fga.Device(r.GetGuid(0))));
                tuples.Add(new StoredTuple(Fga.Device(r.GetGuid(0)), "registered_device", Fga.Property(r.GetGuid(1))));
            }

        await tx.CommitAsync(ct);
        return tuples;
    }
}

/// <summary>
/// Authorization:Mode = Local. The same model (authorization/model.json), the
/// same relationships, decided in the API process with no OpenFGA server: the
/// tenant's relationships are read from the tables and kept for a few seconds;
/// a revocation-sensitive (Strong) check always reads them fresh, and a change
/// that reaches the outbox drops the tenant's copy at once.
///
/// Fails closed like the server: no tenant, or tables that cannot be read, is
/// a 503, never an allow.
/// </summary>
public sealed class LocalAccessDecider(TenantTuples source, ILogger<LocalAccessDecider> logger) : IAccessDecider
{
    public const string ModelId = "local";
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(10);

    private static readonly Lazy<FgaEvaluator> Evaluator = new(() => new FgaEvaluator(FgaModel.Embedded()));
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, ILookup<(string, string), StoredTuple> Tuples)> _cache = new();

    public void Invalidate(Guid tenant) => _cache.TryRemove(tenant, out _);

    public async Task<AccessDecision> CheckAsync(AccessCheck check, CancellationToken ct = default)
    {
        if (check.Tenant is not { } tenant || tenant == Guid.Empty)
            throw new AccessUnavailableException("No tenant to decide in.");
        try
        {
            var stored = await TuplesAsync(tenant, check.Strong, ct);
            var tuples = check.Contextual is { Count: > 0 } extra
                ? stored.SelectMany(g => g).Concat(extra.Select(StoredTuple.From)).ToLookup(t => (t.Object, t.Relation))
                : stored;
            var context = check.Context?.ToDictionary(k => k.Key, k => (object?)k.Value) ?? [];
            context.TryAdd("current_time", DateTimeOffset.UtcNow);
            return new AccessDecision(Evaluator.Value.Check(check.User, check.Relation, check.Object, tuples, context), ModelId);
        }
        catch (Exception e) when (e is not OperationCanceledException and not AccessUnavailableException)
        {
            logger.LogError(e, "Local authorization failed for {Relation} on {Object}", check.Relation, check.Object);
            throw new AccessUnavailableException("Authorization could not be decided.", e);
        }
    }

    private async Task<ILookup<(string, string), StoredTuple>> TuplesAsync(Guid tenant, bool fresh, CancellationToken ct)
    {
        if (!fresh && _cache.TryGetValue(tenant, out var hit) && DateTimeOffset.UtcNow - hit.At < Freshness) return hit.Tuples;
        var tuples = (await source.ReadAsync(tenant, ct)).ToLookup(t => (t.Object, t.Relation));
        _cache[tenant] = (DateTimeOffset.UtcNow, tuples);
        return tuples;
    }
}
