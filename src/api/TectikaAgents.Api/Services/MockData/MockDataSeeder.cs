using System.Collections.Concurrent;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

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
        ConcurrentDictionary<string, Approval> approvals,
        ConcurrentDictionary<string, TaskEdge> edges,
        ConcurrentDictionary<string, UsageRollup> usageRollups,
        ConcurrentDictionary<string, UsageEvent> usageEvents)
    {
        var now = DateTimeOffset.UtcNow;

        // Adds a typed edge to the graph. BoardId is required because edges are board-scoped.
        void AddEdge(string source, string target, string boardId,
            EdgeKind kind = EdgeKind.Dependency, string? label = null)
        {
            var id = TaskEdge.MakeId(source, target);
            edges[id] = new TaskEdge
            {
                Id = id,
                TenantId = Tenant,
                BoardId = boardId,
                SourceTaskId = source,
                TargetTaskId = target,
                Kind = kind,
                Label = label,
                CreatedAt = now.AddDays(-9),
                UpdatedAt = now.AddDays(-9),
            };
        }

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

        // ── Edges (dependency chain that the upstream/downstream arrays used to express) ──
        AddEdge(TaskSpec, TaskImpl, BoardId);
        AddEdge(TaskImpl, TaskReview, BoardId);
        AddEdge(TaskReview, TaskDeploy, BoardId);

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

        SeedExtra(boards, tasks, agentRoles, approvals, now);
        SeedUsage(usageRollups, usageEvents, now);
    }

    // Additional roles, boards and tasks so every view (table, kanban, timeline,
    // calendar, chart, dashboard) renders against a lively, varied dataset.
    private static void SeedExtra(
        ConcurrentDictionary<string, Board> boards,
        ConcurrentDictionary<string, AgentTask> tasks,
        ConcurrentDictionary<string, AgentRole> agentRoles,
        ConcurrentDictionary<string, Approval> approvals,
        DateTimeOffset now)
    {
        const string Maya = "maya@tectika.com";
        const string Noah = "noah@tectika.com";
        const string Lena = "lena@tectika.com";

        // ── Extra agent roles ────────────────────────────────────────────────────
        var extraRoles = new (string Id, string Name, string Prompt, string[] Tools, string Model)[]
        {
            ("role-qa", "QA Engineer", "You are a rigorous QA engineer. Write and run tests, reproduce bugs, and report clear repro steps.", ["read_repo", "run_tests"], "gpt-4o"),
            ("role-devops", "DevOps Engineer", "You are a DevOps engineer. Manage CI/CD, infrastructure-as-code, and deployments safely.", ["read_repo", "deploy", "run_tests"], "claude-sonnet-4-6"),
            ("role-designer", "Product Designer", "You are a product designer. Produce UX flows, wireframes and design specs.", ["search", "read_repo"], "gpt-4o"),
            ("role-docs", "Doc Writer", "You are a technical writer. Produce clear, accurate documentation from code and specs.", ["read_repo", "search"], "claude-haiku-4-5"),
            ("role-analyst", "Data Analyst", "You are a data analyst. Query data, build metrics, and summarize insights.", ["search", "read_repo"], "gpt-4o"),
        };
        foreach (var r in extraRoles)
        {
            agentRoles[r.Id] = new AgentRole
            {
                Id = r.Id, TenantId = Tenant, DisplayName = r.Name, SystemPrompt = r.Prompt,
                Tools = [.. r.Tools], ModelOverride = r.Model,
                CreatedAt = now.AddDays(-12), UpdatedAt = now.AddDays(-4),
            };
        }

        // ── Board 2: Mobile App Launch ───────────────────────────────────────────
        const string B2 = "board-002";
        boards[B2] = new Board
        {
            Id = B2, TenantId = Tenant, Name = "Mobile App Launch",
            Description = "Cross-functional launch of the v2 mobile app.",
            OwnerId = Maya, CreatedAt = now.AddDays(-30),
        };

        // ── Board 3: Data Platform Migration ─────────────────────────────────────
        const string B3 = "board-003";
        boards[B3] = new Board
        {
            Id = B3, TenantId = Tenant, Name = "Data Platform Migration",
            Description = "Migrate the analytics warehouse and rebuild pipelines.",
            OwnerId = Noah, CreatedAt = now.AddDays(-21),
        };

        var seedTasks = new (string Board, string Title, string Desc, AgentTaskStatus Status, TaskPriority Prio, AssigneeType AType, string AId, int CreatedDaysAgo, int? DueInDays)[]
        {
            // board-001 extras (Checkout Service Revamp)
            ("board-001", "Write integration tests", "End-to-end tests for the checkout flow.", AgentTaskStatus.InProgress, TaskPriority.High, AssigneeType.Agent, "role-qa", 4, 3),
            ("board-001", "Update API documentation", "Document the new checkout endpoints.", AgentTaskStatus.Backlog, TaskPriority.Low, AssigneeType.Agent, "role-docs", 3, 9),
            ("board-001", "Load-test checkout", "Validate p95 latency under peak load.", AgentTaskStatus.Blocked, TaskPriority.High, AssigneeType.Human, Lena, 2, 6),
            // board-002 (Mobile App Launch)
            (B2, "Design onboarding flow", "Wireframe and prototype the new onboarding.", AgentTaskStatus.Done, TaskPriority.High, AssigneeType.Agent, "role-designer", 25, -10),
            (B2, "Implement push notifications", "Wire up APNs/FCM and preferences.", AgentTaskStatus.InProgress, TaskPriority.Critical, AssigneeType.Agent, "role-engineer", 12, 4),
            (B2, "App Store assets", "Screenshots, copy and metadata.", AgentTaskStatus.Review, TaskPriority.Medium, AssigneeType.Human, Maya, 8, 7),
            (B2, "Beta feedback triage", "Triage TestFlight feedback into issues.", AgentTaskStatus.AwaitingApproval, TaskPriority.Medium, AssigneeType.Human, Noah, 5, 2),
            (B2, "Crash-free rate dashboard", "Set up crash analytics dashboard.", AgentTaskStatus.Backlog, TaskPriority.Low, AssigneeType.Agent, "role-analyst", 3, 14),
            (B2, "Release v2.0 to stores", "Submit final build for review.", AgentTaskStatus.Backlog, TaskPriority.Critical, AssigneeType.Agent, "role-devops", 2, 12),
            // board-003 (Data Platform Migration)
            (B3, "Audit current warehouse", "Inventory tables, jobs and owners.", AgentTaskStatus.Done, TaskPriority.High, AssigneeType.Agent, "role-analyst", 18, -6),
            (B3, "Provision new lakehouse", "Stand up the target environment.", AgentTaskStatus.InProgress, TaskPriority.Critical, AssigneeType.Agent, "role-devops", 10, 5),
            (B3, "Rewrite ETL pipelines", "Port pipelines to the new engine.", AgentTaskStatus.InProgress, TaskPriority.High, AssigneeType.Agent, "role-engineer", 7, 8),
            (B3, "Validate row counts", "Reconcile source vs target.", AgentTaskStatus.Blocked, TaskPriority.High, AssigneeType.Agent, "role-qa", 4, 10),
            ("board-001", "Add fraud-check telemetry", "Emit metrics for fraud-check hook.", AgentTaskStatus.Failed, TaskPriority.Medium, AssigneeType.Agent, "role-engineer", 6, -1),
        };

        var i = 0;
        var col = 0;
        foreach (var st in seedTasks)
        {
            var id = $"task-x{i:00}";
            tasks[id] = new AgentTask
            {
                Id = id, TenantId = Tenant, BoardId = st.Board,
                Title = st.Title, Description = st.Desc,
                Status = st.Status, Priority = st.Prio,
                Assignee = new TaskAssignee { Type = st.AType, Id = st.AId },
                CreatedBy = Owner,
                CanvasPosition = new CanvasPosition { X = 80 + (col % 4) * 300, Y = 460 + (col / 4) * 220 },
                CreatedAt = now.AddDays(-st.CreatedDaysAgo),
                DueAt = st.DueInDays is int d ? now.AddDays(d) : null,
            };
            i++; col++;
        }

        // A second pending approval on board-002 so the approvals inbox has variety.
        approvals["approval-beta"] = new Approval
        {
            Id = "approval-beta", TenantId = Tenant, RunId = "run-beta", TaskId = "task-x06",
            StepIndex = 0, RequestedAt = now.AddHours(-6), ExpiresAt = now.AddHours(30),
            RequestedFrom = [Maya], Status = ApprovalStatus.Pending,
            ActionDescription = "Promote beta build 2.0(118) to the open beta track.",
            IdentityToBeUsed = Maya,
        };
    }

    // Seed realistic multi-model usage events + rollups so the usage UI renders
    // per-model breakdowns in mock mode. Covers the two tasks that have workflow runs:
    // task-impl (run-impl) and task-deploy (run-deploy). Each task gets one gpt-4o event
    // and one gpt-4o-mini event so PerModel always has two entries. Project and board
    // rollups aggregate across both tasks.
    private static void SeedUsage(
        ConcurrentDictionary<string, UsageRollup> usageRollups,
        ConcurrentDictionary<string, UsageEvent> usageEvents,
        DateTimeOffset now)
    {
        const string Provider = "azure-foundry";
        const string Model4o = "gpt-4o";
        const string Model4oMini = "gpt-4o-mini";

        var calc = new CostCalculator(PricingCatalogLoader.LoadEmbedded());

        // Stable session ids for the two tasks
        const string SessionImpl   = "seed-session-impl";
        const string SessionDeploy = "seed-session-deploy";

        // ── Project rollup (accumulates all tasks) ──────────────────────────────
        var projectRollup = new UsageRollup
        {
            Id        = UsageRollup.ProjectId(Tenant),
            TenantId  = Tenant,
            Scope     = UsageScope.Project,
            ScopeId   = Tenant,
            UpdatedAt = now,
        };

        // ── Board rollup ────────────────────────────────────────────────────────
        var boardRollup = new UsageRollup
        {
            Id        = UsageRollup.BoardId(BoardId),
            TenantId  = Tenant,
            Scope     = UsageScope.Board,
            ScopeId   = BoardId,
            UpdatedAt = now,
        };

        // ── Helper: emit one event + accumulate into the supplied rollups ───────
        int eventSeq = 0;
        void EmitEvent(
            string taskId, string runId, string sessionId,
            string provider, string model,
            TokenUsage tokens,
            UsageRollup taskRollup,
            params UsageRollup[] sharedRollups)
        {
            var seq     = eventSeq++;
            var ts      = now.AddDays(-(seq * 2)).AddHours(-1);   // spread seed events across ~2 weeks for a real time-series
            var cost    = calc.Compute(provider, model, tokens, ts);
            var modelKey = UsageRollup.ModelKey(provider, model);
            var evtId   = UsageEvent.MakeId($"seed-{taskId}", 0, model, seq);

            var evt = new UsageEvent
            {
                Id                   = evtId,
                TenantId             = Tenant,
                BoardId              = BoardId,
                TaskId               = taskId,
                RunId                = runId,
                Step                 = 0,
                Round                = seq,
                AgentRoleId          = "",
                AgentRoleName        = "",
                Provider             = provider,
                Model                = model,
                SessionId            = sessionId,
                Usage                = tokens,
                CatalogVersion       = cost.CatalogVersion,
                InputPerMillion      = cost.InputPerMillion,
                CachedInputPerMillion = cost.CachedInputPerMillion,
                OutputPerMillion     = cost.OutputPerMillion,
                Currency             = cost.Currency,
                CostUsd              = cost.CostUsd,
                PricingMissing       = cost.PricingMissing,
                Timestamp            = ts,
            };
            usageEvents[evtId] = evt;

            // Accumulate into task rollup (lifetime + per-model + current session)
            taskRollup.Lifetime.Add(tokens, cost.CostUsd);
            if (!taskRollup.PerModel.TryGetValue(modelKey, out var taskModelBucket))
            {
                taskModelBucket = new UsageBucket();
                taskRollup.PerModel[modelKey] = taskModelBucket;
            }
            taskModelBucket.Add(tokens, cost.CostUsd);
            taskRollup.CurrentSession!.Add(tokens, cost.CostUsd);
            if (!taskRollup.CurrentSession!.PerModel.TryGetValue(modelKey, out var sessionModelBucket))
            {
                sessionModelBucket = new UsageBucket();
                taskRollup.CurrentSession!.PerModel[modelKey] = sessionModelBucket;
            }
            sessionModelBucket.Add(tokens, cost.CostUsd);

            // Accumulate into shared rollups (project, board)
            foreach (var rollup in sharedRollups)
            {
                rollup.Lifetime.Add(tokens, cost.CostUsd);
                if (!rollup.PerModel.TryGetValue(modelKey, out var sharedBucket))
                {
                    sharedBucket = new UsageBucket();
                    rollup.PerModel[modelKey] = sharedBucket;
                }
                sharedBucket.Add(tokens, cost.CostUsd);
            }
        }

        // ── task-impl (run-impl): 1× gpt-4o + 1× gpt-4o-mini ───────────────────
        var implRollup = new UsageRollup
        {
            Id             = UsageRollup.TaskId(TaskImpl),
            TenantId       = Tenant,
            Scope          = UsageScope.Task,
            ScopeId        = TaskImpl,
            CurrentSession = new SessionBucket { SessionId = SessionImpl, Since = now.AddDays(-2) },
            UpdatedAt      = now,
        };
        EmitEvent(TaskImpl, RunImpl, SessionImpl,
            Provider, Model4o, new TokenUsage { Input = 3000, CachedInput = 500, Output = 1500 },
            implRollup, projectRollup, boardRollup);
        EmitEvent(TaskImpl, RunImpl, SessionImpl,
            Provider, Model4oMini, new TokenUsage { Input = 2000, Output = 800 },
            implRollup, projectRollup, boardRollup);
        usageRollups[implRollup.Id] = implRollup;

        // ── task-deploy (run-deploy): 1× gpt-4o + 1× gpt-4o-mini ───────────────
        var deployRollup = new UsageRollup
        {
            Id             = UsageRollup.TaskId(TaskDeploy),
            TenantId       = Tenant,
            Scope          = UsageScope.Task,
            ScopeId        = TaskDeploy,
            CurrentSession = new SessionBucket { SessionId = SessionDeploy, Since = now.AddDays(-1) },
            UpdatedAt      = now,
        };
        EmitEvent(TaskDeploy, RunDeploy, SessionDeploy,
            Provider, Model4o, new TokenUsage { Input = 900, CachedInput = 0, Output = 300 },
            deployRollup, projectRollup, boardRollup);
        EmitEvent(TaskDeploy, RunDeploy, SessionDeploy,
            Provider, Model4oMini, new TokenUsage { Input = 400, Output = 150 },
            deployRollup, projectRollup, boardRollup);
        usageRollups[deployRollup.Id] = deployRollup;

        // Persist project + board rollups
        usageRollups[projectRollup.Id] = projectRollup;
        usageRollups[boardRollup.Id]   = boardRollup;
    }
}
