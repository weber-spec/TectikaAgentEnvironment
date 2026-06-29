using TectikaAgents.AgentRuntime;
using TectikaAgents.AgentRuntime.Workspace;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

/// <summary>When an agent calls a workspace tool but the sandbox can't be provisioned, the behaviour
/// depends on whether a provider exists: a provider that returns null (provisioning failed) is FATAL —
/// the run must fail cleanly — whereas no provider at all (e.g. the compact path) degrades gracefully.</summary>
public class RoundExecutorWorkspaceTests
{
    private static readonly RoundResponse RunCommandRound =
        RoundResponse.Tools(new[] { new ToolCall("run_command", """{"cmd":"ls"}""", "call_1") });

    [Fact]
    public async Task ProviderPresentButProvisioningFailed_SignalsWorkspaceUnavailable()
    {
        var p = await RoundExecutor.ExecuteOneRoundAsync(
            RunCommandRound, new ThrowingExplorer(), (_, __) => { },
            gitHub: null, boardRepo: null, role: null,
            workspace: new WorkspaceToolExecutor(new UnusedWorkspaceService()),
            workspaceProvider: new NullWorkspaceProvider(),   // provisioning failed → returns null
            ct: default);

        // Fatal: the runtime turns this into RoundOutcome.Error → RunStatus.Failed.
        Assert.NotNull(p.WorkspaceUnavailable);
        Assert.False(p.IsFinal);
        // The call is still answered so the reused conversation isn't left awaiting tool output.
        Assert.Single(p.ToolOutputs);
        Assert.Contains("could not be started", p.ToolOutputs[0].Output);
    }

    [Fact]
    public async Task ProviderReportsRealError_PropagatesItAsTheInternalReason()
    {
        // The provider knows the REAL cause (e.g. a Key Vault 403). That exact reason must travel out as
        // the internal WorkspaceUnavailable text — NOT be replaced by the generic "sandbox" boilerplate —
        // so the run's failure reason is accurate. (The string handed back to the MODEL stays generic.)
        var realError = "Key Vault secret 'workspace-token-board-x' returned 403 Forbidden";
        var p = await RoundExecutor.ExecuteOneRoundAsync(
            RunCommandRound, new ThrowingExplorer(), (_, __) => { },
            gitHub: null, boardRepo: null, role: null,
            workspace: new WorkspaceToolExecutor(new UnusedWorkspaceService()),
            workspaceProvider: new FailedProvisioningProvider(realError),
            ct: default);

        Assert.Equal(realError, p.WorkspaceUnavailable);
    }

    [Fact]
    public async Task ProviderHasNoRealError_FallsBackToGenericReason()
    {
        // If the provider couldn't say why (LastError null), we still fail cleanly with a generic reason.
        var p = await RoundExecutor.ExecuteOneRoundAsync(
            RunCommandRound, new ThrowingExplorer(), (_, __) => { },
            gitHub: null, boardRepo: null, role: null,
            workspace: new WorkspaceToolExecutor(new UnusedWorkspaceService()),
            workspaceProvider: new NullWorkspaceProvider(),
            ct: default);

        Assert.NotNull(p.WorkspaceUnavailable);
        Assert.Contains("could not be started", p.WorkspaceUnavailable!);
    }

    [Fact]
    public async Task NoProvider_DegradesGracefully_NoFatalSignal()
    {
        var p = await RoundExecutor.ExecuteOneRoundAsync(
            RunCommandRound, new ThrowingExplorer(), (_, __) => { },
            gitHub: null, boardRepo: null, role: null,
            workspace: new WorkspaceToolExecutor(new UnusedWorkspaceService()),
            workspaceProvider: null,   // no workspace configured for this run (e.g. compact path)
            ct: default);

        Assert.Null(p.WorkspaceUnavailable);   // not fatal — the run continues
        Assert.Single(p.ToolOutputs);
        Assert.Contains("No workspace is available", p.ToolOutputs[0].Output);
    }

    // ── Stubs ───────────────────────────────────────────────────────────────────
    private sealed class NullWorkspaceProvider : IWorkspaceProvider
    {
        public string? LastError => null;
        public Task<WorkspaceConnection?> EnsureAsync(CancellationToken ct = default) =>
            Task.FromResult<WorkspaceConnection?>(null);
    }

    // Provisioning failed AND the provider captured the real cause (the realistic case the fix targets).
    private sealed class FailedProvisioningProvider : IWorkspaceProvider
    {
        private readonly string _error;
        public FailedProvisioningProvider(string error) => _error = error;
        public string? LastError => _error;
        public Task<WorkspaceConnection?> EnsureAsync(CancellationToken ct = default) =>
            Task.FromResult<WorkspaceConnection?>(null);
    }

    // The executor is never invoked in these tests (wsConn is always null), so the methods just stub out.
    private sealed class UnusedWorkspaceService : IWorkspaceService
    {
        public Task<WorkspaceInfo?> EnsureBoardContainerAsync(Board board, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task CreateWorktreeAsync(string endpoint, string token, string runId, string branch, bool canPush, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RemoveWorktreeAsync(string endpoint, string token, string runId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<WorkspaceMergeResult> MergeRunBranchAsync(string endpoint, string token, string runId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<byte[]> BundleAsync(string endpoint, string token, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RestoreAsync(string endpoint, string token, byte[] bundle, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task DestroyBoardContainerAsync(string containerName, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<WorkspaceAzureState> GetBoardContainerStatusAsync(string containerName, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<CommandResult> RunCommandAsync(string endpoint, string token, string command, int timeoutSeconds = 60, string? runId = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<string> InvokeAsync(string endpoint, string token, string route, object body, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    // run_command never touches the explorer; these throw to prove that.
    private sealed class ThrowingExplorer : IProjectExplorer
    {
        public Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string q, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TaskDetail?> GetTaskAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ArtifactView?> GetArtifactAsync(string id, int? v, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SharedNote>> GetSharedNotesAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
