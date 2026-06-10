# Migrate agent runtime to the new Foundry agent model — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the legacy Assistants-API agent runtime (`Azure.AI.Agents.Persistent`, `asst_…` ids) with the new Foundry agent model (name-keyed agents via `/agents`, runs via `/openai/v1/responses`, per-task `/conversations`), using raw REST.

**Architecture:** Rewrite `FoundryAgentRuntime` to call the new-Foundry REST endpoints with `HttpClient` + `DefaultAzureCredential` (token scope `https://ai.azure.com/.default`). The Foundry agent **name** is a stable app-generated token stored in `AgentRole.FoundryAgentId`; the user's display name stays UI-only (mirrored to the agent's `description`). Interfaces/DTOs and the mock are unchanged.

**Tech Stack:** .NET 10, C#, `System.Net.Http.Json`, `Azure.Identity`, xUnit.

**Live-verified REST contract (project `proj-agentteam`):**
- token scope `https://ai.azure.com/.default`; base = `FoundrySettings.ProjectEndpoint` (e.g. `https://aif-…services.ai.azure.com/api/projects/proj-agentteam`)
- create agent: `POST {base}/agents?api-version=v1` `{name, definition:{kind:"prompt",model,instructions,description?}}` → 200; **409 if name exists**
- new version: `POST {base}/agents/{name}/versions?api-version=v1` `{definition:{…}}` → 200 (version increments)
- get: `GET {base}/agents/{name}?api-version=v1` · delete: `DELETE {base}/agents/{name}?api-version=v1`
- conversation: `POST {base}/conversations?api-version=v1` `{}` → `{id:"conv_…"}`
- run: `POST {base}/openai/v1/responses` (no api-version) `{input, agent_reference:{name,type:"agent_reference"}, conversation?}` → `{id, status, output:[{type:"message",content:[{type:"output_text",text}]}], usage:{input_tokens,output_tokens}, error?}`
- agent **name == id** (no `asst_` guid); agents are versioned.

---

## File structure

- **Create** `src/agentruntime/FoundryAgentName.cs` — pure helper generating a stable, valid agent name.
- **Create** `tests/TectikaAgents.Tests/FoundryAgentNameTests.cs` — unit tests for the helper.
- **Rewrite** `src/agentruntime/FoundryAgentRuntime.cs` — raw-REST implementation (replaces the SDK one).
- **Modify** `src/agentruntime/TectikaAgents.AgentRuntime.csproj` — drop `Azure.AI.Agents.Persistent`; add `Microsoft.Extensions.Http`.

No changes to interfaces/DTOs (`IAgentRuntime`, `IAgentProvisioner`, `AgentRunRequest`, `AgentRunOutcome`), the mock, `AgentRolesController`, `InvokeAgentActivity`, or DI — only `FoundryAgentId` *semantics* change (now = stable agent name).

---

## Task 1: Stable agent-name generator (TDD)

**Files:**
- Create: `src/agentruntime/FoundryAgentName.cs`
- Test: `tests/TectikaAgents.Tests/FoundryAgentNameTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/TectikaAgents.Tests/FoundryAgentNameTests.cs`:
```csharp
using System.Text.RegularExpressions;
using TectikaAgents.AgentRuntime;
using Xunit;

public class FoundryAgentNameTests
{
    [Fact]
    public void New_StartsWithAgentPrefix()
        => Assert.StartsWith("agent-", FoundryAgentName.New());

    [Fact]
    public void New_IsLowercaseAlphanumericAndHyphensOnly()
        => Assert.Matches("^[a-z0-9-]+$", FoundryAgentName.New());

    [Fact]
    public void New_IsUniqueAcrossCalls()
        => Assert.NotEqual(FoundryAgentName.New(), FoundryAgentName.New());

    [Fact]
    public void New_IsWithinFoundryLengthLimit()
    {
        var n = FoundryAgentName.New();
        Assert.InRange(n.Length, 8, 63);
    }
}
```

- [ ] **Step 2: Run the test, verify it FAILS**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter FoundryAgentNameTests`
Expected: FAIL (compile error — `FoundryAgentName` does not exist).

- [ ] **Step 3: Implement**

`src/agentruntime/FoundryAgentName.cs`:
```csharp
namespace TectikaAgents.AgentRuntime;

