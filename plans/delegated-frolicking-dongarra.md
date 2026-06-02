# תוכנית ארכיטקטורה: TectikaAgentEnvironment — "Monday for AI Agents"

## Context

בונים מערכת ניהול משימות שבה ה"עובדים" הם סוכני Azure AI Foundry מוגדרי-תפקיד.
החזון: "Monday.com לסוכני AI" — boards, tasks, pipelines, human-in-the-loop, audit מלא.
שלב 1: שימוש פנימי בחברה. שלב 2: כל לקוח מפרס על הטננט שלו (packaged deployment, לא shared SaaS).

תשובות משתמש:
- היקף MVP: 6-10 תפקידים, 10-30 משתמשים
- Triggers: כל 4 (ידני, supervisor-decomposition, webhook, schedule)
- זהות: היברידי — OBO לפעולות רגישות, role-identity לשאר
- Multi-tenancy: כל לקוח מפרס על הטננט שלו (לא shared infra)

---

## 1. ארכיטקטורת-על — רכיבי Azure ואיך הם מתחברים

```
┌─────────────────────────────────────────────────────────────────┐
│  Browser (Next.js)                                              │
│  • Boards UI / Task cards / Agent status                        │
│  • SSE stream ← live agent updates                              │
│  • Approval inbox (human-in-the-loop)                           │
└────────────────────┬────────────────────────────────────────────┘
                     │ HTTPS / SSE
┌────────────────────▼────────────────────────────────────────────┐
│  API Layer (.NET 8 — Container App)                             │
│  • Auth: Entra (MSAL, JWT validation)                           │
│  • REST: boards, tasks, agents, approvals, runs                 │
│  • SSE endpoint: /api/runs/{id}/stream                          │
│  • Webhook receiver: /api/webhooks/{source}                     │
│  • OBO token exchange service                                   │
└───┬──────────────┬──────────────────────┬───────────────────────┘
    │              │                      │
    ▼              ▼                      ▼
┌──────────┐  ┌──────────────────┐  ┌──────────────────────────┐
│ Cosmos DB│  │  Service Bus     │  │  Azure AI Foundry        │
│          │  │  (Namespace)     │  │  Agent Service           │
│ boards   │  │  • task-trigger  │  │  • AgentRole instances   │
│ tasks    │  │  • agent-events  │  │  • Supervisor agent      │
│ agents   │  │  • approvals     │  │  • MCP tool connections  │
│ runs     │  └────────┬─────────┘  └──────────┬───────────────┘
│ audit    │           │                        │
└──────────┘           ▼                        │
                ┌──────────────────┐            │
                │ Durable Functions│◄───────────┘
                │ (Workflow Engine)│
                │ • Task pipelines │
                │ • Approval waits │
                │ • Retry logic    │
                │ • Schedule timer │
                └──────────────────┘
                       │
              ┌────────▼────────┐
              │  Key Vault      │  ← secrets, OBO client secrets
              │  App Insights   │  ← telemetry, traces
              │  ACR            │  ← container images
              └─────────────────┘
```

---

## 2. מודל זהות והרשאות

### עקרון ההיברידיות

| פעולה | זהות בשימוש | מנגנון |
|-------|-------------|--------|
| קריאה/כתיבה ל-Cosmos DB | Role Identity (Managed Identity) | MSI → Cosmos RBAC |
| קריאה ל-Foundry / LLM | Role Identity | MSI → Foundry |
| Push לגיט, כתיבה לריפו | OBO — זהות המשתמש שיצר את המשימה | OBO flow מהמשתמש |
| Deploy / infra changes | OBO + explicit approval gate | consent + OBO |
| קריאת APIs חיצוניים ללא זהות משתמש | Service Principal per role | App Registration |
| כתיבה ל-DBs פנימיים | Role Identity | MSI |

### Entra Agent ID — מודל הרישום

כל תפקיד-סוכן הוא **App Registration נפרד** ב-Entra:
```
AgentRole: backend-developer
  ├── App Registration: tectika-agent-backend
  │     └── API permissions: repo:write (GitHub), cosmos:readwrite
  ├── Managed Identity: linked to Container App slot
  └── Agent ID claim: agentId = "backend-developer-v1"
```

