# תכנית: Context Architecture — Foundry Agents + TaskBrief + Ephemeral Dev Container

## Context

כרגע כל סוכן מקבל 2 הודעות בלבד. אין זיכרון, אין שיתוף בין ריצות, ואין סביבת הרצה.

ארבע שכבות מצטברות:

| # | שכבה | בעיה | Azure Resource |
|---|------|------|----------------|
| 1 | GitHub Connection | גישה לריפו | OAuth App + KeyVault |
| 2 | Ephemeral Dev Container | סביבת הרצה לקוד | Azure Container Instance per-run |
| 3 | Foundry Agents + Threads | זיכרון per-agent | Foundry Agents API |
| 4 | TaskBrief | קונטקסט cross-run/cross-agent | שדה על AgentTask |

---

## שכבה 1 — GitHub Connection (per Board)

### Board model additions (`src/core/.../Models/Board.cs`):
```csharp
public string? RepoUrl { get; set; }
public string? RepoOwner { get; set; }
public string? RepoName { get; set; }
public string? DefaultBranch { get; set; }              // default "main"
public string? GitHubTokenSecretName { get; set; }      // KeyVault: "github-{boardId}-token"
```

### Backend: `GitHubController` (חדש) (`src/api/.../Controllers/GitHubController.cs`):

| Route | תיאור |
|-------|-------|
| GET `/api/boards/{boardId}/github/connect` | מחזיר GitHub OAuth URL |
| GET `/api/boards/{boardId}/github/callback?code=` | מחליף code ב-token → שומר ב-KeyVault → מעדכן Board |
| DELETE `/api/boards/{boardId}/github` | מסיר חיבור (מוחק secret, מנקה שדות) |
| GET `/api/boards/{boardId}/github/status` | `{ connected, repoUrl, defaultBranch }` |

**callback:**
```csharp
var token = await ExchangeCodeForToken(code);
await _keyVault.SetSecretAsync($"github-{boardId}-token", token);
board.GitHubTokenSecretName = $"github-{boardId}-token";
board.RepoUrl = ...; board.RepoOwner = ...; board.RepoName = ...;
await _cosmos.UpsertBoardAsync(board, ct);
```

**Cleanup:** Board delete → `DELETE /api/boards/{id}/github` → מחיקת secret מ-KeyVault.

**`appsettings.json`:**
```json
"GitHub": { "ClientId": "...", "ClientSecret": "..." }
```

### Frontend (`src/web/.../app/boards/page.tsx`):
- הפוך `window.prompt` למודל עם 2 שלבים
- שלב 1: Name + Description
- שלב 2: "Connect GitHub Repository" (optional) — כפתור OAuth → popup → repo selected
- Board card: badge `org/repo ✓` אם מחובר

---

## שכבה 2 — Ephemeral Dev Container (Azure Container Instances per-run)

### הרעיון
כשפייפליין מתחיל ול-Board יש repo → מפרסמים ACI.
ה-ACI עושה `git clone --depth=1` ומרים MCP server.
אחרי סיום הריצה → ACI נמחק אוטומטית.

**Startup time: ~30-40 שניות (clone + server up)**

### רכיב חדש: `src/agent-container/`
```
src/agent-container/
  Dockerfile
  src/
    server.ts          ← MCP server (Node.js/TypeScript, MCP SDK)
    tools/
      filesystem.ts    ← read_file, write_file, list_files
      git.ts           ← git_status, git_commit, git_create_branch, git_push
      process.ts       ← run_command(cmd, cwd) → stdout/stderr
      github.ts        ← create_pr via GitHub REST API
```

**MCP tools:**
| Tool | פרמטרים | תיאור |
|------|---------|-------|
| `read_file` | `path` | קריאת קובץ |
| `write_file` | `path, content` | כתיבה |
| `list_files` | `path, recursive?` | רשימת קבצים |
| `run_command` | `command, cwd?` | הרצה — tests/build/etc |
| `git_status` | — | קבצים שהשתנו |
| `git_commit` | `message, files[]` | commit |
| `git_create_branch` | `name` | branch מ-HEAD |
| `git_push` | `branch` | push ל-origin |
| `create_pr` | `title, body, base` | PR דרך GitHub API |

