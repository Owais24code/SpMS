using System.Text.Json;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Configuration;
using OpenFga.Sdk.Model;
using Spms.SharedKernel;

namespace Spms.Host.Authorization;

public sealed class AuthorizationOptions
{
    /// <summary>OpenFga (required outside Development) or Permissive (Development without an OpenFGA server).</summary>
    public string Mode { get; set; } = "OpenFga";
    public OpenFgaOptions OpenFga { get; set; } = new();
}

public sealed class OpenFgaOptions
{
    public string? ApiUrl { get; set; }
    public string? StoreId { get; set; }
    public string? ModelId { get; set; }
    /// <summary>The preshared key (Key Vault in deployed environments).</summary>
    public string? ApiToken { get; set; }
    /// <summary>Development: create the store and write authorization/model.json when StoreId is empty.</summary>
    public bool Bootstrap { get; set; }
    public string StoreName { get; set; } = "spms";
}

/// <summary>One OpenFGA client for the process, pointed at a store and a pinned model.</summary>
public sealed class FgaClientHolder
{
    private readonly OpenFgaOptions _options;
    private OpenFgaClient? _client;

    public FgaClientHolder(OpenFgaOptions options)
    {
        _options = options;
        if (!string.IsNullOrWhiteSpace(options.StoreId)) _client = Create(options.StoreId, options.ModelId);
    }

    public bool Ready => _client is not null;

    public async Task BootstrapIfNeededAsync(WebApplication app)
    {
        if (!Ready && _options.Bootstrap) await BootstrapAsync(app.Logger);
        if (!Ready) throw new InvalidOperationException("Authorization:OpenFga:StoreId is not set and Bootstrap is off.");
    }
    public string? ModelId { get; private set; }

    public OpenFgaClient Client => _client ?? throw new AccessUnavailableException("OpenFGA is not configured with a store.");

    private OpenFgaClient Create(string store, string? model)
    {
        ModelId = model;
        var cfg = new ClientConfiguration
        {
            ApiUrl = _options.ApiUrl!,
            StoreId = store,
            AuthorizationModelId = string.IsNullOrWhiteSpace(model) ? null : model,
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiToken))
            cfg.Credentials = new Credentials { Method = CredentialsMethod.ApiToken, Config = new CredentialsConfig { ApiToken = _options.ApiToken } };
        return new OpenFgaClient(cfg);
    }

    /// <summary>
    /// Development bootstrap: find or create the store, write the model from
    /// authorization/model.json (embedded), and pin that model id.
    /// </summary>
    public async Task BootstrapAsync(ILogger logger, CancellationToken ct = default)
    {
        var admin = new OpenFgaClient(new ClientConfiguration { ApiUrl = _options.ApiUrl! });
        var stores = await admin.ListStores(new ClientListStoresRequest(), null, ct);
        var store = stores.Stores?.FirstOrDefault(s => s.Name == _options.StoreName)?.Id
                    ?? (await admin.CreateStore(new ClientCreateStoreRequest { Name = _options.StoreName }, null, ct)).Id;

        await using var stream = typeof(FgaClientHolder).Assembly.GetManifestResourceStream("Spms.Authorization.model.json")!;
        var model = await JsonSerializer.DeserializeAsync<ClientWriteAuthorizationModelRequest>(stream, cancellationToken: ct)
                    ?? throw new InvalidOperationException("authorization/model.json is empty.");
        var scoped = Create(store, null);
        var written = await scoped.WriteAuthorizationModel(model, null, ct);
        _client = Create(store, written.AuthorizationModelId);
        logger.LogInformation("OpenFGA store {Store}, model {Model}", store, written.AuthorizationModelId);
    }
}

/// <summary>The access decider over OpenFGA. Any failure to decide is a refusal (503), never an allow.</summary>
public sealed class OpenFgaAccessDecider(FgaClientHolder fga, ILogger<OpenFgaAccessDecider> logger) : IAccessDecider
{
    public async Task<AccessDecision> CheckAsync(AccessCheck check, CancellationToken ct = default)
    {
        try
        {
            var response = await fga.Client.Check(new ClientCheckRequest
            {
                User = check.User,
                Relation = check.Relation,
                Object = check.Object,
                ContextualTuples = check.Contextual?.Select(t => new ClientTupleKey { User = t.User, Relation = t.Relation, Object = t.Object }).ToList(),
                Context = check.Context?.ToDictionary(k => k.Key, k => k.Value),
            }, new ClientCheckOptions
            {
                Consistency = check.Strong ? ConsistencyPreference.HIGHERCONSISTENCY : ConsistencyPreference.MINIMIZELATENCY,
            }, ct);
            return new AccessDecision(response.Allowed == true, fga.ModelId);
        }
        catch (Exception e) when (e is not OperationCanceledException and not AccessUnavailableException)
        {
            logger.LogError(e, "OpenFGA check failed for {Relation} on {Object}", check.Relation, check.Object);
            throw new AccessUnavailableException("OpenFGA did not answer.", e);
        }
    }
}

/// <summary>
/// Development only, with no OpenFGA server: allows every relationship and
/// says so loudly at start-up. The scope check (the coarse gate) and RLS still
/// apply. The host refuses this mode outside Development.
/// </summary>
public sealed class PermissiveAccessDecider : IAccessDecider
{
    public Task<AccessDecision> CheckAsync(AccessCheck check, CancellationToken ct = default) =>
        Task.FromResult(new AccessDecision(true, "permissive-dev"));
}

/// <summary>Stored-tuple writes and deletes, idempotent (duplicates and missing deletes are ignored).</summary>
public sealed class FgaTupleWriter(FgaClientHolder fga)
{
    private static readonly ClientWriteOptions Idempotent = new()
    {
        Conflict = new ConflictOptions { OnDuplicateWrites = OnDuplicateWrites.Ignore, OnMissingDeletes = OnMissingDeletes.Ignore },
    };

    public Task WriteAsync(IReadOnlyList<FgaTuple> tuples, CancellationToken ct) =>
        tuples.Count == 0 ? Task.CompletedTask : fga.Client.Write(new ClientWriteRequest
        {
            Writes = tuples.Select(t => new ClientTupleKey { User = t.User, Relation = t.Relation, Object = t.Object }).ToList(),
        }, Idempotent, ct);

    public Task WriteConditionalAsync(FgaTuple t, string condition, object context, CancellationToken ct) =>
        fga.Client.Write(new ClientWriteRequest
        {
            Writes = [new ClientTupleKey
            {
                User = t.User, Relation = t.Relation, Object = t.Object,
                Condition = new RelationshipCondition { Name = condition, Context = context },
            }],
        }, Idempotent, ct);

    public Task DeleteAsync(IReadOnlyList<FgaTuple> tuples, CancellationToken ct) =>
        tuples.Count == 0 ? Task.CompletedTask : fga.Client.Write(new ClientWriteRequest
        {
            Deletes = tuples.Select(t => new ClientTupleKeyWithoutCondition { User = t.User, Relation = t.Relation, Object = t.Object }).ToList(),
        }, Idempotent, ct);
}
