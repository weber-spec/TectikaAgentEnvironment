using System.Collections.Concurrent;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services.MockData;

/// <summary>
/// Builds a small, internally-consistent dataset for <see cref="InMemoryCosmosDbService"/> so the
/// frontend has realistic content to render before Azure Cosmos DB is provisioned.
///
/// Everything is created under tenant <c>default</c> and owner <c>dev@tectika.com</c> — the same
/// values the mock auth handler injects — so the seeded data is visible to anonymous dev requests.
/// Ids are stable string literals so cross-references (task→run→artifact→approval) line up.
/// </summary>
internal static class MockDataSeeder
{
    private const string Tenant = "default";
    private const string Owner = "dev@tectika.com";

    // Stable ids for cross-referencing
    private const string BoardId = "board-001";

    private const string RolePlanner = "role-planner";
    private const string RoleEngineer = "role-engineer";
    private const string RoleReviewer = "role-reviewer";

    private const string TaskSpec = "task-spec";
    private const string TaskImpl = "task-impl";
    private const string TaskReview = "task-review";
    private const string TaskDeploy = "task-deploy";
    private const string TaskBacklog = "task-backlog";

    private const string RunImpl = "run-impl";
    private const string RunDeploy = "run-deploy";

    public static void Seed(
        ConcurrentDictionary<string, Board> boards,
        ConcurrentDictionary<string, AgentTask> tasks,
        ConcurrentDictionary<string, AgentRole> agentRoles,
        ConcurrentDictionary<string, WorkflowRun> runs,
        ConcurrentDictionary<string, Artifact> artifacts,
        ConcurrentDictionary<string, Approval> approvals)
    {
        var now = DateTimeOffset.UtcNow;

        // ── Board ────────────────────────────────────────────────────────────────
        boards[BoardId] = new Board
        {
            Id = BoardId,
            TenantId = Tenant,
            Name = "Checkout Service Revamp",
            Description = "Agent-driven rebuild of the checkout microservice.",
            OwnerId = Owner,
            CreatedAt = now.AddDays(-14),
        };

        // ── Agent Roles ────────────────────────────────────────────────────────────
        agentRoles[RolePlanner] = new AgentRole
        {
            Id = RolePlanner,
            TenantId = Tenant,
            DisplayName = "Planner",
            SystemPrompt = "You are a senior technical planner. Break work into a clear, ordered spec " +
                           "with acceptance criteria. Never write implementation code.",
            Tools = ["search", "read_repo"],
            Permissions = new AgentPermissions { RequiresApprovalFor = ["publish_spec"] },
            ModelOverride = "gpt-4o",
            CreatedAt = now.AddDays(-14),
            UpdatedAt = now.AddDays(-14),
        };
        agentRoles[RoleEngineer] = new AgentRole
        {
            Id = RoleEngineer,
            TenantId = Tenant,
            DisplayName = "Senior Engineer",
            SystemPrompt = "You are a senior software engineer. Implement the spec faithfully, with " +
                           "tests, and document trade-offs in your output.",
            Tools = ["read_repo", "write_code", "run_tests"],
            McpServers = ["github"],
            Permissions = new AgentPermissions
            {
                CanPushCode = true,
                RequiresOboFor = ["github"],
                RequiresApprovalFor = ["push_to_main"],
            },
            EscalateTo = RoleReviewer,
            CreatedAt = now.AddDays(-14),
            UpdatedAt = now.AddDays(-7),
        };
        agentRoles[RoleReviewer] = new AgentRole
        {
            Id = RoleReviewer,
            TenantId = Tenant,
            DisplayName = "Reviewer",
            SystemPrompt = "You are a meticulous code reviewer. Flag correctness, security and style " +
                           "issues. Approve only when confident.",
            Tools = ["read_repo"],
            Permissions = new AgentPermissions { CanDeploy = true, RequiresApprovalFor = ["deploy"] },
            CreatedAt = now.AddDays(-14),
            UpdatedAt = now.AddDays(-14),
        };

        // ── Tasks ──────────────────────────────────────────────────────────────────
        tasks[TaskSpec] = new AgentTask
        {
            Id = TaskSpec,
            TenantId = Tenant,
            BoardId = BoardId,
            Title = "Draft checkout API spec",
            Description = "Produce an OpenAPI spec and acceptance criteria for the new checkout flow.",
            Status = AgentTaskStatus.Done,
            Priority = TaskPriority.High,
            Assignee = new TaskAssignee { Type = AssigneeType.Agent, Id = RolePlanner },
            CreatedBy = Owner,
            CurrentArtifactId = "art-spec-v1",
            CanvasPosition = new CanvasPosition { X = 80, Y = 80 },
            DownstreamTaskIds = [TaskImpl],
            CreatedAt = now.AddDays(-10),
        };
        tasks[TaskImpl] = new AgentTask
        {
            Id = TaskImpl,
            TenantId = Tenant,
            BoardId = BoardId,
            Title = "Implement checkout endpoints",
            Description = "Implement the endpoints from the approved spec, with unit tests.",
            Status = AgentTaskStatus.InProgress,
            Priority = TaskPriority.Critical,
            Assignee = new TaskAssignee { Type = AssigneeType.Agent, Id = RoleEngineer },
            CreatedBy = Owner,
            WorkflowRunId = RunImpl,
            CurrentArtifactId = "art-impl-v2",
            CanvasPosition = new CanvasPosition { X = 380, Y = 80 },
            UpstreamTaskIds = [TaskSpec],
            DownstreamTaskIds = [TaskReview],
            CreatedAt = now.AddDays(-6),
            DueAt = now.AddDays(2),
        };
        tasks[TaskReview] = new AgentTask
        {
            Id = TaskReview,
            TenantId = Tenant,
            BoardId = BoardId,
            Title = "Review checkout implementation",
            Description = "Code review of the implementation before deploy.",
            Status = AgentTaskStatus.Review,
            Priority = TaskPriority.High,
            Assignee = new TaskAssignee { Type = AssigneeType.Agent, Id = RoleReviewer },
            CreatedBy = Owner,
            HumanAuditorId = Owner,
            CanvasPosition = new CanvasPosition { X = 680, Y = 80 },
            UpstreamTaskIds = [TaskImpl],
            DownstreamTaskIds = [TaskDeploy],
            CreatedAt = now.AddDays(-3),
        };
        tasks[TaskDeploy] = new AgentTask
        {
            Id = TaskDeploy,
            TenantId = Tenant,
            BoardId = BoardId,
            Title = "Deploy to staging",
            Description = "Deploy the reviewed build to the staging environment.",
            Status = AgentTaskStatus.AwaitingApproval,
            Priority = TaskPriority.Critical,
            Assignee = new TaskAssignee { Type = AssigneeType.Agent, Id = RoleReviewer },
            CreatedBy = Owner,
            WorkflowRunId = RunDeploy,
            CanvasPosition = new CanvasPosition { X = 980, Y = 80 },
            UpstreamTaskIds = [TaskReview],
            CreatedAt = now.AddDays(-1),
        };
        tasks[TaskBacklog] = new AgentTask
        {
            Id = TaskBacklog,
            TenantId = Tenant,
            BoardId = BoardId,
            Title = "Add fraud-check hook",
            Description = "Investigate integrating the fraud-check service into the checkout pipeline.",
            Status = AgentTaskStatus.Backlog,
            Priority = TaskPriority.Medium,
            Assignee = new TaskAssignee { Type = AssigneeType.Human, Id = Owner },
            CreatedBy = Owner,
            CanvasPosition = new CanvasPosition { X = 380, Y = 320 },
            CreatedAt = now.AddDays(-1),
        };

        // ── Artifacts ──────────────────────────────────────────────────────────────
        artifacts["art-spec-v1"] = new Artifact
        {
            Id = "art-spec-v1",
            TenantId = Tenant,
            TaskId = TaskSpec,
            Version = 1,
            ContentType = ArtifactContentType.Markdown,
            Content = "# Checkout API Spec\n\n## POST /checkout\nCreates a checkout session.\n\n" +
                      "### Acceptance criteria\n- Validates cart\n- Idempotent by `cartId`\n- Returns 201 with sessionId",
            Origin = ArtifactOrigin.Agent,
            CreatedAt = now.AddDays(-9),
            UpdatedAt = now.AddDays(-9),
        };
        artifacts["art-impl-v1"] = new Artifact
        {
            Id = "art-impl-v1",
            TenantId = Tenant,
            TaskId = TaskImpl,
            RunId = RunImpl,
            Version = 1,
            ContentType = ArtifactContentType.Code,
            Content = "public record CheckoutResult(string SessionId);\n// initial draft — missing validation",
            InputContext = new ArtifactInputContext
            {
                UpstreamArtifacts =
                [
                    new UpstreamArtifactRef
                    {
                        TaskId = TaskSpec, ArtifactId = "art-spec-v1", Version = 1,
                        ContentType = ArtifactContentType.Markdown,
                    },
                ],
            },
            Origin = ArtifactOrigin.Agent,
            CreatedAt = now.AddDays(-5),
            UpdatedAt = now.AddDays(-5),
        };
        artifacts["art-impl-v2"] = new Artifact
        {
            Id = "art-impl-v2",
            TenantId = Tenant,
            TaskId = TaskImpl,
            RunId = RunImpl,
            Version = 2,
            ContentType = ArtifactContentType.Code,
            Content = "public record CheckoutResult(string SessionId);\n" +
                      "// adds cart validation + idempotency key handling",
            InputContext = new ArtifactInputContext
            {
                UpstreamArtifacts =
                [
                    new UpstreamArtifactRef
                    {
                        TaskId = TaskSpec, ArtifactId = "art-spec-v1", Version = 1,
                        ContentType = ArtifactContentType.Markdown,
                    },
                ],
                HumanContext = "Reviewer asked for idempotency on retries.",
            },
            InternalLogs = ["ran unit tests: 12 passed", "lint: clean"],
            Origin = ArtifactOrigin.Agent,
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-2),
        };

