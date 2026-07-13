using System.Collections.Concurrent;

namespace TectikaAgents.Api.Services;

/// <summary>
/// One connected SSE client. Every write goes through <see cref="WriteAsync"/>, which serialises on a
/// semaphore: a client is written to by more than one producer (a broadcast and the endpoint's own
/// heartbeat loop, or two broadcasters at once), and <see cref="TextWriter"/> is not thread-safe — without
/// the gate the frames interleave and the browser sees corrupt SSE.
/// </summary>
public sealed class SseClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TextWriter Writer { get; }
    public CancellationToken CancellationToken { get; }

    public SseClient(TextWriter writer, CancellationToken cancellationToken)
    {
        Writer = writer;
        CancellationToken = cancellationToken;
    }

    /// <summary>Write one complete SSE frame (data block or comment), then flush.</summary>
    public async Task WriteAsync(string frame, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await Writer.WriteAsync(frame.AsMemory(), ct);
            await Writer.FlushAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>The hub's key-space. Keys from different families can never collide.</summary>
public static class SseKeys
{
    public static string Run(string runId) => $"run:{runId}";
    public static string Board(string boardId) => $"board:{boardId}";
    public static string Channel(string channelId) => $"channel:{channelId}";
    public const string Notifications = "notifications";
}

/// <summary>
/// Keyed SSE fan-out. One event can go to several keys at once — a run event is delivered to both its
/// per-run subscribers and to the board stream that multiplexes every run on the board — and a client
/// subscribed to two of them still receives exactly one copy.
/// </summary>
public sealed class SseHub
{
    // The inner set is a ConcurrentDictionary (used as a set) rather than a List: the previous
    // List<SseClient> was mutated from add/remove/broadcast concurrently under nothing but the outer
    // dictionary's lock, which guards the dictionary and not the list.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<SseClient, byte>> _byKey = new();
    private readonly ILogger<SseHub> _logger;

    public SseHub(ILogger<SseHub> logger) => _logger = logger;

    public void Add(string key, SseClient client)
    {
        var set = _byKey.GetOrAdd(key, _ => new ConcurrentDictionary<SseClient, byte>());
        set[client] = 0;
        _logger.LogInformation("[Sse] client connected key={Key} total={Count}", key, set.Count);
    }

    public void Remove(string key, SseClient client)
    {
        if (!_byKey.TryGetValue(key, out var set)) return;
        set.TryRemove(client, out _);
        if (set.IsEmpty) _byKey.TryRemove(key, out _);
        _logger.LogInformation("[Sse] client disconnected key={Key} total={Count}", key, set.Count);
    }

    /// <summary>Subscriber count for a key. Test hook.</summary>
    public int CountFor(string key) => _byKey.TryGetValue(key, out var set) ? set.Count : 0;

    /// <summary>
    /// Write one frame to every client subscribed to any of <paramref name="keys"/> (null keys are
    /// skipped, duplicates collapsed). Clients are written concurrently: a stalled socket must not
    /// head-of-line block the rest, which matters much more now that one board stream carries every event.
    /// </summary>
    public async Task BroadcastAsync(string frame, CancellationToken ct = default, params string?[] keys)
    {
        var targets = new HashSet<SseClient>();
        foreach (var key in keys)
        {
            if (key is null) continue;
            if (!_byKey.TryGetValue(key, out var set)) continue;
            foreach (var client in set.Keys) targets.Add(client);
        }

        if (targets.Count == 0) return;

        var dead = new ConcurrentBag<SseClient>();
        await Task.WhenAll(targets.Select(async client =>
        {
            try
            {
                await client.WriteAsync(frame, ct);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException)
            {
                // Expected at run handoff / tab close: the client's response body is gone. Drop it quietly
                // rather than logging a warning on every disconnect race (QA S3 §4.1).
                dead.Add(client);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Sse] client write failed");
                dead.Add(client);
            }
        }));

        if (dead.IsEmpty) return;
        foreach (var set in _byKey.Values)
            foreach (var client in dead)
                set.TryRemove(client, out _);
    }
}

/// <summary>The shared body of every SSE endpoint: headers, an immediate prelude, a heartbeat, cleanup.</summary>
public static class SseEndpoint
{
    private static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(20);

    public static async Task RunAsync(
        HttpResponse response,
        SseHub hub,
        string key,
        CancellationToken ct,
        ILogger logger,
        TimeSpan? heartbeat = null)
    {
        response.Headers["Content-Type"] = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        using var client = new SseClient(new StreamWriter(response.Body), ct);
        hub.Add(key, client);
        logger.LogInformation("[Sse] subscribed key={Key}", key);

        try
        {
            // Flushes the response headers, so EventSource fires `onopen` immediately instead of leaving the
            // request "pending" until the first event — which for an idle board could be minutes.
            await client.WriteAsync(": connected\n\n", ct);

            // Keeps the connection off an idle proxy's / Container Apps ingress's reaper.
            using var timer = new PeriodicTimer(heartbeat ?? DefaultHeartbeat);
            while (await timer.WaitForNextTickAsync(ct))
                await client.WriteAsync(": ping\n\n", ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // Client went away — the normal way one of these ends.
        }
        finally
        {
            hub.Remove(key, client);
            logger.LogInformation("[Sse] unsubscribed key={Key}", key);
        }
    }
}
