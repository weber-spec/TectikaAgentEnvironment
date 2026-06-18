using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.AgentRuntime;
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
    private readonly IAgentProvisioner _provisioner;
    private readonly ContextManager _contextManager;
    private readonly WorkflowEventPublisher _events;
    private readonly IWorkspaceService _workspace;
    private readonly UsageRecorder _usage;
    private readonly int _maxCompletionTokens;
    private readonly string _defaultModel;
    private readonly string _provider;
    private readonly ILogger<RunAgentRoundActivity> _logger;

    public RunAgentRoundActivity(
        WorkflowCosmosService cosmos,
        IAgentRuntime runtime,
        IAgentProvisioner provisioner,
        ContextManager contextManager,
        WorkflowEventPublisher events,
        IWorkspaceService workspace,
        UsageRecorder usage,
        IOptions<FoundrySettings> foundry,
        ILogger<RunAgentRoundActivity> logger)
    {
        _cosmos = cosmos;
        _runtime = runtime;
        _provisioner = provisioner;
        _contextManager = contextManager;
        _events = events;
        _workspace = workspace;
        _usage = usage;
        _maxCompletionTokens = foundry.Value.MaxCompletionTokens;
        _defaultModel = foundry.Value.DefaultModel;
        _provider = foundry.Value.IsOpenAiDirect ? "openai" : "azure-foundry";
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
        _logger.LogInformation("[RunAgentRound] board={Board} hasGitHub={HasGitHub} owner={Owner}",
            input.BoardId, board.GitHub != null, board.GitHub?.Owner ?? "(null)");

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
            // Self-heal: if the agent's stored definition is stale (e.g. the tool schema added the
            // terminal tool), republish it so the model actually knows its current tools.
            if (await AgentSelfHeal.EnsureCurrentAsync(_provisioner, role, ct))
                await _cosmos.UpsertAgentRoleAsync(role, ct);

            var upstreamIds = await _cosmos.GetUpstreamTaskIdsAsync(task.BoardId, input.TaskId, ct);
            var upstream = await _cosmos.GetUpstreamArtifactsAsync(upstreamIds, ct);
            var qa = await _cosmos.GetQaFeedbackArtifactsAsync(task.BoardId, input.TaskId, ct);
            var context = await _contextManager.BuildUserContentAsync(role, task, board, upstream.Concat(qa).ToList(), ct);
            userInput = string.IsNullOrEmpty(input.UserInput)
                ? context
                : context + "\n\n## User message\n" + input.UserInput;

            userInput += WorkspacePrompt(role.Permissions.CanUseWorkspace, board.GitHub is not null);
        }

        var explorer = new BoardProjectExplorer(_cosmos, input.BoardId, input.TenantId);
        var outcome = await _runtime.RunRoundAsync(
            new RoundRequest(role, task, threadId, userInput, input.PendingToolOutputs, _maxCompletionTokens, input.RunId, input.Round)
            {
                BoardGitHub = board.GitHub,
                Workspace = role.Permissions.CanUseWorkspace
                    ? new RunWorkspaceProvider(_cosmos, _workspace, board, input.RunId, _logger)
                    : null,
            },
            explorer, ct);

        // Fold a tool-driven brief update into the task brief.
        if (!string.IsNullOrEmpty(outcome.BriefUpdate))
        {
            task.TaskBrief += $"\n[{role.DisplayName}, {Short(input.RunId)}, Round {input.Round}]: {outcome.BriefUpdate}";
            await _cosmos.PatchTaskBriefAsync(input.BoardId, input.TaskId, task.TaskBrief, ct);
        }

        // Per-run declared outputs: reset at the start of a run, fold in this round's declare/update/remove ops.
        if (input.Round == 0)
            task.PendingOutputs = [];
        if (outcome.OutputOps is { Count: > 0 })
            task.PendingOutputs = OutputAccumulator.Apply(task.PendingOutputs, outcome.OutputOps);
        if (input.Round == 0 || outcome.OutputOps is { Count: > 0 })
            await _cosmos.PatchTaskPendingOutputsAsync(input.BoardId, input.TaskId, task.PendingOutputs, ct);

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
                Content = outcome.FinalText ?? "",        // back-compat: Content == summary; existing readers + EnsureHandoffShape still work
                Summary = outcome.FinalText ?? "",         // the agent's final message is the handoff summary
                Outputs = task.PendingOutputs.Where(o => o.IsValid()).ToList(),  // deliberately declared deliverables
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

        // Record usage to the ledger + rollups (per round). Session = the task's current session id.
        var model = role.ModelOverride ?? _defaultModel;
        await _usage.RecordAsync(new UsageRecorder.Attribution(
            TenantId: input.TenantId, BoardId: input.BoardId, TaskId: input.TaskId, RunId: input.RunId,
            Step: 0, Round: input.Round, InvocationId: ctx.InvocationId,
            AgentRoleId: role.Id, AgentRoleName: role.DisplayName,
            Provider: _provider, Model: model, ModelVersion: null,
            SessionId: task.UsageSessionId ?? input.RunId),
            outcome.Usage, ct);

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

    public static string WorkspacePrompt(bool canUseWorkspace, bool repoConnected)
    {
        if (!canUseWorkspace)
            return "\n\n## Sandbox\nYou do not have sandbox (workspace) access. If the task requires running " +
                   "code or git operations, inform the user that your role does not have workspace permission " +
                   "and ask them to reassign the task to an agent that does.";

        const string table =
            "\n\n## Sandbox & File Tools\n\n" +
            "You have dedicated tools for file operations — **prefer them over `run_command`** for anything file-related:\n\n" +
            "| Task | Use | Not |\n" +
            "|------|-----|-----|\n" +
            "| Read a file | `read_file` | `run_command cat` |\n" +
            "| Write a new file | `write_file` | `run_command` + heredoc |\n" +
            "| Edit part of a file | `edit_file` | `run_command sed/awk` |\n" +
            "| List directory | `list_dir` | `run_command ls` |\n" +
            "| Search code | `search_code` | `run_command grep` |\n\n" +
            "Workflow: `read_file` → understand → `edit_file` (new files: `write_file`).\n" +
            "Use `run_command` for: builds, tests, git operations, package installs, and other shell execution.\n";

        return repoConnected
            ? table + "\nOn first `run_command` use, the connected GitHub repository is cloned to `/workspace` " +
              "with git configured (you can `git commit`/`git push`)."
            : table + "\nYour sandbox is `/workspace` — an empty directory with no git repo. " +
              "Use `run_command` for builds, tests, and other shell execution.";
    }

    private sealed class RunWorkspaceProvider : IWorkspaceProvider
    {
        private readonly WorkflowCosmosService _cosmos;
        private readonly IWorkspaceService _workspace;
        private readonly Board _board;
        private readonly string _runId;
        private readonly ILogger _logger;
        private WorkspaceConnection? _cached;

        public RunWorkspaceProvider(WorkflowCosmosService cosmos, IWorkspaceService workspace, Board board, string runId, ILogger logger)
        { _cosmos = cosmos; _workspace = workspace; _board = board; _runId = runId; _logger = logger; }

        public async Task<WorkspaceConnection?> EnsureAsync(CancellationToken ct = default)
        {
            if (_cached is not null) return _cached;
            var run = await _cosmos.GetRunByIdAsync(_runId, ct);
            if (run is { WorkspaceEndpoint: not null, WorkspaceToken: not null })
                return _cached = new WorkspaceConnection(run.WorkspaceEndpoint, run.WorkspaceToken);
            try
            {
                var branch = $"agent/{_runId[..Math.Min(8, _runId.Length)]}";
                var info = await _workspace.ProvisionAsync(_board, branch, _runId, ct);
                if (info is null) return null;
                await _cosmos.PatchRunWorkspaceAsync(_runId, info.ContainerName, info.Endpoint, info.Token, ct);
                return _cached = new WorkspaceConnection(info.Endpoint, info.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RunAgentRound] sandbox provisioning failed for run {RunId}", _runId);
                return null;
            }
        }
    }
}

public record RoundActivityInput(
    string RunId,
    string TaskId,
    string BoardId,
    string TenantId,
    string AgentRoleId,
    int Round,
    string? UserInput,
    List<PriorToolOutput> PendingToolOutputs);

public record RoundActivityResult(RoundOutcome Outcome, string? ArtifactId);
