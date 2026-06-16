using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

/// <summary>
/// Activity — runs ONE steerable round (fine-grained Shape B). All side effects (Foundry call,
/// Cosmos) happen here, never in the orchestrator. On round 0 it assembles task context; on
/// <see cref="RoundKind.Final"/> it writes the artifact and updates the task. Trace/SSE events are
/// added in Stage 3b.
/// </summary>
public class RunAgentRoundActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly IAgentRuntime _runtime;
    private readonly ContextManager _contextManager;
    private readonly WorkflowEventPublisher _events;
    private readonly int _maxCompletionTokens;
    private readonly ILogger<RunAgentRoundActivity> _logger;

    public RunAgentRoundActivity(
        WorkflowCosmosService cosmos,
        IAgentRuntime runtime,
        ContextManager contextManager,
        WorkflowEventPublisher events,
        IOptions<FoundrySettings> foundry,
        ILogger<RunAgentRoundActivity> logger)
    {
        _cosmos = cosmos;
        _runtime = runtime;
        _contextManager = contextManager;
        _events = events;
        _maxCompletionTokens = foundry.Value.MaxCompletionTokens;
        _logger = logger;
    }

    [Function(nameof(RunAgentRoundActivity))]
    public async Task<RoundActivityResult> Run([ActivityTrigger] RoundActivityInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;
        _logger.LogInformation("[RunAgentRound] role={Role} task={Task} round={Round}", input.AgentRoleId, input.TaskId, input.Round);

        var role = await _cosmos.GetAgentRoleAsync(input.TenantId, input.AgentRoleId, ct)
            ?? throw new Exception($"AgentRole '{input.AgentRoleId}' not found in tenant '{input.TenantId}'");
        var task = await _cosmos.GetTaskAsync(input.BoardId, input.TaskId, ct)
            ?? throw new Exception($"Task '{input.TaskId}' not found in board '{input.BoardId}'");
        var board = await _cosmos.GetBoardAsync(input.BoardId, input.TenantId, ct)
            ?? throw new Exception($"Board '{input.BoardId}' not found");

        // EnsureThreadAsync mutates task.FoundryThreadId in place, so capture whether it existed
        // BEFORE the call. Otherwise the guard never fires, the thread is never persisted, and every
        // round creates a fresh Foundry conversation (orphaning the prior round's tool calls).
        var hadThread = !string.IsNullOrEmpty(task.FoundryThreadId);
        var threadId = await _runtime.EnsureThreadAsync(task, ct);
        if (!hadThread)
            await _cosmos.PatchTaskThreadIdAsync(input.BoardId, input.TaskId, threadId, ct);

        // Round 0 seeds the conversation with assembled task context (+ any user/chat message).
        var userInput = input.UserInput;
        if (input.Round == 0)
        {
            var upstreamIds = await _cosmos.GetUpstreamTaskIdsAsync(task.BoardId, input.TaskId, ct);
            var upstream = await _cosmos.GetUpstreamArtifactsAsync(upstreamIds, ct);
            var qa = await _cosmos.GetQaFeedbackArtifactsAsync(task.BoardId, input.TaskId, ct);
            var context = await _contextManager.BuildUserContentAsync(role, task, board, upstream.Concat(qa).ToList(), ct);
            userInput = string.IsNullOrEmpty(input.UserInput)
                ? context
                : context + "\n\n## User message\n" + input.UserInput;
        }

        var explorer = new BoardProjectExplorer(_cosmos, input.BoardId, input.TenantId);
        var outcome = await _runtime.RunRoundAsync(
            new RoundRequest(role, task, threadId, userInput, input.PendingToolOutputs, _maxCompletionTokens, input.RunId, input.Round)
            {
                BoardGitHub = board.GitHub,
                WorkspaceEndpoint = input.WorkspaceEndpoint,
                WorkspaceToken = input.WorkspaceToken,
            },
            explorer, ct);

        // Fold a tool-driven brief update into the task brief.
        if (!string.IsNullOrEmpty(outcome.BriefUpdate))
        {
            task.TaskBrief += $"\n[{role.DisplayName}, {Short(input.RunId)}, Round {input.Round}]: {outcome.BriefUpdate}";
            await _cosmos.PatchTaskBriefAsync(input.BoardId, input.TaskId, task.TaskBrief, ct);
        }

        string? artifactId = null;
        if (outcome.Kind == RoundKind.Final && outcome.Error is null)
        {
            var existing = await _cosmos.GetUpstreamArtifactsAsync([input.TaskId], ct);
            var nextVersion = (existing.MaxBy(a => a.Version)?.Version ?? 0) + 1;
            var artifact = new Artifact
            {
                TaskId = input.TaskId,
                RunId = input.RunId,
                TenantId = input.TenantId,
                Version = nextVersion,
                ContentType = ArtifactContentType.Markdown,
                Content = outcome.FinalText ?? "",
                Origin = ArtifactOrigin.Agent,
                InternalLogs = [$"Agent: {role.DisplayName}", $"Round: {input.Round}", $"Completion: {outcome.CompletionId}"],
            };
            var saved = await _cosmos.CreateArtifactAsync(artifact, ct);
            artifactId = saved.Id;
            await _cosmos.UpdateTaskStatusAsync(input.BoardId, input.TaskId, AgentTaskStatus.Done, input.RunId, ct);
        }
        else
        {
            await _cosmos.UpdateTaskStatusAsync(input.BoardId, input.TaskId, AgentTaskStatus.InProgress, input.RunId, ct);
        }

        // Persist the round trace (hierarchical) and mirror each event over SSE — live and stored share one shape.
        foreach (var ev in RunEventFactory.BuildRoundEvents(input.RunId, input.TaskId, input.Round, outcome, artifactId))
        {
            var saved = await _cosmos.CreateRunEventAsync(ev, ct);
            await _events.PublishRunEventAsync(saved, ct);
        }

        // A steerable control tool paused the run — persist a HumanInteraction so the request surfaces
        // in the Approvals tab + notifications (and the chat), answerable from any of them.
        if (outcome.Kind == RoundKind.AwaitUser && outcome.Control is not null)
        {
            var interaction = SteerableInteractionFactory.Build(
                input.RunId, input.TaskId, input.BoardId, input.TenantId, input.Round,
                task.HumanAuditorId, outcome.Control);
            var savedInteraction = await _cosmos.UpsertInteractionAsync(interaction, ct);
            await _events.PublishInteractionRequiredAsync(
                input.RunId, input.TaskId, input.Round, savedInteraction.Id, savedInteraction.Type.ToString(), ct);
        }

        return new RoundActivityResult(outcome, artifactId);
    }

    private static string Short(string s) => s[..Math.Min(6, s.Length)];
}

public record RoundActivityInput(
    string RunId,
    string TaskId,
    string BoardId,
    string TenantId,
    string AgentRoleId,
    int Round,
    string? UserInput,
    List<PriorToolOutput> PendingToolOutputs,
    string? WorkspaceEndpoint = null,
    string? WorkspaceToken = null);

public record RoundActivityResult(RoundOutcome Outcome, string? ArtifactId);