/// <summary>Generates a stable, Foundry-valid agent name (lowercase alphanumeric + hyphens).
/// This becomes the agent's permanent identity (stored in AgentRole.FoundryAgentId) and is
/// deliberately decoupled from the user's display name, so renames never recreate the agent.</summary>
public static class FoundryAgentName
{
    public static string New() => $"agent-{Guid.NewGuid():N}"[..18]; // "agent-" + 12 hex
}
```

- [ ] **Step 4: Run the test, verify it PASSES**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter FoundryAgentNameTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment
git add src/agentruntime/FoundryAgentName.cs tests/TectikaAgents.Tests/FoundryAgentNameTests.cs
git commit -m "feat(agentruntime): stable Foundry agent-name generator (name-keyed identity)"
```

---

## Task 2: Rewrite FoundryAgentRuntime to the new-Foundry REST API

**Files:**
- Modify: `src/agentruntime/TectikaAgents.AgentRuntime.csproj`
- Rewrite: `src/agentruntime/FoundryAgentRuntime.cs`

- [ ] **Step 1: Swap the package reference in the csproj**

In `src/agentruntime/TectikaAgents.AgentRuntime.csproj`, **remove** the line
`<PackageReference Include="Azure.AI.Agents.Persistent" Version="1.1.0-beta.4" />`
and **add** (next to the other `Microsoft.Extensions.*` refs):
```xml
    <PackageReference Include="Microsoft.Extensions.Http" Version="9.0.0" />
```
Keep `Azure.Identity`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.Abstractions`. (`Azure.Core` types come transitively via `Azure.Identity`; `System.Net.Http.Json` is in the framework. If `Microsoft.Extensions.Http 9.0.0` doesn't restore, use the nearest version that does and note it.)

- [ ] **Step 2: Replace the entire contents of `src/agentruntime/FoundryAgentRuntime.cs`**

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

// ─────────────────────────────────────────────────────────────────────────────
// NEW Foundry agent REST contract (live-verified against proj-agentteam):
//   token scope https://ai.azure.com/.default ; base = FoundrySettings.ProjectEndpoint
//   create:   POST {base}/agents?api-version=v1   {name, definition:{kind:"prompt",model,instructions,description?}}  (409 if name exists)
//   version:  POST {base}/agents/{name}/versions?api-version=v1   {definition:{…}}
//   delete:   DELETE {base}/agents/{name}?api-version=v1
//   convo:    POST {base}/conversations?api-version=v1  {}  -> {id:"conv_…"}
//   run:      POST {base}/openai/v1/responses   {input, agent_reference:{name,type:"agent_reference"}, conversation?}
//             -> {id,status,output:[{type:"message",content:[{type:"output_text",text}]}],usage:{input_tokens,output_tokens},error?}
//   agent name == id (no asst_ guid); agents are versioned.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Real new-Foundry agent runtime + provisioner (raw REST). Persists the stable agent
/// name onto AgentRole.FoundryAgentId and the conversation id onto AgentTask.FoundryThreadId;
/// the caller saves them to Cosmos.</summary>
public sealed class FoundryAgentRuntime : IAgentRuntime, IAgentProvisioner
{
    private const string Api = "api-version=v1";
    private static readonly string[] Scopes = ["https://ai.azure.com/.default"];
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly FoundrySettings _settings;
    private readonly ILogger<FoundryAgentRuntime> _logger;
    private readonly TokenCredential _credential = new DefaultAzureCredential();
    private readonly string _base;

    /// <summary>Optional per-turn sink for the agent's output text (one event, non-streaming).</summary>
    public Action<string>? OnText { get; set; }
    public Action<string>? OnStatus { get; set; }

    public FoundryAgentRuntime(IHttpClientFactory httpFactory, IOptions<FoundrySettings> settings, ILogger<FoundryAgentRuntime> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _logger = logger;
        _base = _settings.ProjectEndpoint.TrimEnd('/');
    }

    private async Task<HttpClient> ClientAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false);
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);
        return http;
    }

    public async Task<AgentSyncResult> EnsureAgentAsync(AgentRole role, CancellationToken ct = default)
    {
        try
        {
            var model = role.ModelOverride ?? _settings.DefaultModel;
            var hash = AgentInstructionsHash.Compute(role.SystemPrompt, model);
            var definition = new AgentDefinition("prompt", model, role.SystemPrompt, role.DisplayName);
            var http = await ClientAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(role.FoundryAgentId))
            {
                var name = FoundryAgentName.New();
                var resp = await http.PostAsJsonAsync($"{_base}/agents?{Api}", new CreateAgentRequest(name, definition), Json, ct).ConfigureAwait(false);
                await EnsureOkAsync(resp, ct).ConfigureAwait(false);
                role.FoundryAgentId = name;
                role.FoundryAgentHash = hash;
                return new AgentSyncResult(name, true);
            }

            if (role.FoundryAgentHash != hash)
            {
                var resp = await http.PostAsJsonAsync($"{_base}/agents/{role.FoundryAgentId}/versions?{Api}", new NewVersionRequest(definition), Json, ct).ConfigureAwait(false);
                await EnsureOkAsync(resp, ct).ConfigureAwait(false);
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
        try
        {
            var http = await ClientAsync(ct).ConfigureAwait(false);
            await http.DeleteAsync($"{_base}/agents/{foundryAgentId}?{Api}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "DeleteAgent failed (ignored) for {Id}", foundryAgentId); }
    }

    public async Task<string> EnsureThreadAsync(AgentTask task, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(task.FoundryThreadId)) return task.FoundryThreadId!;
        var http = await ClientAsync(ct).ConfigureAwait(false);
        var resp = await http.PostAsJsonAsync($"{_base}/conversations?{Api}", new EmptyBody(), Json, ct).ConfigureAwait(false);
        await EnsureOkAsync(resp, ct).ConfigureAwait(false);
        var conv = await resp.Content.ReadFromJsonAsync<ConversationResponse>(Json, ct).ConfigureAwait(false);
        task.FoundryThreadId = conv!.Id;
        return task.FoundryThreadId!;
    }

    public async Task<AgentRunOutcome> RunTurnAsync(AgentRunRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(req.Role.FoundryAgentId))
            return Fail(req, "Role has no Foundry agent — ensure the agent first.");
        try
        {
            var http = await ClientAsync(ct).ConfigureAwait(false);
            var body = new ResponsesRequest(
                req.UserMessage,
                new AgentRef(req.Role.FoundryAgentId!, "agent_reference"),
                string.IsNullOrEmpty(req.ThreadId) ? null : req.ThreadId);
            var resp = await http.PostAsJsonAsync($"{_base}/openai/v1/responses", body, Json, ct).ConfigureAwait(false);
            await EnsureOkAsync(resp, ct).ConfigureAwait(false);
            var r = await resp.Content.ReadFromJsonAsync<ResponsesResult>(Json, ct).ConfigureAwait(false)
                    ?? throw new Exception("Empty response from Foundry.");

            OnStatus?.Invoke(r.Status ?? "");
            var content = ExtractText(r);
            var usage = new TokenUsage { Input = r.Usage?.InputTokens ?? 0, Output = r.Usage?.OutputTokens ?? 0 };
            if (!string.IsNullOrEmpty(content)) OnText?.Invoke(content);

            return r.Status switch
            {
                "completed" => new AgentRunOutcome(AgentRunStatus.Completed, content, DetectType(content, req.Role), usage, r.Id ?? ""),
                "incomplete" => new AgentRunOutcome(AgentRunStatus.BudgetExceeded, content, ArtifactContentType.Markdown, usage, r.Id ?? ""),
                _ => Fail(req, $"Foundry response status '{r.Status}': {r.Error?.Message}"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunTurn failed for role {Role} task {Task}", req.Role.Id, req.Task.Id);
            return Fail(req, ex.Message);
        }
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var bodyText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"Foundry {(int)resp.StatusCode} {resp.ReasonPhrase}: {bodyText}");
    }

    private static string ExtractText(ResponsesResult r)
    {
        if (r.Output is null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var item in r.Output)
            if (item.Type == "message" && item.Content is not null)
                foreach (var c in item.Content)
                    if (c.Type == "output_text" && c.Text is not null) sb.Append(c.Text);
        return sb.ToString();
    }

    private static AgentRunOutcome Fail(AgentRunRequest req, string error) =>
        new(AgentRunStatus.Failed, "", ArtifactContentType.Markdown, new TokenUsage(), $"run-{req.RunId}-{req.Step}", Error: error);

    private static ArtifactContentType DetectType(string content, AgentRole role)
    {
        if (role.Id.Contains("backend") || role.Id.Contains("devops") || role.Id.Contains("qa"))
            return ArtifactContentType.Code;
        var t = content.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('[')) return ArtifactContentType.Json;
        return ArtifactContentType.Markdown;
    }

    // ── REST DTOs ─────────────────────────────────────────────────────────────
    private sealed record EmptyBody();
    private sealed record AgentDefinition(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("instructions")] string Instructions,
        [property: JsonPropertyName("description")] string? Description);
    private sealed record CreateAgentRequest(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("definition")] AgentDefinition Definition);
    private sealed record NewVersionRequest(
        [property: JsonPropertyName("definition")] AgentDefinition Definition);
    private sealed record ConversationResponse(
        [property: JsonPropertyName("id")] string Id);
    private sealed record AgentRef(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type);
    private sealed record ResponsesRequest(
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("agent_reference")] AgentRef AgentReference,
        [property: JsonPropertyName("conversation")] string? Conversation);
    private sealed class ResponsesResult
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("output")] public List<OutputItem>? Output { get; set; }
        [JsonPropertyName("usage")] public UsageInfo? Usage { get; set; }
        [JsonPropertyName("error")] public ErrorInfo? Error { get; set; }
    }
    private sealed class OutputItem
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("content")] public List<ContentItem>? Content { get; set; }
    }
    private sealed class ContentItem
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
    private sealed class UsageInfo
    {
        [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
        [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    }
    private sealed class ErrorInfo
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
```
Notes for the implementer:
- The `OnText`/`OnStatus` public sinks are preserved (the workflows activity sets `OnText`). The ctor signature changed: it now takes `IHttpClientFactory` first. Both DI registrations resolve it — the API and Workflows `Program.cs` already call `builder.Services.AddHttpClient();`, so `IHttpClientFactory` is available. **Verify** both `Program.cs` files have `AddHttpClient()`; if either is missing it, add `builder.Services.AddHttpClient();`.
- `req.MaxCompletionTokens` is intentionally not sent (the Responses API output cap field is unverified; omitting it lets runs complete normally). Leave the field on the DTO unused.