- `agentId` נכנס ל-JWT של הסוכן (custom claim)
- כל קריאה ל-API החיצוני נושאת: `X-Agent-Id`, `X-Run-Id`, `X-Initiated-By`

### OBO Flow (לפעולות רגישות)

```
1. User logs in → receives Entra access token (scope: api://tectika-agents)
2. Token stored in Cosmos (encrypted, TTL=1h) per runId
3. When agent needs sensitive action:
   API → MSAL OBO exchange → downstream token (e.g. GitHub OAuth)
4. Consent gate: Service Bus message → user approves in UI → external event to Durable Function
5. Action performed, audit entry written
```

### Audit Log — כל רשומה מכילה:
```json
{
  "id": "uuid",
  "tenantId": "...",
  "timestamp": "ISO8601",
  "runId": "...",
  "taskId": "...",
  "actorType": "agent | human",
  "actorId": "backend-developer | user@company.com",
  "agentRoleId": "backend-developer",
  "action": "git.push | cosmos.write | api.call | approval.requested",
  "identityUsed": "obo:user@company.com | role:backend-developer | service:tectika-backend",
  "resource": { "type": "git_repo", "id": "repo-name", "scope": "write" },
  "outcome": "success | denied | pending_approval",
  "tokenUsage": { "input": 1200, "output": 340 },
  "durationMs": 2340
}
```

---

## 3. מנוע תזמור + Workflow Engine — המלצת Build vs. Buy

### ההחלטה: Durable Functions + Foundry Agent Service (היברידי)

**למה לא Foundry Native Multi-Agent בלבד:**
- אין שמירת state בין שלבים ארוכים (approval waits, ימים)
- אין replay דטרמיניסטי — קשה ל-audit
- אין native timer / schedule
- אין human-in-the-loop natively (approval gates)

**למה לא Semantic Kernel בלבד:**
- עוד beta/preview לפיצ'רים קריטיים
- מוסיף תלות כשאפשר להישען ישירות על Foundry
- אין state durability מובנה

**ההמלצה: Durable Functions (outer) + Foundry Agent Service (inner)**

```
Durable Orchestrator Function: "RunTaskPipeline"
  │
  ├── Activity: "InvokeAgent(roleId=backend-developer, taskId=X)"
  │     └── calls Foundry Agent Service REST API
  │           returns: result, tokenUsage, runId
  │
  ├── WaitForExternalEvent("approval-gate") ← blocks until human approves
  │
  ├── Activity: "InvokeAgent(roleId=qa-engineer, taskId=X)"
  │
  └── Activity: "WriteAuditLog(...)"
```

**יתרונות:**
- Durable Functions: state מנוהל ב-Azure Storage, replay בחינם, timer חינמי
- Foundry Agent Service: אפשר לנצל tools, MCP, built-in thread management, streaming
- Separation of concerns: workflow logic ≠ AI logic

**Timer Trigger לmissions חוזרות:** DurableFunctions `ContinueAsNew` או TimerTrigger רגיל שמפעיל Orchestrator.

---

## 4. סכמת נתונים — Cosmos DB

**Container: `boards`** — partition key: `/tenantId`
```json
{
  "id": "board-uuid",
  "tenantId": "tectika",
  "name": "Sprint 2025-Q3",
  "description": "...",
  "ownerId": "user@tectika.com",
  "columns": ["backlog","in-progress","review","done"],
  "createdAt": "ISO8601"
}
```

**Container: `tasks`** — partition key: `/boardId`
```json
{
  "id": "task-uuid",
  "tenantId": "tectika",
  "boardId": "board-uuid",
  "title": "Implement auth middleware",
  "description": "...",
  "status": "backlog | in-progress | blocked | review | done",
  "priority": "critical | high | medium | low",
  "assignee": {
    "type": "agent | human",
    "id": "backend-developer | user@tectika.com"
  },
  "createdBy": "user@tectika.com",
  "dependencies": ["task-uuid-2"],
  "workflowRunId": "run-uuid",
  "triggerSource": "manual | supervisor | webhook:github | schedule",
  "triggerMeta": {},
  "createdAt": "ISO8601",
  "dueAt": "ISO8601"
}
```

