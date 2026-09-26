using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Npgsql;
using Spms.Host.Workers;
using Spms.SharedKernel;

namespace Spms.Host.Operations;

public sealed class ObservabilityOptions
{
    /// <summary>Required to read /metrics outside Development (header X-Metrics-Key); empty = the endpoint is off there.</summary>
    public string MetricsKey { get; set; } = "";
    /// <summary>"json" (the default outside Development) or "simple".</summary>
    public string LogFormat { get; set; } = "";
    public int SampleSeconds { get; set; } = 30;
}

/// <summary>
/// Collects the Spms meter in memory and renders Prometheus text. Counters
/// are summed per tag set; histograms keep fixed buckets. A few gauges are
/// sampled rather than instrumented: the outbox backlog and its oldest age,
/// and the number of open board streams.
/// </summary>
public sealed class MetricsCollector : IDisposable
{
    private static readonly double[] Buckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<(string Name, string Tags), long> _counters = new();
    private readonly ConcurrentDictionary<(string Name, string Tags), Histogram> _histograms = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _help = new(StringComparer.Ordinal);

    private sealed class Histogram
    {
        public readonly long[] Counts = new long[Buckets.Length + 1];
        public double Sum;
        public long Count;
    }

    public MetricsCollector()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name != Telemetry.Name) return;
            _help[instrument.Name] = instrument.Description ?? instrument.Name;
            listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => _counters.AddOrUpdate((i.Name, Key(tags)), v, (_, old) => old + v));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) =>
        {
            var h = _histograms.GetOrAdd((i.Name, Key(tags)), _ => new Histogram());
            lock (h)
            {
                var b = Array.FindIndex(Buckets, x => v <= x);
                h.Counts[b < 0 ? Buckets.Length : b]++;
                h.Sum += v;
                h.Count++;
            }
        });
        _listener.Start();
    }

    public void SetGauge(string name, double value, string help)
    {
        _gauges[name] = value;
        _help[name] = help;
    }

    private static string Key(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0) return "";
        var parts = new List<string>(tags.Length);
        foreach (var t in tags) parts.Add($"{t.Key}=\"{Escape(Convert.ToString(t.Value, CultureInfo.InvariantCulture) ?? "")}\"");
        parts.Sort(StringComparer.Ordinal);
        return string.Join(',', parts);
    }

    private static string Escape(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var group in _counters.GroupBy(c => c.Key.Name).OrderBy(g => g.Key))
        {
            sb.Append("# HELP ").Append(group.Key).Append("_total ").Append(_help.GetValueOrDefault(group.Key, group.Key)).Append('\n');
            sb.Append("# TYPE ").Append(group.Key).Append("_total counter\n");
            foreach (var c in group.OrderBy(c => c.Key.Tags)) sb.Append(group.Key).Append("_total").Append(Labels(c.Key.Tags)).Append(' ').Append(c.Value).Append('\n');
        }
        foreach (var group in _histograms.GroupBy(h => h.Key.Name).OrderBy(g => g.Key))
        {
            sb.Append("# HELP ").Append(group.Key).Append(' ').Append(_help.GetValueOrDefault(group.Key, group.Key)).Append('\n');
            sb.Append("# TYPE ").Append(group.Key).Append(" histogram\n");
            foreach (var h in group.OrderBy(h => h.Key.Tags))
            {
                long cumulative = 0;
                lock (h.Value)
                {
                    for (var i = 0; i < Buckets.Length; i++)
                    {
                        cumulative += h.Value.Counts[i];
                        sb.Append(group.Key).Append("_bucket").Append(Labels(h.Key.Tags, $"le=\"{Buckets[i].ToString(CultureInfo.InvariantCulture)}\"")).Append(' ').Append(cumulative).Append('\n');
                    }
                    cumulative += h.Value.Counts[Buckets.Length];
                    sb.Append(group.Key).Append("_bucket").Append(Labels(h.Key.Tags, "le=\"+Inf\"")).Append(' ').Append(cumulative).Append('\n');
                    sb.Append(group.Key).Append("_sum").Append(Labels(h.Key.Tags)).Append(' ').Append(h.Value.Sum.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    sb.Append(group.Key).Append("_count").Append(Labels(h.Key.Tags)).Append(' ').Append(h.Value.Count).Append('\n');
                }
            }
        }
        foreach (var g in _gauges.OrderBy(g => g.Key))
        {
            sb.Append("# HELP ").Append(g.Key).Append(' ').Append(_help.GetValueOrDefault(g.Key, g.Key)).Append('\n');
            sb.Append("# TYPE ").Append(g.Key).Append(" gauge\n");
            sb.Append(g.Key).Append(' ').Append(g.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Labels(string tags, string? extra = null)
    {
        var all = string.Join(',', new[] { tags, extra ?? "" }.Where(s => s.Length > 0));
        return all.Length == 0 ? "" : "{" + all + "}";
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>Samples the gauges that live in the database: the outbox backlog across tenants, read as the publisher role.</summary>
public sealed class MetricsSampler(MetricsCollector metrics, NpgsqlDataSource dataSource, OutboxOptions outbox, BoardFeed board,
    ObservabilityOptions options, ILogger<MetricsSampler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync(stoppingToken);
                await using var tx = await conn.BeginTransactionAsync(stoppingToken);
                await using (var role = new NpgsqlCommand($"SET LOCAL ROLE \"{outbox.Role.Replace("\"", "\"\"")}\"", conn, tx))
                    await role.ExecuteNonQueryAsync(stoppingToken);
                await using var cmd = new NpgsqlCommand(
                    "SELECT count(*), coalesce(extract(epoch FROM now() - min(occurred_at)), 0) FROM core.event_outbox WHERE published_at IS NULL", conn, tx);
                await using var r = await cmd.ExecuteReaderAsync(stoppingToken);
                if (await r.ReadAsync(stoppingToken))
                {
                    metrics.SetGauge("spms_outbox_pending", r.GetInt64(0), "Outbox events not yet published.");
                    metrics.SetGauge("spms_outbox_oldest_pending_seconds", Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture), "Age of the oldest unpublished outbox event.");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Metrics sample failed");
            }
            metrics.SetGauge("spms_board_streams", board.Subscribers, "Open live-board streams.");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.SampleSeconds)), stoppingToken);
        }
    }
}

public static class ObservabilityEndpoints
{
    /// <summary>
    /// Times every request and counts it by route template (never the raw path,
    /// which carries ids), method and status class; and puts the trace id next
    /// to the correlation id in the log scope.
    /// </summary>
    public static IApplicationBuilder UseRequestMetrics(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var started = Stopwatch.GetTimestamp();
        try { await next(); }
        finally
        {
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            var status = $"{http.Response.StatusCode / 100}xx";
            var tags = new TagList { { "route", route }, { "method", http.Request.Method }, { "status", status } };
            Telemetry.HttpRequests.Add(1, tags);
            Telemetry.HttpDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    });

    public static void MapMetrics(this IEndpointRouteBuilder app, IHostEnvironment env, ObservabilityOptions options)
    {
        app.MapGet("/metrics", (HttpContext http, MetricsCollector metrics) =>
        {
            if (!env.IsDevelopment())
            {
                if (string.IsNullOrEmpty(options.MetricsKey)) return Results.NotFound();
                var presented = http.Request.Headers["X-Metrics-Key"].FirstOrDefault() ?? "";
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(options.MetricsKey)))
                    return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            return Results.Text(metrics.Render(), "text/plain; version=0.0.4; charset=utf-8");
        });
    }
}
