using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

/// <summary>
/// Activity — טוען AgentRole + upstream artifacts, קורא ל-Azure OpenAI,
/// שומר artifact ב-Cosmos, ומחזיר StepResult.
/// כל side effect מתבצע כאן (לא ב-Orchestrator) למניעת replay בעיות.
/// </summary>
public class InvokeAgentActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly IAgentRuntime _runtime;
    private readonly ContextManager _contextManager;
    private readonly WorkflowEventPublisher _events;
    private readonly ILogger<InvokeAgentActivity> _logger;
    private readonly int _maxCompletionTokens;

    public InvokeAgentActivity(
        WorkflowCosmosService cosmos,
        IAgentRuntime runtime,
        ContextManager contextManager,
        WorkflowEventPublisher events,
        Microsoft.Extensions.Options.IOptions<TectikaAgents.Core.Configuration.FoundrySettings> foundry,
        ILogger<InvokeAgentActivity> logger)
    {
        _cosmos = cosmos;
        _runtime = runtime;
        _contextManager = contextManager;
        _events = events;
        _maxCompletionTokens = foundry.Value.MaxCompletionTokens;
        _logger = logger;
    }

    [Function(nameof(InvokeAgentActivity))]
    public async Task<StepResult> Run(
        [ActivityTrigger] InvokeAgentInput input,
        FunctionContext executionContext)
    {
        var ct = executionContext.CancellationToken;
        var start = DateTimeOffset.UtcNow;

        _logger.LogInformation("InvokeAgent: role={Role} task={Task} step={Step}",
            input.AgentRoleId, input.TaskId, input.Step);

        // ── 1. Load AgentRole ─────────────────────────────────────────────────
        var role = await _cosmos.GetAgentRoleAsync(input.TenantId, input.AgentRoleId, ct)
            ?? throw new Exception($"AgentRole '{input.AgentRoleId}' not found in tenant '{input.TenantId}'");

        // ── 2. Load task + board ──────────────────────────────────────────────
        var task = await _cosmos.GetTaskAsync(input.BoardId, input.TaskId, ct)
            ?? throw new Exception($"Task '{input.TaskId}' not found in board '{input.BoardId}'");

        var board = await _cosmos.GetBoardAsync(input.BoardId, ct)
            ?? throw new Exception($"Board '{input.BoardId}' not found");

        // ── 3. Load upstream artifacts ────────────────────────────────────────
        var upstreamTaskIds = await _cosmos.GetUpstreamTaskIdsAsync(task.BoardId, input.TaskId, ct);
        var upstreamArtifacts = await _cosmos.GetUpstreamArtifactsAsync(upstreamTaskIds, ct);

        await _events.PublishStepStartedAsync(input.RunId, input.TaskId, input.Step, role.Id, ct);

        // ── 4. Build context + invoke agent runtime ──────────────────────────
        AgentRunOutcome outcome;
        try
        {
            var threadId = await _runtime.EnsureThreadAsync(task, ct);
            await _cosmos.PatchTaskThreadIdAsync(input.BoardId, input.TaskId, threadId, ct);

            var userContent = await _contextManager.BuildUserContentAsync(role, task, board, upstreamArtifacts, ct);

            // Stream thinking text → agent_thinking SSE. Real runtime streams (once, for the poll impl);
            // ensure at least one event in mock mode too.
            bool publishedThinking = false;
            if (_runtime is TectikaAgents.AgentRuntime.FoundryAgentRuntime fr)
            {
                fr.OnText = delta =>
                {
                    if (string.IsNullOrEmpty(delta)) return;
                    publishedThinking = true;
                    _events.PublishAgentThinkingAsync(input.RunId, input.TaskId, input.Step, delta, ct).GetAwaiter().GetResult();
                };
            }

            outcome = await _runtime.RunTurnAsync(
                new AgentRunRequest(role, task, threadId, userContent, _maxCompletionTokens, input.RunId, input.Step), ct);

            if (!publishedThinking && !string.IsNullOrEmpty(outcome.Content))
            {
                var preview = outcome.Content.Length > 400 ? outcome.Content[..400] : outcome.Content;
                await _events.PublishAgentThinkingAsync(input.RunId, input.TaskId, input.Step, preview, ct);
            }

            if (outcome.Status == AgentRunStatus.Failed)
                throw new Exception(outcome.Error ?? "Agent run failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent invocation failed for role {Role}", role.Id);
            throw;
        }

        // ── 5. Parse agent sections ───────────────────────────────────────────
        var (briefUpdate, artifactSummary) = ParseAgentSections(outcome.Content);
        if (!string.IsNullOrEmpty(outcome.BriefUpdate))
            briefUpdate = outcome.BriefUpdate;

        // ── 6. Determine next artifact version ────────────────────────────────
        var existingArtifacts = await _cosmos.GetUpstreamArtifactsAsync([input.TaskId], ct);

        var nextVersion = (existingArtifacts.MaxBy(a => a.Version)?.Version ?? 0) + 1;

        // ── 7. Save artifact to Cosmos ────────────────────────────────────────
        var artifact = new Artifact
        {
            TaskId      = input.TaskId,
            RunId       = input.RunId,
            TenantId    = input.TenantId,
            Version     = nextVersion,
            ContentType = outcome.ContentType,
            Content     = outcome.Content,
            Summary     = string.IsNullOrEmpty(artifactSummary) ? null : artifactSummary,
            Origin      = ArtifactOrigin.Agent,
            InternalLogs = [$"Agent: {role.DisplayName}", $"Step: {input.Step}", $"Model completion: {outcome.CompletionId}"],
            InputContext = new ArtifactInputContext
            {
                UpstreamArtifacts = upstreamArtifacts.Select(a => new UpstreamArtifactRef
                {
                    TaskId = a.TaskId,
                    ArtifactId = a.Id,
                    Version = a.Version,
                    ContentType = a.ContentType
                }).ToList()
            }
        };

        var savedArtifact = await _cosmos.CreateArtifactAsync(artifact, ct);

        // ── 8. Update task: status + TaskBrief ───────────────────────────────
        await _cosmos.UpdateTaskStatusAsync(input.BoardId, input.TaskId, AgentTaskStatus.InProgress, input.RunId, ct);

        if (!string.IsNullOrEmpty(briefUpdate))
        {
            task.TaskBrief += $"\n[{role.DisplayName}, {input.RunId[..Math.Min(6, input.RunId.Length)]}, Step {input.Step}]: {briefUpdate}";
            await _cosmos.PatchTaskBriefAsync(input.BoardId, input.TaskId, task.TaskBrief, ct);
        }

        var usage = new TokenUsage { Input = outcome.TokenUsage.Input, Output = outcome.TokenUsage.Output };
        await _events.PublishStepCompletedAsync(input.RunId, input.TaskId, input.Step, role.Id, usage, ct);

        return new StepResult
        {
            Step         = input.Step,
            Status       = RunStatus.Completed,
            FoundryRunId = outcome.CompletionId,
            ArtifactId   = savedArtifact.Id,
            TokenUsage   = usage,
            DurationMs   = (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds,
            CompletedAt  = DateTimeOffset.UtcNow
        };
    }

    private static (string Brief, string Summary) ParseAgentSections(string content)
    {
        string ExtractFirstNonEmptyLine(string marker)
        {
            var idx = content.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            return content[(idx + marker.Length)..]
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        }

        var brief = ExtractFirstNonEmptyLine("## Brief Update");

        var summary = "";
        var summaryIdx = content.LastIndexOf("## Artifact Summary", StringComparison.OrdinalIgnoreCase);
        if (summaryIdx >= 0)
        {
            var jsonStart = content.IndexOf('{', summaryIdx);
            var jsonEnd   = content.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                summary = content[jsonStart..(jsonEnd + 1)];
        }

        return (brief, summary);
    }
}

public record InvokeAgentInput(
    string RunId,
    string TaskId,
    string BoardId,
    string TenantId,
    string AgentRoleId,
    int Step);