**Container: `agentRoles`** — partition key: `/tenantId`
```json
{
  "id": "backend-developer",
  "tenantId": "tectika",
  "displayName": "Backend Developer",
  "systemPrompt": "You are a senior backend engineer...",
  "foundryAgentId": "asst_xxx",
  "tools": ["github", "cosmos-admin", "web-search"],
  "mcpServers": ["github-mcp", "azure-mcp"],
  "permissions": {
    "canPushCode": true,
    "canDeploy": false,
    "requiresOboFor": ["git.push", "api.external"]
  },
  "identityConfig": {
    "appRegistrationId": "aad-app-uuid",
    "managedIdentityId": "mi-uuid"
  },
  "escalateTo": "pm-agent",
  "createdAt": "ISO8601"
}
```

**Container: `workflowRuns`** — partition key: `/taskId`
```json
{
  "id": "run-uuid",
  "tenantId": "tectika",
  "taskId": "task-uuid",
  "pipelineDefinition": [
    { "step": 1, "agentRoleId": "backend-developer", "action": "implement" },
    { "step": 2, "agentRoleId": "qa-engineer", "action": "review" },
    { "step": 3, "type": "approval_gate", "approvers": ["user@tectika.com"] },
    { "step": 4, "agentRoleId": "devops", "action": "deploy" }
  ],
  "currentStep": 2,
  "status": "running | paused_approval | completed | failed",
  "steps": [
    {
      "step": 1, "status": "completed",
      "foundryRunId": "foundry-run-id",
      "output": "...", "tokenUsage": { "input": 1200, "output": 400 },
      "durationMs": 45000, "completedAt": "ISO8601"
    }
  ],
  "durableFunctionInstanceId": "df-instance-id",
  "totalTokens": 5400,
  "startedAt": "ISO8601",
  "completedAt": null
}
```

**Container: `approvals`** — partition key: `/runId`
```json
{
  "id": "approval-uuid",
  "tenantId": "tectika",
  "runId": "run-uuid",
  "stepIndex": 3,
  "requestedAt": "ISO8601",
  "expiresAt": "ISO8601+48h",
  "requestedFrom": ["user@tectika.com"],
  "status": "pending | approved | rejected | expired",
  "approvedBy": null,
  "approvedAt": null,
  "notes": null
}
```

**Container: `auditLog`** — partition key: `/tenantId` (+ cross-partition queries for run/task views)

---

## 5. Human-in-the-Loop — מנגנון מלא

שלושת ה-patterns שנבחרו:
1. לפני כל פעולה חיצונית רגישה (gate on specific tools)
2. Confidence-based escalation (supervisor מחליט)
3. Checkpoint בין שלבי pipeline

### Implementation עם Durable Functions:

```
Step N completes →
  Orchestrator evaluates: isApprovalRequired(step, context)?
    YES:
      1. Write approval doc to Cosmos (status=pending)
      2. Send notification (Teams webhook / email via Logic App)
      3. WaitForExternalEvent("approval-{runId}-{step}", timeout=48h)
      4. On timeout: escalate to human supervisor → new approval request
    NO:
      continue to step N+1
```

### Frontend Approval Flow:
- `/approvals` page — inbox of pending approvals with context (what agent wants to do, why)
- One-click approve/reject with notes
- API endpoint: `POST /api/approvals/{id}/respond`
- API raises external event on Durable Functions instance → pipeline resumes

---

## 6. Supervisor Agent — מפרק + מנתב

**Foundry Agent Service instance**: `supervisor-orchestrator`

### Trigger ← משתמש כותב תיאור פרויקט/משימה גדולה

