using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Host.Operations;

/// <summary>A change a board should reload for: what changed, where, and when. No content.</summary>
public sealed record BoardChange(Guid TenantId, Guid PropertyId, string Kind, Guid AggregateId, DateTimeOffset OccurredUtc);

/// <summary>
/// The live board's fan-out: every open board at a property receives a
/// signal when something on it changed, and reloads what it shows through
/// the ordinary, authorized reads. The signal carries no guest, no time and
/// no status — only the kind of record and its id — so a stream that leaked
/// would say nothing a board viewer could not already see.
///
/// In-process by design for R1 (one API instance per environment): with
/// several instances this becomes a subscription on a shared bus, and the
/// endpoint and the client stay as they are.
/// </summary>
public sealed class BoardFeed
{
    private readonly ConcurrentDictionary<Guid, Channel<BoardChange>> _subscribers = new();

    public int Subscribers => _subscribers.Count;

    public (Guid Id, ChannelReader<BoardChange> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        // Bounded, dropping the oldest: a slow client misses intermediate signals, never the latest.
        var channel = Channel.CreateBounded<BoardChange>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var c)) c.Writer.TryComplete();
    }

    public void Publish(BoardChange change)
    {
        foreach (var c in _subscribers.Values) c.Writer.TryWrite(change);
    }
}

/// <summary>Feeds the board from the outbox: committed changes only, never an intention.</summary>
public sealed class BoardFeedHandler(BoardFeed feed) : IOutboxHandler
{
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        [EventTypes.AppointmentCreated] = "appointment",
        [EventTypes.AppointmentRescheduled] = "appointment",
        [EventTypes.AppointmentStatusChanged] = "appointment",
        [EventTypes.TurnaroundChanged] = "room",
        [EventTypes.VisitChanged] = "visit",
        [EventTypes.OrderChanged] = "order",
    };

    public string Name => "board-feed";
    public bool Handles(string eventType) => Kinds.ContainsKey(eventType);

    public Task HandleAsync(OutboxMessage m, CancellationToken ct)
    {
        if (m.PropertyId is { } property)
            feed.Publish(new BoardChange(m.TenantId, property, Kinds[m.EventType], m.AggregateId, m.OccurredAt));
        return Task.CompletedTask;
    }
}

public static class BoardStream
{
    /// <summary>
    /// GET /board/stream: server-sent events for the caller's current property.
    /// Mapped outside the request transaction (a stream must not hold one open)
    /// and authorized once, at connect, with can_view_board. A comment every
    /// 15 seconds keeps proxies from closing an idle stream.
    /// </summary>
    public static void MapBoardStream(this IEndpointRouteBuilder app)
    {
        app.MapGet("/board/stream", async (HttpContext http, BoardFeed feed, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) { await denied.ExecuteAsync(http); return; }
            if (await Guard.RequireAccessAsync(ctx, access, "can_view_board", Fga.Property(ctx.PropertyId), ct: ct) is { } refused)
            { await refused.ExecuteAsync(http); return; }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache, no-transform";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.WriteAsync($"retry: 3000\nevent: ready\ndata: {{\"propertyId\":\"{ctx.PropertyId}\"}}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);

            var (id, reader) = feed.Subscribe();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    beat.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        var change = await reader.ReadAsync(beat.Token);
                        if (change.TenantId != ctx.TenantId || change.PropertyId != ctx.PropertyId) continue;
                        var data = JsonSerializer.Serialize(new { kind = change.Kind, id = change.AggregateId, occurredUtc = change.OccurredUtc.ToUniversalTime().ToString("O") }, Json.Options);
                        await http.Response.WriteAsync($"event: change\ndata: {data}\n\n", ct);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await http.Response.WriteAsync(": keep-alive\n\n", ct);
                    }
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* the board closed */ }
            finally { feed.Unsubscribe(id); }
        });
    }
}
