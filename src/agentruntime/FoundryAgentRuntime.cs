using Azure.AI.Agents.Persistent;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using FoundryRunStatus = Azure.AI.Agents.Persistent.RunStatus;

namespace TectikaAgents.AgentRuntime;

// ─────────────────────────────────────────────────────────────────────────────
// CONFIRMED SDK SURFACE — Azure.AI.Agents.Persistent 1.1.0-beta.4
// (discovered via the package XML doc + reflection of the installed net8.0 DLL)
//
//   PersistentAgentsClient(string endpoint, TokenCredential)        — ctor
//     .Administration : PersistentAgentsAdministrationClient
//     .Threads        : Threads
//     .Messages       : ThreadMessages
//     .Runs           : ThreadRuns
//
//   Administration.CreateAgent(model, name, description, instructions, ... , ct)
//                                                         => Response<PersistentAgent>
//   Administration.UpdateAgent(assistantId, model, name, description,
//                              instructions, ... , ct)    => Response<PersistentAgent>
//   Administration.DeleteAgent(assistantId, ct)           => Response<bool>
//   PersistentAgent { string Id; string Model; string Instructions; ... }
//
//   Threads.CreateThread(messages?, toolResources?, metadata?, ct)
//                                                         => Response<PersistentAgentThread>
//   PersistentAgentThread { string Id; ... }
//
//   Messages.CreateMessage(threadId, MessageRole, string content, attachments?,
//                          metadata?, ct)                 => Response<PersistentThreadMessage>
//   Messages.GetMessages(threadId, runId?, limit?, ListSortOrder?, after?, before?, ct)
//                                                         => Pageable<PersistentThreadMessage>
//   PersistentThreadMessage { MessageRole Role; IReadOnlyList<MessageContent> ContentItems; ... }
//   MessageRole { User, Agent }   (Agent == the assistant)
//   MessageTextContent : MessageContent { string Text; ... }
//
//   Runs.CreateRun(threadId, assistantId, overrideModelName?, overrideInstructions?,
//                  additionalInstructions?, additionalMessages?, overrideTools?,
//                  stream?, temperature?, topP?, maxPromptTokens?, maxCompletionTokens?,
//                  truncationStrategy?, responseFormat?, toolChoice?, parallelToolCalls?,
//                  metadata?, additionalFieldList?, ct)   => Response<ThreadRun>
//   Runs.GetRun(threadId, runId, ct)                      => Response<ThreadRun>
//   ThreadRun { string Id; RunStatus Status; RunCompletionUsage Usage; RunError LastError; ... }
//   RunStatus { Queued, InProgress, RequiresAction, Cancelling, Cancelled,
//               Failed, Completed, Expired }   (value-struct; .ToString() => "completed" etc.)
//   RunCompletionUsage { long PromptTokens; long CompletionTokens; long TotalTokens }
//
// NOTE: Implemented with a NON-STREAMING poll loop (CreateRun → poll GetRun until a
// terminal RunStatus → read the latest assistant message). The streaming overloads
// (CreateRunStreaming) return SSE update unions that are awkward to consume robustly
// for a behavior-preserving compile, so polling was chosen per the task's fallback.
// OnStatus is invoked on every status transition; OnText is invoked once with the
// final assistant text (no per-token deltas without streaming).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Real Azure AI Foundry Agent Service runtime + provisioner. Persists agent/thread ids
/// onto the role/task objects passed in; the caller is responsible for saving them to Cosmos.</summary>
public sealed class FoundryAgentRuntime : IAgentRuntime, IAgentProvisioner
{
    private readonly PersistentAgentsClient _client;
    private readonly FoundrySettings _settings;
    private readonly ILogger<FoundryAgentRuntime> _logger;

    /// <summary>Optional per-turn streaming sinks set by the caller per run.</summary>
    public Action<string>? OnText { get; set; }
    public Action<string>? OnStatus { get; set; }

    public FoundryAgentRuntime(IOptions<FoundrySettings> settings, ILogger<FoundryAgentRuntime> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _client = new PersistentAgentsClient(_settings.ProjectEndpoint, new DefaultAzureCredential());
    }

    public async Task<AgentSyncResult> EnsureAgentAsync(AgentRole role, CancellationToken ct = default)
    {
        try
        {
            var model = role.ModelOverride ?? _settings.DefaultModel;
            var hash = AgentInstructionsHash.Compute(role.SystemPrompt, model);
            if (string.IsNullOrEmpty(role.FoundryAgentId))
            {
                var created = await _client.Administration.CreateAgentAsync(
                    model: model,
                    name: role.DisplayName,
                    description: null,
                    instructions: role.SystemPrompt,
                    cancellationToken: ct).ConfigureAwait(false);
                role.FoundryAgentId = created.Value.Id;
                role.FoundryAgentHash = hash;
                return new AgentSyncResult(role.FoundryAgentId, true);
            }
            if (role.FoundryAgentHash != hash)
            {
                await _client.Administration.UpdateAgentAsync(
                    assistantId: role.FoundryAgentId,
                    model: model,
                    name: role.DisplayName,
                    description: null,
                    instructions: role.SystemPrompt,
                    cancellationToken: ct).ConfigureAwait(false);
                role.FoundryAgentHash = hash;
            }
            return new AgentSyncResult(role.FoundryAgentId, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EnsureAgent failed for role {Role}", role.Id);
            return new AgentSyncResult(role.FoundryAgentId, false, ex.Message);
        }
    }

    public async Task DeleteAgentAsync(string? foundryAgentId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(foundryAgentId)) return;
        try { await _client.Administration.DeleteAgentAsync(foundryAgentId, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "DeleteAgent failed (ignored) for {Id}", foundryAgentId); }
    }