```
User input: "Build a new auth module with OAuth support"
                     │
              Supervisor Agent
              (Foundry Agent Service)
                     │
              Returns structured JSON:
              {
                "tasks": [
                  {"title":"Design auth flow","assignTo":"architect","priority":"high","dependsOn":[]},
                  {"title":"Implement OAuth backend","assignTo":"backend-developer","dependsOn":["task-1"]},
                  {"title":"Add UI login page","assignTo":"ui-developer","dependsOn":["task-2"]},
                  {"title":"Write auth tests","assignTo":"qa-engineer","dependsOn":["task-2","task-3"]}
                ]
              }
                     │
              API creates tasks in Cosmos + triggers Durable Functions per task
```

### Escalation Decision:
Supervisor agent runs periodically (timer trigger) to check:
- Tasks stuck > threshold → escalate notification
- Agent confidence < 0.6 → pause run, notify human
- Dependency conflict → replan

---

## 7. Streaming / Observability

**SSE Architecture:**
```
Durable Function activity → writes event to Service Bus topic "agent-events"
     │
     ▼
.NET API — background service subscribes to Service Bus
     │
     ▼
SSE endpoint /api/runs/{runId}/stream → pushed to browser
```

**Event types streamed:**
```json
{ "type": "step_started", "runId": "...", "step": 2, "agentRole": "qa-engineer" }
{ "type": "agent_thinking", "runId": "...", "content": "Reviewing code..." }
{ "type": "tool_call", "runId": "...", "tool": "github.read", "identity": "role:qa-engineer" }
{ "type": "approval_required", "runId": "...", "step": 3 }
{ "type": "step_completed", "runId": "...", "step": 2, "tokenUsage": {...} }
```

**Observability stack:**
- Application Insights: traces per runId, agent, token usage, latency
- Cosmos audit log: full action history
- Azure Monitor: token cost alerts, pipeline failure alerts

---

## 8. Multi-Tenancy — "Deploy on your Tenant" Model

מכיוון שהמודל הוא "כל לקוח מפרס על הטננט שלו" (packaged deployment, לא shared SaaS):

**מה זה אומר ב-code:**
- `tenantId` בכל document ב-Cosmos עדיין צריך (לעתיד, אם יהיו multi-tenant tests)
- אבל ב-production כל deployment הוא single-tenant
- הארכיטקטורה כבר מוכנה — רק צריך Bicep templates

**Bicep Infrastructure as Code (Phase 3):**
```
/infra/
  main.bicep          — entry point, params: tenantName, region, agentModelDeployment
  modules/
    cosmos.bicep
    foundry.bicep
    containerApps.bicep
    servicebus.bicep
    keyvault.bicep
    entra-appregistrations.bicep (via Graph API calls)
```

**לכל לקוח חדש:** `az deployment sub create --template-file main.bicep --parameters tenantName=acme`

---

## 9. הגדרת MVP — Phase 1 (6-8 שבועות)

### מה כלול ב-MVP:

**ליבת הכלי (Weeks 1-3):**
- [ ] Cosmos DB schema + API layer (.NET 8) — boards, tasks, agentRoles CRUD
- [ ] Next.js UI — board view (kanban), task creation, agent assignment
- [ ] Entra auth (MSAL) — login, user identity
- [ ] 3 agent roles: `backend-developer`, `qa-engineer`, `pm-agent`
- [ ] Manual trigger: user creates task, assigns agent, clicks "Run"

**זרימה בסיסית (Weeks 3-5):**
- [ ] Durable Functions — single linear pipeline (A→B, no branching yet)
- [ ] Foundry Agent Service integration — invoke agent, stream output
- [ ] SSE streaming → live status in UI
- [ ] Role-identity only (no OBO in MVP)
- [ ] Approval gate (1 type: between QA → Deploy)

**Observability + Polish (Weeks 5-6):**
- [ ] Audit log (Cosmos)
- [ ] Run history view — token usage, duration, steps
- [ ] Application Insights traces
- [ ] Basic error handling + retry (Durable Functions built-in)

### מה **לא** כלול ב-MVP:
- Supervisor auto-decomposition (Phase 2)
- Webhooks (Phase 2)
- Schedule/recurring (Phase 2)
- OBO flow (Phase 2)
- Confidence-based escalation (Phase 2)
- Bicep packaging (Phase 3)