- [ ] **Step 3: Build the library + full solution**

Run: `dotnet build src/agentruntime/TectikaAgents.AgentRuntime.csproj`
Expected: Build succeeded, 0 errors (no remaining references to `Azure.AI.Agents.Persistent` types).
Run: `dotnet build TectikaAgents.slnx`
Expected: Build succeeded across all projects (API + Workflows still compile against the unchanged `FoundryAgentRuntime` public surface — note the ctor now needs `IHttpClientFactory`, which DI provides; no call site constructs it manually).

- [ ] **Step 4: Confirm the mock unit tests still pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`
Expected: all PASS (the mock path is unchanged; `FoundryAgentName` tests included).

- [ ] **Step 5: Commit**

```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment
git add src/agentruntime/FoundryAgentRuntime.cs src/agentruntime/TectikaAgents.AgentRuntime.csproj
git commit -m "feat(agentruntime): migrate FoundryAgentRuntime to new-Foundry REST (/agents + /openai/v1/responses); drop legacy Assistants SDK"
```

---

## Task 3: Verify DI has IHttpClientFactory in both hosts

**Files:**
- Verify/Modify: `src/api/TectikaAgents.Api/Program.cs`, `src/workflows/Program.cs`

- [ ] **Step 1: Check both Program.cs register HttpClient**

Run: `grep -n "AddHttpClient" src/api/TectikaAgents.Api/Program.cs src/workflows/Program.cs`
Expected: each file has a `builder.Services.AddHttpClient();` (or `AddHttpClient<…>()`). The Workflows one is present (line ~73). **If the API one is missing**, add `builder.Services.AddHttpClient();` near its other service registrations.

- [ ] **Step 2: Build both hosts**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj && dotnet build src/workflows/TectikaAgents.Workflows.csproj`
Expected: both Build succeeded.