    public async Task<string> EnsureThreadAsync(AgentTask task, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(task.FoundryThreadId)) return task.FoundryThreadId!;
        var thread = await _client.Threads.CreateThreadAsync(cancellationToken: ct).ConfigureAwait(false);
        task.FoundryThreadId = thread.Value.Id;
        return task.FoundryThreadId!;
    }

    public async Task<AgentRunOutcome> RunTurnAsync(AgentRunRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(req.Role.FoundryAgentId))
            return Fail(req, "Role has no FoundryAgentId — ensure the agent first.");

        // 1) add the user message to the thread.
        await _client.Messages.CreateMessageAsync(
            req.ThreadId, MessageRole.User, req.UserMessage, cancellationToken: ct).ConfigureAwait(false);

        // 2) create the run, capping completion tokens, then poll until terminal.
        var createResp = await _client.Runs.CreateRunAsync(
            threadId: req.ThreadId,
            assistantId: req.Role.FoundryAgentId,
            overrideModelName: null,
            overrideInstructions: null,
            additionalInstructions: null,
            additionalMessages: null,
            overrideTools: null,
            stream: false,
            temperature: null,
            topP: null,
            maxPromptTokens: null,
            maxCompletionTokens: req.MaxCompletionTokens,
            truncationStrategy: null,
            responseFormat: null,
            toolChoice: null,
            parallelToolCalls: null,
            metadata: null,
            cancellationToken: ct).ConfigureAwait(false);

        ThreadRun run = createResp.Value;
        string completionId = run.Id;
        var text = new System.Text.StringBuilder();
        int input = 0, output = 0;

        string status = run.Status.ToString();
        OnStatus?.Invoke(status);

        // NOTE: RequiresAction is intentionally NOT a continue-status. Phase 1 agents carry no
        // tools, so we never submit tool outputs; treating it as terminal (→ Failed below) avoids
        // polling forever should a tool-enabled role ever reach this runtime before tools land.
        while (run.Status == FoundryRunStatus.Queued
            || run.Status == FoundryRunStatus.InProgress
            || run.Status == FoundryRunStatus.Cancelling)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
            run = (await _client.Runs.GetRunAsync(req.ThreadId, run.Id, ct).ConfigureAwait(false)).Value;
            var next = run.Status.ToString();
            if (next != status)
            {
                status = next;
                OnStatus?.Invoke(status);
            }
        }

        if (run.Usage is not null)
        {
            input = (int)run.Usage.PromptTokens;
            output = (int)run.Usage.CompletionTokens;
        }

        // 3) read the latest assistant message text from the thread.
        await foreach (var msg in _client.Messages.GetMessagesAsync(
            req.ThreadId, runId: run.Id, order: ListSortOrder.Descending, cancellationToken: ct)
                .ConfigureAwait(false))
        {
            if (msg.Role != MessageRole.Agent) continue;
            foreach (var item in msg.ContentItems)
                if (item is MessageTextContent t)
                    text.Append(t.Text);
            break; // newest assistant message only
        }

        if (text.Length > 0) OnText?.Invoke(text.ToString());

        if (status == "requires_action")
            return Fail(req, "Foundry run requires tool action, which this runtime does not handle yet (Phase 1 has no tools).");
        if (status is "failed" or "cancelled" or "expired")
            return Fail(req, $"Foundry run ended with status '{status}'.");
        if (status == "incomplete")
            return new AgentRunOutcome(AgentRunStatus.BudgetExceeded, text.ToString(),
                ArtifactContentType.Markdown, new TokenUsage { Input = input, Output = output }, completionId);

        var content = text.ToString();
        return new AgentRunOutcome(AgentRunStatus.Completed, content, DetectType(content, req.Role),
            new TokenUsage { Input = input, Output = output }, completionId);
    }

    private static AgentRunOutcome Fail(AgentRunRequest req, string error) =>
        new(AgentRunStatus.Failed, "", ArtifactContentType.Markdown, new TokenUsage(),
            $"run-{req.RunId}-{req.Step}", Error: error);

    private static ArtifactContentType DetectType(string content, AgentRole role)
    {
        if (role.Id.Contains("backend") || role.Id.Contains("devops") || role.Id.Contains("qa"))
            return ArtifactContentType.Code;
        var t = content.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('[')) return ArtifactContentType.Json;
        return ArtifactContentType.Markdown;
    }
}