---

## 10. Phasing — מה MVP עד חזון מלא

### Phase 1 — MVP פנימי (Weeks 1-8)
מה שמוכיח ערך: אדם יוצר משימה → סוכן מבצע → QA בודק → אדם מאשר → done

### Phase 2 — כוח מלא (Weeks 9-20)
- Supervisor agent (decomposition)
- OBO flow + consent gates
- Webhooks (GitHub PR → task auto-created)
- Schedule triggers
- DAG pipelines (branching, parallel steps)
- Confidence-based escalation
- 6-10 agent roles (+ IT, Security, Architect, Designer)

### Phase 3 — SaaS-ready (Weeks 20-30)
- Bicep deployment templates (full infra as code)
- Customer onboarding flow (automated provisioning)
- Usage metering (token costs per tenant)
- Admin portal
- Multi-language support

---

## 11. סיכונים ושאלות פתוחות

| סיכון | חומרה | מענה |
|-------|-------|------|
| OBO token expiry תוך כדי pipeline ארוך | גבוהה | refresh tokens + consent gate מחדש |
| Foundry Agent Service quotas (rate limits) | בינונית | exponential backoff ב-Durable Functions + multiple deployments |
| Durable Functions replay consistency עם side effects | בינונית | deterministic activities only, side effects בחוץ |
| Cosmos cross-partition queries ל-audit | נמוכה | secondary index + dedicated audit container |
| Foundry Agent Service pricing בסקייל | בינונית | token budget per run, hard limits in orchestrator |
| Entra App Registration per role — ניהול | נמוכה | Bicep + Graph API automation |

### שאלות פתוחות לשלב הפיתוח:
1. **Foundry Agent Service vs. direct API calls** — לאיזה agents שווה להשתמש ב-Foundry threads ולאיזה לקרוא ל-Azure OpenAI ישירות?
2. **MCP servers** — אילו כלים (GitHub MCP, Azure MCP) ב-MVP, ואיך מנהלים credentials ל-MCP per role?
3. **Supervisor prompt design** — כמה structured ה-output של הסופרווייזר? JSON schema strict validation?
4. **SSE vs. SignalR** — לאחר שיש 30+ משתמשים ו-runs במקביל, האם SSE מספיק או צריך לעבור ל-SignalR?

---

## 12. נקודות כניסה לפיתוח — תיקיות מוצעות

```
/
├── infra/                    # Bicep (Phase 3, structure now)
├── src/
│   ├── api/                  # .NET 8 Web API (Container App)
│   │   ├── Controllers/      # boards, tasks, agents, runs, approvals, webhooks
│   │   ├── Services/         # OboTokenService, FoundryAgentService, SseService
│   │   ├── Models/           # Cosmos document models
│   │   └── Program.cs
│   ├── workflows/            # Azure Functions (Durable)
│   │   ├── Orchestrators/    # TaskPipelineOrchestrator.cs
│   │   ├── Activities/       # InvokeAgentActivity, WriteAuditActivity
│   │   └── Triggers/         # WebhookTrigger, TimerTrigger
│   └── web/                  # Next.js
│       ├── app/
│       │   ├── boards/
│       │   ├── tasks/
│       │   ├── approvals/
│       │   └── runs/
│       └── components/
│           ├── BoardView
│           ├── TaskCard
│           ├── AgentStatusStream   # SSE consumer
│           └── ApprovalInbox
```

---

## 13. Verification — איך לבדוק שזה עובד

**בדיקת E2E לאחר MVP:**
1. צור board + task, בחר role `backend-developer`, לחץ Run
2. ודא SSE מגיע לדפדפן עם step_started, agent_thinking, step_completed
3. ודא ש-Cosmos מכיל workflowRun עם steps מאוכלסים
4. ודא audit log מכיל רשומה עם `identityUsed: role:backend-developer`
5. הוסף approval gate — ודא שהפייפליין מחכה ב-`WaitForExternalEvent`
6. אשר מה-UI — ודא שהפייפליין ממשיך
7. בדוק Application Insights — trace מלא של run עם token counts
