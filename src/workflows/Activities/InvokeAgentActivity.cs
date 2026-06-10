using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Observability;
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
    private readonly bool _logSensitive;

    public InvokeAgentActivity(
        WorkflowCosmosService cosmos,
        IAgentRuntime runtime,
        ContextManager contextManager,
        WorkflowEventPublisher events,
        Microsoft.Extensions.Options.IOptions<TectikaAgents.Core.Configuration.FoundrySettings> foundry,
        Microsoft.Extensions.Options.IOptions<LoggingSettings> logging,
        ILogger<InvokeAgentActivity> logger)
    {
        _cosmos = cosmos;
        _runtime = runtime;
        _contextManager = contextManager;
        _events = events;
        _maxCompletionTokens = foundry.Value.MaxCompletionTokens;
        _logSensitive = logging.Value.LogSensitiveContent;
        _logger = logger;
    }

    [Function(nameof(InvokeAgentActivity))]
    public async Task<StepResult> Run(
        [ActivityTrigger] InvokeAgentInput input,
        FunctionContext executionContext)
    {
        var ct = executionContext.CancellationToken;
        var start = DateTimeOffset.UtcNow;

        _logger.LogInformation("[InvokeAgent] role={Role} task={Task} step={Step}",
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
        // Include validator feedback from QaFeedback edges so agents know what to fix on retry
        var qaFeedbackArtifacts = await _cosmos.GetQaFeedbackArtifactsAsync(task.BoardId, input.TaskId, ct);
        var allUpstreamArtifacts = upstreamArtifacts.Concat(qaFeedbackArtifacts).ToList();

        await _events.PublishStepStartedAsync(input.RunId, input.TaskId, input.Step, role.Id, ct);

        // ── 4. Build context + invoke agent runtime ──────────────────────────
        AgentRunOutcome outcome;
        try
        {
            var threadId = await _runtime.EnsureThreadAsync(task, ct);
            // Persist only when a new thread was created (EnsureThreadAsync sets it in-place); avoids a
            // redundant write on reuse and an orphan thread if a prior patch failed before commit.
            if (task.FoundryThreadId != threadId)
                await _cosmos.PatchTaskThreadIdAsync(input.BoardId, input.TaskId, threadId, ct);

            var userContent = await _contextManager.BuildUserContentAsync(role, task, board, allUpstreamArtifacts, ct);

            // If this task is a QA validator (has outgoing QaFeedback edge),
            // automatically inject revision-signaling instructions — no manual prompt config needed.
            var isValidator = await _cosmos.HasOutgoingQaFeedbackEdgeAsync(input.BoardId, input.TaskId, ct);
            if (isValidator)
            {
                var injectedInstruction =
                    "\n\n---\n[System: If you find issues that require the pipeline to re-run, " +
                    "end your response with:\n## REVISION_NEEDED\n<brief description of what needs to be fixed>]";
                userContent = userContent + injectedInstruction;
            }

            // Stream thinking text → agent_thinking SSE. Collect the publish tasks and await them AFTER the
            // turn — never block inside the synchronous OnText callback (deadlock risk). FoundryAgentRuntime
            // invokes OnText synchronously before RunTurnAsync returns, so all tasks are queued by then.
            var thinkingTasks = new List<Task>();
            if (_runtime is TectikaAgents.AgentRuntime.FoundryAgentRuntime fr)
            {
                fr.OnText = delta =>
                {
                    if (string.IsNullOrEmpty(delta)) return;
                    thinkingTasks.Add(_events.PublishAgentThinkingAsync(input.RunId, input.TaskId, input.Step, delta, ct));
                };
            }

            _logger.LogInformation("[InvokeAgent] invoking runtime role={Role} task={Task} step={Step} thread={Thread}",
                role.Id, input.TaskId, input.Step, threadId);
            _logger.LogDebug("[InvokeAgent] user content role={Role} task={Task} content={Content}",
                role.Id, input.TaskId, SensitiveContent.Format(userContent, _logSensitive));

            outcome = await _runtime.RunTurnAsync(
                new AgentRunRequest(role, task, threadId, userContent, _maxCompletionTokens, input.RunId, input.Step), ct);

            _logger.LogInformation("[InvokeAgent] runtime returned role={Role} task={Task} step={Step} status={Status} completion={Completion}",
                role.Id, input.TaskId, input.Step, outcome.Status, outcome.CompletionId);
            _logger.LogDebug("[InvokeAgent] runtime output role={Role} task={Task} content={Content}",
                role.Id, input.TaskId, SensitiveContent.Format(outcome.Content, _logSensitive));

            if (thinkingTasks.Count > 0)
                await Task.WhenAll(thinkingTasks);
            else if (!string.IsNullOrEmpty(outcome.Content)) // mock / no-stream: emit one thinking event
            {
                var preview = outcome.Content.Length > 400 ? outcome.Content[..400] : outcome.Content;
                await _events.PublishAgentThinkingAsync(input.RunId, input.TaskId, input.Step, preview, ct);
            }

            // BudgetExceeded means truncated output — fail rather than save partial content as success.
            if (outcome.Status is AgentRunStatus.Failed or AgentRunStatus.BudgetExceeded)
                throw new Exception(outcome.Error ?? $"Agent run ended with status {outcome.Status}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[InvokeAgent] invocation failed role={Role} task={Task} step={Step}",
                role.Id, input.TaskId, input.Step);
            throw;
        }

        // ── 5. Parse agent sections ───────────────────────────────────────────
        var (briefUpdate, artifactSummary, pendingInteraction, cleanContent, revisionReason) = ParseAgentSections(outcome.Content);
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
            Content     = cleanContent,
            Summary     = string.IsNullOrEmpty(artifactSummary) ? null : artifactSummary,
            Origin      = ArtifactOrigin.Agent,
            InternalLogs = [$"Agent: {role.DisplayName}", $"Step: {input.Step}", $"Model completion: {outcome.CompletionId}"],
            InputContext = new ArtifactInputContext
            {
                UpstreamArtifacts = allUpstreamArtifacts.Select(a => new UpstreamArtifactRef
                {
                    TaskId = a.TaskId,
                    ArtifactId = a.Id,
                    Version = a.Version,
                    ContentType = a.ContentType
                }).ToList()
            }
        };

        var savedArtifact = await _cosmos.CreateArtifactAsync(artifact, ct);

        // ── 8a. NeedsRevision early return ────────────────────────────────────
        if (!string.IsNullOrEmpty(revisionReason))
        {
            _logger.LogInformation("[InvokeAgent] REVISION_NEEDED task={Task} step={Step} reason={Reason}",
                input.TaskId, input.Step, SensitiveContent.Format(revisionReason, _logSensitive));
            var usage0 = new TokenUsage { Input = outcome.TokenUsage.Input, Output = outcome.TokenUsage.Output };
            await _events.PublishStepCompletedAsync(input.RunId, input.TaskId, input.Step, role.Id, usage0, ct);
            return new StepResult
            {
                Step         = input.Step,
                Status       = RunStatus.NeedsRevision,
                FoundryRunId = outcome.CompletionId,
                ArtifactId   = savedArtifact.Id,
                TokenUsage   = usage0,
                DurationMs   = (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds,
                CompletedAt  = DateTimeOffset.UtcNow,
                RevisionReason = revisionReason
            };
        }

        // ── 8. Update task: status + TaskBrief ───────────────────────────────
        await _cosmos.UpdateTaskStatusAsync(input.BoardId, input.TaskId, AgentTaskStatus.InProgress, input.RunId, ct);

        if (!string.IsNullOrEmpty(briefUpdate))
        {
            task.TaskBrief += $"\n[{role.DisplayName}, {input.RunId[..Math.Min(6, input.RunId.Length)]}, Step {input.Step}]: {briefUpdate}";
            await _cosmos.PatchTaskBriefAsync(input.BoardId, input.TaskId, task.TaskBrief, ct);
        }

        if (!string.IsNullOrEmpty(artifactSummary))
        {
            var summaryText = artifactSummary;
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(artifactSummary);
                if (doc.RootElement.TryGetProperty("summary", out var s))
                    summaryText = s.GetString() ?? summaryText;
            }
            catch { /* use raw */ }
            await _cosmos.PatchTaskArtifactSummaryAsync(input.BoardId, input.TaskId, summaryText, ct);
        }

        var usage = new TokenUsage { Input = outcome.TokenUsage.Input, Output = outcome.TokenUsage.Output };
        await _events.PublishStepCompletedAsync(input.RunId, input.TaskId, input.Step, role.Id, usage, ct);

        return new StepResult
        {
            Step               = input.Step,
            Status             = RunStatus.Completed,
            FoundryRunId       = outcome.CompletionId,
            ArtifactId         = savedArtifact.Id,
            TokenUsage         = usage,
            DurationMs         = (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds,
            CompletedAt        = DateTimeOffset.UtcNow,
            PendingInteraction = pendingInteraction
        };
    }

    private static (string Brief, string Summary, PendingInteractionRequest? Interaction, string CleanContent, string? RevisionReason) ParseAgentSections(string content)
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

        PendingInteractionRequest? interaction = null;
        var cleanContent = content;
        var interactionMarker = "## INTERACTION_REQUIRED";
        var interactionIdx = content.LastIndexOf(interactionMarker, StringComparison.OrdinalIgnoreCase);
        if (interactionIdx >= 0)
        {
            var jsonStart = content.IndexOf('{', interactionIdx);
            if (jsonStart >= 0)
            {
                var depth = 0;
                var jsonEnd = -1;
                for (var k = jsonStart; k < content.Length; k++)
                {
                    if (content[k] == '{') depth++;
                    else if (content[k] == '}') { depth--; if (depth == 0) { jsonEnd = k; break; } }
                }
                if (jsonEnd > jsonStart)
                {
                    var json = content[jsonStart..(jsonEnd + 1)];
                    try
                    {
                        interaction = System.Text.Json.JsonSerializer.Deserialize<PendingInteractionRequest>(json,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch { /* malformed JSON — ignore */ }
                    cleanContent = content[..interactionIdx].TrimEnd();
                }
            }
        }

        string? revisionReason = null;
        var revisionMarker = "## REVISION_NEEDED";
        var revisionIdx = content.LastIndexOf(revisionMarker, StringComparison.OrdinalIgnoreCase);
        if (revisionIdx >= 0)
        {
            revisionReason = content[(revisionIdx + revisionMarker.Length)..]
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "Revision needed";
            cleanContent = cleanContent[..Math.Min(cleanContent.Length, revisionIdx)].TrimEnd();
        }

        return (brief, summary, interaction, cleanContent, revisionReason);
    }
}

public record InvokeAgentInput(
    string RunId,
    string TaskId,
    string BoardId,
    string TenantId,
    string AgentRoleId,
    int Step);
