using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Activities;
using TectikaAgents.Workflows.Orchestrators;
using Xunit;

/// <summary>
/// Verifies the orchestrator propagates a step's <see cref="RunStatus.NeedsRevision"/> result
/// (the QA validator's "REVISION_NEEDED" signal) instead of marking the run Completed.
/// Without propagation the QA feedback loop never fires.
/// </summary>
public class TaskPipelineOrchestratorTests
{
    /// <summary>
    /// Minimal in-memory <see cref="TaskOrchestrationContext"/>: returns a fixed input, returns
    /// scripted results per activity name, and records every activity invocation for assertions.
    /// </summary>
    private sealed class FakeOrchestrationContext : TaskOrchestrationContext
    {
        private readonly object _input;
        private readonly IReadOnlyDictionary<string, object?> _activityResults;
        public readonly List<(string Name, object? Input)> Calls = new();

        public FakeOrchestrationContext(object input, IReadOnlyDictionary<string, object?> activityResults)
        {
            _input = input;
            _activityResults = activityResults;
        }

        public override TaskName Name => default;
        public override string InstanceId => "test-instance";
        public override ParentOrchestrationInstance? Parent => null;
        public override DateTime CurrentUtcDateTime => DateTime.UtcNow;
        public override bool IsReplaying => false;
        protected override ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

        public override T GetInput<T>() => (T)_input;

        public override Task<TResult> CallActivityAsync<TResult>(TaskName name, object? input = null, TaskOptions? options = null)
        {
            Calls.Add((name.Name, input));
            if (_activityResults.TryGetValue(name.Name, out var result) && result is not null)
                return Task.FromResult((TResult)result);
            return Task.FromResult(default(TResult)!);
        }

        public override Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<T> WaitForExternalEvent<T>(string eventName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override void SendEvent(string instanceId, string eventName, object payload) { }
        public override void SetCustomStatus(object? customStatus) { }
        public override Task<TResult> CallSubOrchestratorAsync<TResult>(TaskName orchestratorName, object? input = null, TaskOptions? options = null) => throw new NotSupportedException();
        public override void ContinueAsNew(object? newInput = null, bool preserveUnprocessedEvents = true) { }
        public override Guid NewGuid() => Guid.NewGuid();
    }

    private static PipelineInput SingleAgentStep() => new(
        RunId: "run1",
        TaskId: "task1",
        BoardId: "board1",
        TenantId: "tenant1",
        Steps: new List<PipelineStep> { new() { Step = 1, Type = StepType.AgentExecution, AgentRoleId = "validator" } });

    private static List<UpdateRunStatusInput> StatusUpdates(FakeOrchestrationContext ctx) =>
        ctx.Calls
            .Where(c => c.Name == nameof(UpdateRunStatusActivity))
            .Select(c => (UpdateRunStatusInput)c.Input!)
            .ToList();

    [Fact]
    public async Task NeedsRevisionStepResult_MarksRunNeedsRevision_AndDoesNotComplete()
    {
        var revision = new StepResult { Step = 1, Status = RunStatus.NeedsRevision, RevisionReason = "fix the thing" };
        var ctx = new FakeOrchestrationContext(SingleAgentStep(),
            new Dictionary<string, object?> { [nameof(InvokeAgentActivity)] = revision });

        var result = await new TaskPipelineOrchestrator().RunOrchestration(ctx);

        // The orchestration result must surface NeedsRevision, not Completed.
        Assert.Equal(RunStatus.NeedsRevision, result.Status);

        var updates = StatusUpdates(ctx);
        // The QA loop only fires when UpdateRunStatusActivity is told NeedsRevision.
        Assert.Contains(updates, u => u.Status == RunStatus.NeedsRevision);
        // The run must never be marked Completed (which would route the task to Done and reset the QA edge).
        Assert.DoesNotContain(updates, u => u.Status == RunStatus.Completed);
    }

    [Fact]
    public async Task CompletedStepResult_CompletesRun()
    {
        var ok = new StepResult { Step = 1, Status = RunStatus.Completed };
        var ctx = new FakeOrchestrationContext(SingleAgentStep(),
            new Dictionary<string, object?> { [nameof(InvokeAgentActivity)] = ok });

        var result = await new TaskPipelineOrchestrator().RunOrchestration(ctx);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Contains(StatusUpdates(ctx), u => u.Status == RunStatus.Completed);
    }
}