**`Dockerfile`:**
```dockerfile
FROM node:22-alpine
RUN apk add --no-cache git curl bash
# Add runtimes needed (dotnet, java, etc) — לפי הצורך
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY src/ ./src/
RUN npm run build
ENV REPO_URL="" GITHUB_TOKEN="" MCP_AUTH_TOKEN="" WORK_DIR="/repo"
CMD sh -c "git clone --depth=1 $REPO_URL /repo && node dist/server.js"
EXPOSE 3001
```

### WorkflowRun additions (`src/core/.../Models/WorkflowRun.cs`):
```csharp
public string? AciContainerGroupName { get; set; }   // לניקוי לאחר הריצה
public string? McpEndpointUrl { get; set; }           // http://...:3001 — זמני לריצה
```

### Orchestrator changes (`src/workflows/Orchestrators/TaskPipelineOrchestrator.cs`):

**בתחילת הפייפליין** (לפני ה-for loop):
```csharp
// Activity חדשה: StartDevContainerActivity
if (board.GitHubTokenSecretName != null)
{
    var containerInfo = await context.CallActivityAsync<ContainerInfo>(
        nameof(StartDevContainerActivity), 
        new StartDevContainerInput(board, runId));
    run.AciContainerGroupName = containerInfo.ContainerGroupName;
    run.McpEndpointUrl = containerInfo.McpUrl;
    // Wait for container ready (poll with timeout 60s)
}
```

**בסוף הפייפליין** (אחרי ה-for loop, גם ב-finally):
```csharp
// Activity חדשה: StopDevContainerActivity
if (run.AciContainerGroupName != null)
    await context.CallActivityAsync(nameof(StopDevContainerActivity), run.AciContainerGroupName);
```

### Activities חדשות:

**`StartDevContainerActivity`** (`src/workflows/Activities/StartDevContainerActivity.cs`):
- יוצר ACI via Azure Management SDK
- Container group name: `tectika-run-{runId[:8]}`
- מחכה עד שMCP server מגיב (poll `/health` עד 60 שניות)
- מחזיר `ContainerInfo { ContainerGroupName, McpUrl }`

**`StopDevContainerActivity`** (`src/workflows/Activities/StopDevContainerActivity.cs`):
- מוחק ACI container group

### InvokeAgentActivity — injection:
```csharp
// run.McpEndpointUrl כבר קיים מה-orchestrator
var mcpToken = run.McpEndpointUrl != null 
    ? await _keyVault.GetSecretAsync($"mcp-run-{runId}-token")
    : null;

// Pass to Foundry Run as additional tool
```

**`appsettings.json`:**
```json
"Azure": {
  "SubscriptionId": "...",
  "ResourceGroup": "tectika-rg",
  "AciLocation": "westeurope"
}
```

---

## שכבה 3 — Foundry Agents + Threads

**`AgentTask`** (`src/core/.../Models/AgentTask.cs`):
```csharp
public string? ThreadId { get; set; }
public string TaskBrief { get; set; } = "";
public string BoardId { get; set; } = string.Empty;
```

**`AgentRolesController.Upsert`:**
```csharp
if (string.IsNullOrEmpty(role.FoundryAgentId))
    role.FoundryAgentId = await _foundry.CreateAgentAsync(role);
else
    await _foundry.UpdateAgentAsync(role.FoundryAgentId, role);
```

**`WorkflowAgentRunner.InvokeAsync`** — החלף HTTP call:
```
GetOrCreate Thread → שמור ThreadId על Task
CreateMessage (TaskBrief + upstream artifacts + task description)
CreateRun (foundryAgentId, threadId, additionalTools=[containerMcp if available])
Poll → GetLastMessage → content ל-Artifact
Parse "## Brief Update" → append to task.TaskBrief → save Task
```

**Cleanup כש-Task → Done:**
```csharp
if (task.ThreadId != null)
    await _foundry.DeleteThreadAsync(task.ThreadId);
task.ThreadId = null;
task.TaskBrief = "";
await _cosmos.UpsertTaskAsync(task, ct);
```

**`FoundryAgentService`** — אותם שינויים לנתיב ה-API.

---

## שכבה 4 — TaskBrief

