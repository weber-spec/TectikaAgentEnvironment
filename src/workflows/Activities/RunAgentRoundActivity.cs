using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.AgentRuntime;
using TectikaAgents.AgentRuntime.GitHub;
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
    private readonly ISecretProvider _secrets;
    private readonly IGitHubReadService _ghRead;
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
        ISecretProvider secrets,
        IGitHubReadService ghRead,
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
        _secrets = secrets;
        _ghRead = ghRead;
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
        // On continuation runs that reuse an existing Foundry thread (hadThread == true), the model
        // already has the full context from the first run — resending it every time causes the thread
        // to accumulate duplicate instructions and confuses the model. Only seed the full context when
        // opening a brand-new thread.
        var userInput = input.UserInput;
        if (input.Round == 0)
        {
            // Self-heal: if the agent's stored definition is stale (e.g. the tool schema added the
            // terminal tool), republish it so the model actually knows its current tools.
            if (await AgentSelfHeal.EnsureCurrentAsync(_provisioner, role, ct))
                await _cosmos.UpsertAgentRoleAsync(role, ct);

            if (!hadThread)
            {
                var upstreamIds = await _cosmos.GetUpstreamTaskIdsAsync(task.BoardId, input.TaskId, ct);
                var upstream = await _cosmos.GetUpstreamArtifactsAsync(upstreamIds, ct);
                var qa = await _cosmos.GetQaFeedbackArtifactsAsync(task.BoardId, input.TaskId, ct);
                var context = await _contextManager.BuildUserContentAsync(role, task, board, upstream.Concat(qa).ToList(), ct);

                // Validator (QA) awareness: a task with an outgoing QaFeedback edge gates an upstream task.
                // Tell it to use request_revision, which automatically re-runs that upstream loop target.
                if (await _cosmos.HasOutgoingQaFeedbackEdgeAsync(input.BoardId, input.TaskId, ct))
                    context += ValidatorPrompt;

                userInput = string.IsNullOrEmpty(input.UserInput)
                    ? context
                    : context + "\n\n## User message\n" + input.UserInput;
            }
            // Workspace prompt is sent:
            //   - Always on a new thread (!hadThread) — covers both button-triggered and chat-triggered first runs.
            //   - On existing threads when there is no user seed message (button re-trigger reminder).
            // NOT sent on chat continuations on existing threads (agent already knows the workspace from history).
            if (!hadThread || string.IsNullOrEmpty(input.UserInput))
                userInput += WorkspacePrompt(role.Permissions.CanUseWorkspace, board.GitHub is not null, role.Permissions.CanPushCode,
                    $"agent/{input.RunId[..Math.Min(8, input.RunId.Length)]}");
        }

        var explorer = new BoardProjectExplorer(_cosmos, input.BoardId, input.TenantId);
        var outcome = await _runtime.RunRoundAsync(
            new RoundRequest(role, task, threadId, userInput, input.PendingToolOutputs, _maxCompletionTokens, input.RunId, input.Round)
            {
                BoardGitHub = board.GitHub,
                Workspace = role.Permissions.CanUseWorkspace
                    ? new RunWorkspaceProvider(_cosmos, _workspace, _secrets, board, input.RunId, input.TaskId, role.Permissions.CanPushCode, _logger)
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
        if (outcome.Error is not null)
        {
            // Infra/sandbox failure (e.g. the workspace couldn't start). Fail the task cleanly — produce no
            // artifact and don't leave it stranded InProgress. The run itself is marked Failed by the
            // orchestrator (OnStateAsync) carrying this same error message, so the user sees why it stopped.
            await _cosmos.UpdateTaskStatusAsync(input.BoardId, input.TaskId, AgentTaskStatus.Failed, input.RunId, ct);
        }
        else if (outcome.Kind == RoundKind.Final)
        {
            // Only push for roles permitted to (CanPushCode); the sandbox's push remote is disabled
            // otherwise, so an ungated push would just fail and log noise every run.
            if (board.GitHub is not null && role.Permissions.CanPushCode) await TryPushBranchAsync(input.RunId, ct);

            var existing = await _cosmos.GetUpstreamArtifactsAsync([input.TaskId], ct);
            var nextVersion = (existing.MaxBy(a => a.Version)?.Version ?? 0) + 1;

            var outputs = task.PendingOutputs.Where(o => o.IsValid()).ToList();  // deliberately declared deliverables
            var codeOutput = await TryBuildCodeOutputAsync(board, input.RunId, ct);  // automatic code deliverable
            if (codeOutput is not null) outputs.Add(codeOutput);

            var artifact = new Artifact
            {
                TaskId = input.TaskId,
                RunId = input.RunId,
                TenantId = input.TenantId,
                Version = nextVersion,
                ContentType = ArtifactContentType.Markdown,
                Content = outcome.FinalText ?? "",        // back-compat: Content == summary; existing readers + EnsureHandoffShape still work
                Summary = outcome.FinalText ?? "",         // the agent's final message is the handoff summary
                Outputs = outputs,
                Origin = ArtifactOrigin.Agent,
                InternalLogs = [$"Agent: {role.DisplayName}", $"Round: {input.Round}", $"Completion: {outcome.CompletionId}"],
            };
            var saved = await _cosmos.CreateArtifactAsync(artifact, ct);
            artifactId = saved.Id;
            await _cosmos.UpdateTaskStatusAsync(input.BoardId, input.TaskId, AgentTaskStatus.Done, input.RunId, ct);
        }
        else if (outcome.Kind == RoundKind.NeedsRevision)
        {
            // Validator requested an upstream re-run. Persist its feedback as an artifact so the
            // QaFeedback edge's loop target reads it on re-run. Task status is set by the orchestrator
            // (NeedsRevision → Review → QA loop) — don't mark it Done/InProgress here.
            var reason = outcome.Control?.Text ?? "Revision requested.";
            var existing = await _cosmos.GetUpstreamArtifactsAsync([input.TaskId], ct);
            var nextVersion = (existing.MaxBy(a => a.Version)?.Version ?? 0) + 1;
            var artifact = new Artifact
            {
                TaskId = input.TaskId,
                RunId = input.RunId,
                TenantId = input.TenantId,
                Version = nextVersion,
                ContentType = ArtifactContentType.Markdown,
                Content = reason,
                Summary = reason,
                Outputs = task.PendingOutputs.Where(o => o.IsValid()).ToList(),
                Origin = ArtifactOrigin.Agent,
                InternalLogs = [$"Agent: {role.DisplayName}", $"Round: {input.Round}", "Disposition: NeedsRevision", $"Completion: {outcome.CompletionId}"],
            };
            var saved = await _cosmos.CreateArtifactAsync(artifact, ct);
            artifactId = saved.Id;
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

    // Best-effort: push the run branch to origin so its commits are durable + enrichable.
    private async Task TryPushBranchAsync(string runId, CancellationToken ct)
    {
        try
        {
            var run = await _cosmos.GetRunByIdAsync(runId, ct);
            if (run?.WorkspaceEndpoint is null || run.WorkspaceToken is null) return; // no workspace was used
            var branchName = $"agent/{runId[..Math.Min(8, runId.Length)]}";
            var runIdShort = runId[..Math.Min(8, runId.Length)];
            var res = await _workspace.RunCommandAsync(run.WorkspaceEndpoint, run.WorkspaceToken,
                $"cd /workspace/runs/{runIdShort} && git push origin {branchName}", 120, runIdShort, ct);
            if (res.ExitCode == 0)
                _logger.LogInformation("[RunAgentRound] finalization push run={RunId} ok", runId);
            else
                _logger.LogWarning("[RunAgentRound] finalization push run={RunId} non-zero exit={Exit} stderr={Stderr}", runId, res.ExitCode, res.Stderr);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[RunAgentRound] finalization push failed run={RunId}", runId); }
    }

    // Best-effort: build the Code output for this run's branch (null if no repo / no changes / error).
    private async Task<Output?> TryBuildCodeOutputAsync(Board board, string runId, CancellationToken ct)
    {
        if (board.GitHub is null) return null;
        var head = $"agent/{runId[..Math.Min(8, runId.Length)]}";
        try
        {
            var meta = await _ghRead.GetRepoMetadataAsync(board.GitHub, ct);
            var cmp = await _ghRead.CompareAsync(board.GitHub, meta.DefaultBranch, head, ct);
            if (cmp.FilesChanged == 0) return null;
            var prs = await _ghRead.ListPullRequestsAsync(board.GitHub, "all", ct);
            var pr = prs.FirstOrDefault(p => p.Head == head);
            await _cosmos.PatchRunBranchAsync(runId, head, pr?.Number, ct);
            return CodeOutputBuilder.Build(board.GitHub, meta.DefaultBranch, head, cmp, pr);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[RunAgentRound] code-output enrichment failed run={RunId}", runId); return null; }
    }

    private static string Short(string s) => s[..Math.Min(6, s.Length)];

    /// <summary>Appended to a validator's round-0 context (task has an outgoing QaFeedback edge). Tells it
    /// to use the request_revision tool, which auto-re-runs the upstream loop target.</summary>
    public const string ValidatorPrompt =
        "\n\n## You are a QA validator for this pipeline\n" +
        "This task gates an upstream task. Its deliverable is the upstream's **declared output** shown above — " +
        "a summary plus any linked files, which are merged into your base branch (open them with `read_file` / " +
        "`list_dir`). Review THAT against the requirements.\n" +
        "- Judge the deliverable's substance. Do NOT request revision merely because a file isn't where you " +
        "expected or looks 'missing from the repo' — read the declared output and its linked files first; they " +
        "are the deliverable, and a git repo is optional.\n" +
        "- If it is acceptable, finish normally (your summary becomes the validation record).\n" +
        "- If it genuinely needs rework, call the `request_revision` tool with a clear, specific reason. " +
        "That AUTOMATICALLY re-runs the upstream task with your feedback — do not ask a human, and do not " +
        "try to fix the upstream work yourself. State exactly what must change so the re-run can address it.";

    public static string WorkspacePrompt(bool canUseWorkspace, bool repoConnected, bool canPushCode, string? branchName = null)
    {
        if (!canUseWorkspace)
            return "\n\n## Sandbox\nYou do not have sandbox (workspace) access. If the task requires running " +
                   "code or git operations, inform the user that your role does not have workspace permission " +
                   "and ask them to reassign the task to an agent that does.";

        var branch = branchName ?? "agent/your-branch";

        // Step 5 reflects the role's push permission. The branch is already created — no checkout needed.
        var commitStep = repoConnected && canPushCode
            ? $"5. Commit & push: `git add -A && git commit -m \"...\" && git push -u origin {branch}`\n\n"
            : repoConnected
                ? $"5. Commit locally: `git add -A && git commit -m \"...\"`. Do NOT push — your role lacks push permission.\n\n"
                : "5. (No git repo connected — write and run code in the sandbox.)\n\n";

        var header =
            "\n\n## Sandbox — MANDATORY WORKFLOW\n\n" +
            $"You have a live sandbox workspace at `/workspace/runs/{branch}` and **MUST** use it for all implementation work.\n\n" +
            "**The sandbox is NON-INTERACTIVE.** There is no TTY, keyboard, or terminal — programs run " +
            "unattended and any read from stdin returns EOF immediately. Do NOT design around live keypresses " +
            "or terminal prompts, and do NOT choose interactive console-UI / TUI libraries (anything that draws " +
            "to the terminal or waits for `ReadKey`/`readline`-style input) for code you must build and verify " +
            "here. If a program needs input, make it argument/flag-driven, or feed input non-interactively " +
            "(e.g. `run_command printf 'a\\nb\\n' | dotnet run`). Verify by running the program unattended.\n\n" +
            "**REQUIRED steps — do not skip:**\n" +
            "1. Call `list_dir` or `run_command git log --oneline -5` to see the current workspace state\n" +
            "2. Read files with `read_file` before editing them\n" +
            "3. Create/edit files using `write_file` / `edit_file` — **do NOT write code as text in your response**\n" +
            "4. Verify: `run_command dotnet build` / `run_command npm test` / `run_command python -m pytest`\n" +
            commitStep +
            "**NEVER:**\n" +
            "- Write implementation code as text in your response or inside `declare_output`\n" +
            "- Ask \"how would you like me to proceed?\" — explore and implement directly\n" +
            "- Ask the human (via request_human_input OR request_approval) to decide implementation details, to " +
            "approve fixing your own build/runtime errors, or whether to retry/finalize. You have full autonomy: " +
            "pick the most reasonable approach and proceed. If an approach keeps failing, CHANGE the approach " +
            "(e.g. a different library or design) rather than asking\n" +
            "- Claim you built, ran, tested, or verified something you did NOT actually run via `run_command` in " +
            "THIS run. If a build/test wasn't run, or it failed, say so plainly in your summary — never assert a " +
            "success you did not observe in a tool result\n" +
            "- Mark the task done without having run at least one `run_command` or `write_file`\n" +
            "- Run `git init`, `git checkout`, `git branch`, or create new branches — the branch is already set up\n\n" +
            "**Deliver your work with `declare_output`** — that record (not the git repo, which is optional " +
            "and whose per-run branches don't carry across tasks) is how your work reaches the user and " +
            "downstream tasks:\n" +
            "- A document deliverable (spec, plan, report): put it in `declare_output`'s `content`.\n" +
            "- Files/code you wrote: keep the code in workspace files (never in tool arguments) AND pass their " +
            "paths in `declare_output`'s `files` so they're delivered as links — downstream opens them from the " +
            "merged base branch, never from your private run branch.\n\n" +
            "| File task      | Use          | NOT               |\n" +
            "|----------------|--------------|-------------------|\n" +
            "| Read a file    | `read_file`  | `run_command cat` |\n" +
            "| Write new file | `write_file` | heredoc in shell  |\n" +
            "| Edit file      | `edit_file`  | `run_command sed` |\n" +
            "| List directory | `list_dir`   | `run_command ls`  |\n" +
            "| Search code    | `search_code`| `run_command grep`|\n";

        return repoConnected
            ? header +
              $"\nThe repository is ready. **Your working directory is `/workspace/runs/{branch}` and you are already on branch `{branch}`.** " +
              "Your branch was forked from the up-to-date base branch, so file deliverables from completed upstream tasks are already present here — read them with `read_file`/`list_dir`.\n" +
              "Do NOT run `git init`, `git checkout`, `git branch`, or create new branches — the branch is already set up.\n" +
              (canPushCode
                  ? "Git is configured — you can `git commit` and `git push`."
                  : "Git is configured — you can `git commit` locally, but pushing is disabled for your role.")
            : header + $"\nYour sandbox is `/workspace/runs/{branch}` — an isolated directory with no git repo.";
    }

    private sealed class RunWorkspaceProvider : IWorkspaceProvider
    {
        private const int PollIntervalMs   = 3_000;
        private const int PollMaxAttempts  = 40;   // 40 × 3s = 2 minutes

        private readonly WorkflowCosmosService _cosmos;
        private readonly IWorkspaceService _workspace;
        private readonly ISecretProvider _secrets;
        private Board _board;
        private readonly string _runId;
        private readonly string _taskId;
        private readonly bool _canPush;
        private readonly ILogger _logger;
        private WorkspaceConnection? _cached;
        private bool _failed;
        private string? _lastError;

        /// <summary>The real cause of the failed provisioning attempt (e.g. a Key Vault 403), surfaced so
        /// the run's failure reason is accurate rather than a generic "sandbox could not be started".</summary>
        public string? LastError => _lastError;

        public RunWorkspaceProvider(WorkflowCosmosService cosmos, IWorkspaceService workspace,
            ISecretProvider secrets, Board board, string runId, string taskId, bool canPush, ILogger logger)
        {
            _cosmos = cosmos; _workspace = workspace; _secrets = secrets;
            _board = board; _runId = runId; _taskId = taskId; _canPush = canPush; _logger = logger;
        }

        public async Task<WorkspaceConnection?> EnsureAsync(CancellationToken ct = default)
        {
            if (_cached is not null) return _cached;
            if (_failed) return null;

            // Resume: if a previous round already provisioned the workspace for this run, reuse it.
            var run = await _cosmos.GetRunByIdAsync(_runId, ct);
            if (run is { WorkspaceEndpoint: not null, WorkspaceToken: not null })
            {
                var branchResume = $"agent/{_runId[..Math.Min(8, _runId.Length)]}";
                // Ensure the worktree exists (idempotent) in case the container was recycled.
                try { await _workspace.CreateWorktreeAsync(run.WorkspaceEndpoint, run.WorkspaceToken, _runId[..Math.Min(8, _runId.Length)], branchResume, _canPush, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "[RunWorkspace] worktree idempotent-create failed run={RunId}", _runId); }
                return _cached = new WorkspaceConnection(run.WorkspaceEndpoint, run.WorkspaceToken, _runId[..Math.Min(8, _runId.Length)]);
            }

            try
            {
                return _cached = await ProvisionWithCasAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RunWorkspace] workspace provisioning failed run={RunId}", _runId);
                _failed = true;
                _lastError = ex.Message;   // carry the accurate cause to the run's failure reason
                return null;
            }
        }

        private async Task<WorkspaceConnection?> ProvisionWithCasAsync(CancellationToken ct)
        {
            var runIdShort = _runId[..Math.Min(8, _runId.Length)];
            var branch = $"agent/{runIdShort}";
            var tokenKey = $"workspace-token-board-{_board.Id}";

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var (freshBoard, etag) = await _cosmos.GetBoardWithEtagAsync(_board.Id, _board.TenantId, ct);
                _board = freshBoard;

                switch (freshBoard.WorkspaceStatus)
                {
                    case BoardWorkspaceStatus.Ready:
                        return await AttachToReadyContainerAsync(freshBoard, runIdShort, branch, tokenKey, ct);

                    case BoardWorkspaceStatus.Provisioning:
                        var readyBoard = await PollForReadyAsync(ct);
                        if (readyBoard is null) throw new TimeoutException("Board workspace did not become Ready within 2 minutes.");
                        return await AttachToReadyContainerAsync(readyBoard, runIdShort, branch, tokenKey, ct);

                    case BoardWorkspaceStatus.None:
                        // CAS: atomically flip None → Provisioning; first writer wins.
                        var claimed = await TryClaimProvisioningAsync(freshBoard, etag, ct);
                        if (!claimed)
                        {
                            // Lost the race — another run is provisioning; wait for it.
                            var raceBoard = await PollForReadyAsync(ct);
                            if (raceBoard is null) throw new TimeoutException("Board workspace did not become Ready within 2 minutes.");
                            return await AttachToReadyContainerAsync(raceBoard, runIdShort, branch, tokenKey, ct);
                        }

                        // We won — provision the container.
                        try
                        {
                            var info = await _workspace.EnsureBoardContainerAsync(_board, ct);
                            if (info is null) throw new InvalidOperationException("EnsureBoardContainerAsync returned null.");

                            // Store the ACTUAL token the container was started with. Must match the
                            // container's EXECUTOR_TOKEN env var so subsequent runs authenticate correctly.
                            await _secrets.SetSecretAsync(tokenKey, info.Token, ct);
                            await _cosmos.PatchBoardWorkspaceAsync(_board.Id, _board.TenantId,
                                info.ContainerName, info.Endpoint, BoardWorkspaceStatus.Ready, ct);

                            await _workspace.CreateWorktreeAsync(info.Endpoint, info.Token, runIdShort, branch, _canPush, ct);
                            await _cosmos.PatchRunWorkspaceAsync(_runId, _taskId, info.ContainerName, info.Endpoint, info.Token, ct);
                            await _cosmos.PatchBoardWorkspaceLastUsedAsync(_board.Id, _board.TenantId, ct);

                            return new WorkspaceConnection(info.Endpoint, info.Token, runIdShort);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[RunWorkspace] container provision failed board={BoardId}", _board.Id);
                            try { await _cosmos.PatchBoardWorkspaceStatusAsync(_board.Id, _board.TenantId, BoardWorkspaceStatus.None, ct); }
                            catch { /* best-effort reset */ }
                            throw;
                        }
                }
            }
            throw new InvalidOperationException("Failed to provision workspace after 3 attempts.");
        }

        private async Task<WorkspaceConnection> AttachToReadyContainerAsync(
            Board board, string runIdShort, string branch, string tokenKey, CancellationToken ct)
        {
            var token = await _secrets.GetSecretAsync(tokenKey, ct)
                        ?? throw new InvalidOperationException($"Workspace token secret '{tokenKey}' not found.");
            await _workspace.CreateWorktreeAsync(board.WorkspaceEndpoint!, token, runIdShort, branch, _canPush, ct);
            await _cosmos.PatchRunWorkspaceAsync(_runId, _taskId, board.WorkspaceContainerName!, board.WorkspaceEndpoint!, token, ct);
            await _cosmos.PatchBoardWorkspaceLastUsedAsync(board.Id, board.TenantId, ct);
            return new WorkspaceConnection(board.WorkspaceEndpoint!, token, runIdShort);
        }

        private async Task<bool> TryClaimProvisioningAsync(Board board, string etag, CancellationToken ct)
        {
            try
            {
                board.WorkspaceStatus = BoardWorkspaceStatus.Provisioning;
                await _cosmos.ReplaceBoardAsync(board, etag, ct);
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                return false;
            }
        }

        private async Task<Board?> PollForReadyAsync(CancellationToken ct)
        {
            for (var i = 0; i < PollMaxAttempts; i++)
            {
                await Task.Delay(PollIntervalMs, ct);
                var b = await _cosmos.GetBoardAsync(_board.Id, _board.TenantId, ct);
                if (b?.WorkspaceStatus == BoardWorkspaceStatus.Ready) return b;
                if (b?.WorkspaceStatus == BoardWorkspaceStatus.None) return null; // provisioning failed
            }
            return null;
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