- [ ] **Step 3: Commit (only if a change was needed)**

```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment
git add src/api/TectikaAgents.Api/Program.cs
git commit -m "chore(api): register IHttpClientFactory for FoundryAgentRuntime"
```
(Skip the commit if no file changed.)

---

## Task 4: Deploy to ACA + Azure smoke (verification)

The new-Foundry runtime can only be verified live (no unit test for REST). Follow the manual-deploy procedure (memory `manual-container-app-deploy`). Only the API is needed (agent provisioning + a single-task run via the API path).

- [ ] **Step 1: Build, push, and deploy the API image**

```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment
az account set --subscription 929e4f09-f929-4ebe-b146-3723b1e283b5
az acr login --name tacragentteam
SHA=$(git rev-parse --short HEAD); RG=rg-agentteam-dev-001
docker build -f src/api/Dockerfile -t tacragentteam.azurecr.io/agentteam-api:$SHA -t tacragentteam.azurecr.io/agentteam-api:latest .
docker push tacragentteam.azurecr.io/agentteam-api:$SHA && docker push tacragentteam.azurecr.io/agentteam-api:latest
az containerapp update -n ca-agentteam-api -g $RG --image tacragentteam.azurecr.io/agentteam-api:$SHA --query "properties.provisioningState" -o tsv
```
Expected: `Succeeded`. Confirm health: `az containerapp revision list -n ca-agentteam-api -g $RG --query "sort_by([?properties.active],&properties.createdTime)[-1].{health:properties.healthState,running:properties.runningState}" -o json` → `Healthy/Running`.

