using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;

namespace TectikaAgents.Api.Controllers;

/// <summary>
/// The board's multiplexed SSE stream: every run event for every task on the board, over ONE connection.
///
/// The board page used to open one EventSource per task-with-a-run. Browsers cap concurrent connections
/// per origin at ~6 on HTTP/1.1, so a board with a handful of finished runs exhausted the pool and every
/// subsequent fetch hung forever — no error, just a pending request (that's why "Reset &amp; run" silently
/// did nothing and the board sat on "Loading…"). Connection count is now O(1) in the number of tasks.
/// </summary>
[ApiController]
[Route("api/boards/{boardId}/stream")]
[Authorize]
public class BoardStreamController : ControllerBase
{
    private readonly SseHub _hub;
    private readonly ILogger<BoardStreamController> _logger;

    public BoardStreamController(SseHub hub, ILogger<BoardStreamController> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <remarks>
    /// AllowAnonymous is forced, not chosen: EventSource cannot attach an Authorization header, which is why
    /// every SSE route here is anonymous (see RunsController.Stream). Note this widens exposure from "one
    /// run's events, if you know the runId" to "the whole board's events, if you know the boardId" — and the
    /// boardId sits in the URL of every board page. Fine for dev; a short-lived query-string token is
    /// required before this ships to production.
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    public Task Get(string boardId, CancellationToken ct) =>
        SseEndpoint.RunAsync(Response, _hub, SseKeys.Board(boardId), ct, _logger);
}
