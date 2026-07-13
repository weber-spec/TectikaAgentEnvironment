using System.Collections.Concurrent;

namespace TectikaAgents.Api.Services;

/// <summary>
/// The run→board map, as a pure cache with no dependencies.
///
/// It is deliberately separate from <see cref="RunBoardResolver"/>: the data layer seeds this index every
/// time a run is persisted (<c>CreateRunAsync</c> — the single choke-point every run creation path goes
/// through, including the chat path, which bypasses <see cref="RunStartService"/>), while the resolver
/// reads Cosmos on a miss. Keeping the cache dependency-free is what stops that from being a DI cycle.
/// </summary>
public sealed class RunBoardIndex
{
    /// <summary>How long an unresolvable run id is remembered before we try again. It MUST expire: an event
    /// can be broadcast before its WorkflowRun lands in Cosmos, and a permanent negative would black-hole a
    /// live run's events for the rest of the process's life — the board would simply never show it.</summary>
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(45);

    private const int MaxEntries = 20_000;

    private readonly ConcurrentDictionary<string, string> _boardByRun = new();
    private readonly ConcurrentDictionary<string, string> _boardByTask = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _negativeUntil = new();
    private readonly TimeProvider _time;

    public RunBoardIndex(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public void Remember(string runId, string? taskId, string boardId)
    {
        if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(boardId)) return;
        Trim();
        _boardByRun[runId] = boardId;
        if (!string.IsNullOrEmpty(taskId)) _boardByTask[taskId!] = boardId;
        _negativeUntil.TryRemove(runId, out _);
    }

    /// <summary>Board for this run from cache alone. The taskId index also covers the CLI bridge, which
    /// advertises a synthetic <c>run-{taskId}</c> id that has no WorkflowRun document of its own.</summary>
    public bool TryGet(string runId, string? taskId, out string boardId)
    {
        if (_boardByRun.TryGetValue(runId, out var byRun)) { boardId = byRun; return true; }
        if (!string.IsNullOrEmpty(taskId) && _boardByTask.TryGetValue(taskId!, out var byTask))
        {
            _boardByRun[runId] = byTask;
            boardId = byTask;
            return true;
        }
        boardId = string.Empty;
        return false;
    }

    public bool IsKnownUnresolvable(string runId) =>
        _negativeUntil.TryGetValue(runId, out var until) && _time.GetUtcNow() < until;

    public void RememberUnresolvable(string runId) => _negativeUntil[runId] = _time.GetUtcNow() + NegativeTtl;

    private void Trim()
    {
        if (_boardByRun.Count <= MaxEntries) return;
        _boardByRun.Clear();
        _boardByTask.Clear();
        _negativeUntil.Clear();
    }
}

/// <summary>
/// Resolves the board a run belongs to, so an <c>AgentEvent</c> (which carries only runId + taskId) can be
/// fanned out to the board's multiplexed SSE stream as well as to its own run stream.
///
/// Why a resolver and not a boardId field on AgentEvent: adding the field would need a coordinated deploy —
/// during a rolling upgrade, events published by the old workflows build would arrive with boardId=null and
/// the board stream would be silently dead. The resolver has no such window, and CliBridgeManager only ever
/// holds taskId+runId anyway. Cost is one point read per *run*, not per event.
/// </summary>
public interface IRunBoardResolver
{
    void Remember(string runId, string? taskId, string boardId);

    /// <summary>The run's board, or null when it can't be resolved (event goes to its run stream only).</summary>
    ValueTask<string?> ResolveBoardIdAsync(string runId, string? taskId, CancellationToken ct = default);
}

public sealed class RunBoardResolver : IRunBoardResolver
{
    // Single-flight: the Service Bus listener processes up to 10 messages concurrently, so without this a
    // burst of events for a brand-new run would fire ten simultaneous point reads for the same document.
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();

    private readonly RunBoardIndex _index;
    private readonly ICosmosDbService _cosmos;
    private readonly ILogger<RunBoardResolver> _logger;

    public RunBoardResolver(RunBoardIndex index, ICosmosDbService cosmos, ILogger<RunBoardResolver> logger)
    {
        _index = index;
        _cosmos = cosmos;
        _logger = logger;
    }

    public void Remember(string runId, string? taskId, string boardId) => _index.Remember(runId, taskId, boardId);

    public ValueTask<string?> ResolveBoardIdAsync(string runId, string? taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(runId)) return ValueTask.FromResult<string?>(null);

        if (_index.TryGet(runId, taskId, out var known))
            return ValueTask.FromResult<string?>(known);

        // Runs are partitioned by taskId, so without one there is no point read to make.
        if (string.IsNullOrEmpty(taskId)) return ValueTask.FromResult<string?>(null);

        if (_index.IsKnownUnresolvable(runId)) return ValueTask.FromResult<string?>(null);

        return new ValueTask<string?>(LookupAsync(runId, taskId!, ct));
    }

    private async Task<string?> LookupAsync(string runId, string taskId, CancellationToken ct)
    {
        // The completion source is published to _inflight BEFORE the fetch starts, and removed only after it
        // finishes. Doing the removal inside the factory's own finally would race a synchronously-completing
        // fetch — it would remove the key before GetOrAdd inserted it, leaving a stale result cached forever.
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inflight = _inflight.GetOrAdd(runId, tcs.Task);
        if (!ReferenceEquals(inflight, tcs.Task))
            return await inflight;   // another event for this run is already fetching — ride along

        try
        {
            string? boardId = null;
            try
            {
                var run = await _cosmos.GetRunAsync(taskId, runId, ct);
                boardId = run?.BoardId;
                if (!string.IsNullOrEmpty(boardId))
                {
                    _index.Remember(runId, taskId, boardId!);
                    _logger.LogDebug("[RunBoard] resolved run={RunId} -> board={BoardId}", runId, boardId);
                }
                else
                {
                    // Not there (yet). Remember the miss briefly, so a chatty stream on an unresolvable run id
                    // — the CLI bridge's synthetic one, say — can't become a point read per event.
                    _index.RememberUnresolvable(runId);
                }
            }
            catch (Exception ex)
            {
                // Transient failure: do NOT record a negative, or a blip would mute the board for the TTL.
                _logger.LogDebug(ex, "[RunBoard] lookup failed run={RunId} task={TaskId}", runId, taskId);
            }

            tcs.SetResult(boardId);
            return boardId;
        }
        finally
        {
            _inflight.TryRemove(runId, out _);
        }
    }
}