        // ── Workflow Runs ───────────────────────────────────────────────────────────
        runs[RunImpl] = new WorkflowRun
        {
            Id = RunImpl,
            TenantId = Tenant,
            TaskId = TaskImpl,
            Status = RunStatus.Completed,
            CurrentStep = 1,
            PipelineDefinition =
            [
                new PipelineStep { Step = 0, Type = StepType.AgentExecution, AgentRoleId = RoleEngineer, Action = "implement" },
            ],
            Steps =
            [
                new StepResult
                {
                    Step = 0, Status = RunStatus.Completed, ArtifactId = "art-impl-v2",
                    TokenUsage = new TokenUsage { Input = 3200, Output = 1800 },
                    DurationMs = 42_000, CompletedAt = now.AddDays(-2),
                },
            ],
            TotalTokens = 5000,
            EstimatedCostUsd = 0.06m,
            StartedAt = now.AddDays(-2).AddMinutes(-1),
            CompletedAt = now.AddDays(-2),
        };
        runs[RunDeploy] = new WorkflowRun
        {
            Id = RunDeploy,
            TenantId = Tenant,
            TaskId = TaskDeploy,
            Status = RunStatus.PausedApproval,
            CurrentStep = 1,
            PipelineDefinition =
            [
                new PipelineStep { Step = 0, Type = StepType.AgentExecution, AgentRoleId = RoleReviewer, Action = "build" },
                new PipelineStep { Step = 1, Type = StepType.ApprovalGate, Approvers = [Owner] },
            ],
            Steps =
            [
                new StepResult
                {
                    Step = 0, Status = RunStatus.Completed,
                    TokenUsage = new TokenUsage { Input = 900, Output = 300 },
                    DurationMs = 8_000, CompletedAt = now.AddHours(-3),
                },
                new StepResult { Step = 1, Status = RunStatus.PausedApproval },
            ],
            TotalTokens = 1200,
            EstimatedCostUsd = 0.02m,
            StartedAt = now.AddHours(-3).AddMinutes(-1),
        };

        // ── Approvals ───────────────────────────────────────────────────────────────
        approvals["approval-deploy"] = new Approval
        {
            Id = "approval-deploy",
            TenantId = Tenant,
            RunId = RunDeploy,
            TaskId = TaskDeploy,
            StepIndex = 1,
            RequestedAt = now.AddHours(-3),
            ExpiresAt = now.AddHours(45),
            RequestedFrom = [Owner],
            Status = ApprovalStatus.Pending,
            ActionDescription = "Deploy checkout service build #482 to staging.",
            IdentityToBeUsed = Owner,
        };
    }
}