- [ ] **Step 2: Smoke — create a new-model agent via the API**

```bash
API=https://ca-agentteam-api.calmstone-c10c7a54.westeurope.azurecontainerapps.io
curl -s -X POST "$API/api/agentroles" -H 'content-type: application/json' \
  -d '{"id":"role-newfoundry-smoke","displayName":"New Foundry Smoke","systemPrompt":"Reply only: PONG","modelOverride":"gpt-4o"}' \
  | python3 -c "import sys,json;d=json.load(sys.stdin);print('synced=',d['synced'],'error=',d['error'],'foundryAgentId=',d['role']['foundryAgentId'])"
```
Expected: `synced= True error= None foundryAgentId= agent-<12hex>` (a **name-keyed** id, not `asst_…`).

- [ ] **Step 3: Smoke — confirm it appears on the NEW agents surface**

```bash
PROJ="https://aif-agentteam-gbangabggobxy.services.ai.azure.com/api/projects/proj-agentteam"
TOKEN=$(az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv)
curl -s -H "Authorization: Bearer $TOKEN" "$PROJ/agents?api-version=v1" \
  | python3 -c "import sys,json;d=json.load(sys.stdin);print([(a.get('name'),a.get('id')) for a in d.get('data',[])])"
```
Expected: the `agent-<hex>` shows up in the `/agents` list (and is visible in the Foundry portal project Agents view — the original bug is fixed).

- [ ] **Step 4: Smoke — rename does not recreate the agent**

```bash
curl -s -X POST "$API/api/agentroles" -H 'content-type: application/json' \
  -d '{"id":"role-newfoundry-smoke","displayName":"Renamed Smoke","systemPrompt":"Reply only: PONG","modelOverride":"gpt-4o"}' \
  | python3 -c "import sys,json;d=json.load(sys.stdin);print('foundryAgentId=',d['role']['foundryAgentId'])"
```
Expected: **same** `foundryAgentId` as Step 2 (rename changed only the display name; no new agent). Confirm `/agents` list still has exactly one `agent-…` for this role.

- [ ] **Step 5: Clean up the smoke agent**

```bash
curl -s -X DELETE "$API/api/agentroles/role-newfoundry-smoke" -w "HTTP %{http_code}\n"
curl -s -H "Authorization: Bearer $TOKEN" "$PROJ/agents?api-version=v1" \
  | python3 -c "import sys,json;print('agents remaining:',len(json.load(sys.stdin).get('data',[])))"
```
Expected: `HTTP 204`; `agents remaining: 0`.

- [ ] **Step 6 (optional, end-to-end run):** if the workflows Function App is deployed, assign a synced agent to a task and `POST /api/runs/start`, then confirm an artifact is produced and the run uses `/openai/v1/responses`. (Out of scope if the Function App isn't deployed — note it.)

---

## Notes & risks
- **Beta API surface:** all REST shapes were live-verified on 2026-06-10; api-version `v1` for `/agents`+`/conversations`, none for `/openai/v1/responses`. If a field is rejected, capture the error body (the `EnsureOkAsync` helper includes it) and adjust.
- **Update path:** new versions use `POST /agents/{name}/versions` (verified); `POST /agents` with an existing name 409s.
- **`FoundryAgentId` semantics** change to the stable agent name — no data migration needed (all live values are null).
- **Durable replay / DI lifetime** unchanged from Phase 1 (runtime stays Transient in workflows; `OnText` set per call).
