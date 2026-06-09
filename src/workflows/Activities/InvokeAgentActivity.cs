using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
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
    private readonly WorkflowAgentRunner _runner;
    private readonly ContextManager _contextManager;
    private readonly WorkflowEventPublisher _events;
    private readonly ILogger<InvokeAgentActivity> _logger;

    public InvokeAgentActivity(
        WorkflowCosmosService cosmos,
        WorkflowAgentRunner runner,
        ContextManager contextManager,
        WorkflowEventPublisher events,
        ILogger<InvokeAgentActivity> logger)
    {
        _cosmos = cosmos;
        _runner = runner;
        _contextManager = contextManager;
        _events = events;
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
        var upstreamArtifacts = await _cosmos.GetUpstreamArtifactsAsync(task.UpstreamTaskIds, ct);

        await _events.PublishStepStartedAsync(input.RunId, input.TaskId, input.Step, role.Id, ct);

        // ── 4. Build context + invoke LLM ────────────────────────────────────
        AgentInvocationResult result;
        try
        {
            var messages = await _contextManager.BuildContextAsync(role, task, board, upstreamArtifacts, ct);
            result = await _runner.InvokeWithMessagesAsync(messages, role, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent invocation failed for role {Role}", role.Id);
            throw;
        }

        // ── 5. Parse agent sections ───────────────────────────────────────────
        var (briefUpdate, artifactSummary) = ParseAgentSections(result.Content);

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
            ContentType = result.ContentType,
            Content     = result.Content,
            Summary     = string.IsNullOrEmpty(artifactSummary) ? null : artifactSummary,
            Origin      = ArtifactOrigin.Agent,
            InternalLogs = [$"Agent: {role.DisplayName}", $"Step: {input.Step}", $"Model completion: {result.CompletionId}"],
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

        var usage = new TokenUsage { Input = result.InputTokens, Output = result.OutputTokens };
        await _events.PublishStepCompletedAsync(input.RunId, input.TaskId, input.Step, role.Id, usage, ct);

        return new StepResult
        {
            Step         = input.Step,
            Status       = RunStatus.Completed,
            FoundryRunId = result.CompletionId,
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