**`WorkflowAgentRunner.BuildMessages()`** — הוסף לUser message:
```
### Task Brief (context from prior runs):
{task.TaskBrief}
---
{upstream artifacts}
---
At the end of your response:
## Brief Update
<one sentence: what you did / decided / any blocker>
```

**`InvokeAgentActivity`** לאחר ריצה:
```csharp
var update = ParseBriefUpdate(result.Content);
task.TaskBrief += $"\n[{agentRole.DisplayName}, {runId[:6]}, Step {step}]: {update}";
await _cosmos.UpsertTaskAsync(task, ct);
```

**Cleanup:** חלק מ-Done handler (ראה שכבה 3).

---

## סדר מימוש

1. **שכבה 4 — TaskBrief** (עצמאי, מהיר):
   - הוסף `TaskBrief`, `BoardId` לAgentTask
   - עדכן WorkflowAgentRunner (BuildMessages + parse BriefUpdate)
   - עדכן InvokeAgentActivity
   - Done handler ניקוי

2. **שכבה 1 — GitHub Connection** (עצמאי, לא תלוי Foundry):
   - שדות לBoard + GitHubController
   - OAuth flow + KeyVault storage
   - Frontend: Board modal + badge

3. **שכבה 2 — Dev Container** (תלוי ב-1):
   - `src/agent-container/` — MCP server + Dockerfile
   - StartDevContainerActivity + StopDevContainerActivity
   - Orchestrator: start/stop wrapping the pipeline
   - WorkflowRun: AciContainerGroupName + McpEndpointUrl

4. **שכבה 3 — Foundry Agents** (מקביל ל-2):
   - `FoundryAgentsClient` wrapper service
   - AgentRolesController: Create/Update Foundry Agent
   - WorkflowAgentRunner + FoundryAgentService: Foundry run loop
   - InvokeAgentActivity: Container MCP injection

---

## קבצים קריטיים

| קובץ | שינוי |
|------|-------|
| `src/agent-container/` | **חדש** — MCP server + Dockerfile |
| `src/core/.../Models/Board.cs` | Repo fields + GitHub token ref |
| `src/core/.../Models/AgentTask.cs` | `ThreadId`, `TaskBrief`, `BoardId` |
| `src/core/.../Models/WorkflowRun.cs` | `AciContainerGroupName`, `McpEndpointUrl` |
| `src/api/.../Controllers/GitHubController.cs` | **חדש** — OAuth + status + disconnect |
| `src/api/.../Controllers/BoardsController.cs` | cleanup ב-delete |
| `src/api/.../Controllers/AgentRolesController.cs` | CreateAgent/UpdateAgent (Foundry) |
| `src/workflows/Orchestrators/TaskPipelineOrchestrator.cs` | Start/Stop container wrapping pipeline |
| `src/workflows/Activities/StartDevContainerActivity.cs` | **חדש** — ACI provision + wait |
| `src/workflows/Activities/StopDevContainerActivity.cs` | **חדש** — ACI delete |
| `src/workflows/Services/WorkflowAgentRunner.cs` | BuildMessages + Foundry run |
| `src/workflows/Activities/InvokeAgentActivity.cs` | TaskBrief + ThreadId + MCP injection |
| `src/api/.../Services/FoundryAgentService.cs` | Foundry run (API path) |
| `src/web/.../app/boards/page.tsx` | Modal + GitHub connect + badge |
| `src/web/.../lib/types.ts` | Board + AgentTask + WorkflowRun types |
| `appsettings.json` (api + workflows) | GitHub + Azure ACI config |

---

## בדיקה

**שכבה 4 (TaskBrief):**
- הרץ pipeline פעמיים על אותה Task → TaskBrief מצטבר
- Task → Done → TaskBrief = ""

**שכבות 1+2 (GitHub + Container):**
- צור Board → connect GitHub
- הרץ pipeline → בדוק ACI נוצר ב-Azure Portal (נמחק אחרי הריצה)
- בדוק task "קרא README" → סוכן קרא קובץ דרך MCP
- בדוק task "כתוב test והרץ" → סוכן כתב, הריץ, ראה output
- בדוק task "פתח PR" → PR קיים ב-GitHub

**שכבה 3 (Foundry):**
- AgentRole נוצר → FoundryAgentId ב-Cosmos
- Task רץ → ThreadId ב-Cosmos
- Task → Done → Thread נמחק ב-Foundry
