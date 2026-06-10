# Detailed Logging → `ai-agentteam` App Insights — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship full, detailed, structured logging from web, API, and workflows to the existing `ai-agentteam` Application Insights resource, with a single switch to drop sensitive content for production.

**Architecture:** API uses Azure Monitor OpenTelemetry (matching workflows, which is already wired). All sensitive log sites pass through one shared redaction helper gated by a `Logging:LogSensitiveContent` flag (default `true`). The Next.js web app gets the App Insights browser SDK, with its connection string injected at **runtime** by the server root layout (no `NEXT_PUBLIC_` build-time baking). Every enrichment message is prefixed with a `[Topic]` tag.

**Tech Stack:** .NET 10 (API) / .NET 9 (workflows, Functions isolated) / `Azure.Monitor.OpenTelemetry.AspNetCore` / `@microsoft/applicationinsights-web` / Bicep / xunit.

**Spec:** `docs/superpowers/specs/2026-06-10-detailed-logging-app-insights-design.md`

---

## File Structure

**New files:**
- `src/core/TectikaAgents.Core/Configuration/LoggingSettings.cs` — the `LogSensitiveContent` flag POCO.
- `src/core/TectikaAgents.Core/Observability/SensitiveContent.cs` — pure redaction helper (the testable core).
- `src/api/TectikaAgents.Api/Middleware/RequestLoggingMiddleware.cs` — HTTP request/response logging, bodies gated by the flag.
- `src/web/tectika-board/src/lib/telemetry.ts` — App Insights singleton + `trackEvent`/`trackException`/`trackTrace`/`redact` helpers.
- `src/web/tectika-board/src/components/observability/TelemetryProvider.tsx` — `'use client'` provider that initializes App Insights from a runtime-injected connection string.
- `tests/TectikaAgents.Tests/SensitiveContentTests.cs` — unit tests for the redaction helper.
- `tests/TectikaAgents.Tests/LoggingSettingsTests.cs` — unit tests for the flag default + binding.

**Modified files:**
- `src/agentruntime/FoundryAgentRuntime.cs` — inject `LoggingSettings`, enrich Foundry calls.
- `src/api/TectikaAgents.Api/Program.cs` — register OTel/Azure Monitor, `LoggingSettings`, middleware.
- `src/api/TectikaAgents.Api/appsettings.json` — log levels + `LogSensitiveContent`.
- `src/api/TectikaAgents.Api/TectikaAgents.Api.csproj` — add Azure Monitor OTel package.
- API controllers/services (enrichment) — listed in Tasks 5–6.
- `src/workflows/Program.cs` — register `LoggingSettings`.
- Workflows orchestrator/activities/triggers/services (enrichment) — listed in Task 8.
- `src/web/tectika-board/src/app/layout.tsx` — read env, render `<TelemetryProvider>`.
- `src/web/tectika-board/src/lib/api.ts` — instrument the fetch client.
- `src/web/tectika-board/package.json` — add App Insights deps.
- `infra/modules/containerapps.bicep`, `infra/modules/functionapp.bicep` — env vars (kept idempotent).

---

## Task 1: Core — `LoggingSettings` flag + `SensitiveContent` redaction helper (TDD)

**Files:**
- Create: `src/core/TectikaAgents.Core/Configuration/LoggingSettings.cs`
- Create: `src/core/TectikaAgents.Core/Observability/SensitiveContent.cs`
- Test: `tests/TectikaAgents.Tests/SensitiveContentTests.cs`
- Test: `tests/TectikaAgents.Tests/LoggingSettingsTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/TectikaAgents.Tests/SensitiveContentTests.cs`:

```csharp
using TectikaAgents.Core.Observability;
using Xunit;

namespace TectikaAgents.Tests;

public class SensitiveContentTests
{
    [Fact]
    public void Format_ReturnsContent_WhenLoggingEnabled()
    {
        Assert.Equal("secret prompt", SensitiveContent.Format("secret prompt", logSensitive: true));
    }

    [Fact]
    public void Format_RedactsWithLength_WhenLoggingDisabledAndContentPresent()
    {
        Assert.Equal("[redacted](6 chars)", SensitiveContent.Format("abcdef", logSensitive: false));
    }

    [Fact]
    public void Format_ReturnsBareRedacted_WhenLoggingDisabledAndContentEmpty()
    {
        Assert.Equal("[redacted]", SensitiveContent.Format("", logSensitive: false));
        Assert.Equal("[redacted]", SensitiveContent.Format(null, logSensitive: false));
    }

    [Fact]
    public void Format_ReturnsEmpty_WhenLoggingEnabledAndContentNull()
    {
        Assert.Equal("", SensitiveContent.Format(null, logSensitive: true));
    }
}
```

`tests/TectikaAgents.Tests/LoggingSettingsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using TectikaAgents.Core.Configuration;
using Xunit;

namespace TectikaAgents.Tests;

public class LoggingSettingsTests
{
    [Fact]
    public void LogSensitiveContent_DefaultsToTrue()
    {
        Assert.True(new LoggingSettings().LogSensitiveContent);
    }

    [Fact]
    public void Binds_LogSensitiveContent_False_FromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogSensitiveContent"] = "false",
            })
            .Build();

        var settings = config.GetSection("Logging").Get<LoggingSettings>()!;
        Assert.False(settings.LogSensitiveContent);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "SensitiveContentTests|LoggingSettingsTests"`
Expected: FAIL to compile — `SensitiveContent` and `LoggingSettings` do not exist.

- [ ] **Step 3: Create `LoggingSettings`**

`src/core/TectikaAgents.Core/Configuration/LoggingSettings.cs`:

```csharp
namespace TectikaAgents.Core.Configuration;

/// <summary>
/// Bound from the "Logging" configuration section. Controls whether sensitive content
/// (request/response bodies, agent prompts, model outputs) is written to logs.
/// Defaults to true (log everything); flip to false for production via
/// the Logging__LogSensitiveContent environment variable.
/// </summary>
public class LoggingSettings
{
    public bool LogSensitiveContent { get; set; } = true;
}
```

- [ ] **Step 4: Create `SensitiveContent`**

`src/core/TectikaAgents.Core/Observability/SensitiveContent.cs`:

```csharp
namespace TectikaAgents.Core.Observability;

/// <summary>
/// Single gate for sensitive log content. Pass the resolved LogSensitiveContent flag and the
/// raw value; when logging is disabled, only a redaction marker (with length) is returned so
/// the field stays queryable without leaking content.
/// </summary>
public static class SensitiveContent
{
    public const string RedactedPlaceholder = "[redacted]";

    public static string Format(string? content, bool logSensitive)
    {
        if (logSensitive) return content ?? string.Empty;
        if (string.IsNullOrEmpty(content)) return RedactedPlaceholder;
        return $"{RedactedPlaceholder}({content.Length} chars)";
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "SensitiveContentTests|LoggingSettingsTests"`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/core/TectikaAgents.Core/Configuration/LoggingSettings.cs \
        src/core/TectikaAgents.Core/Observability/SensitiveContent.cs \
        tests/TectikaAgents.Tests/SensitiveContentTests.cs \
        tests/TectikaAgents.Tests/LoggingSettingsTests.cs
git commit -m "feat(core): add LoggingSettings flag and SensitiveContent redaction helper"
```

---

## Task 2: Infra — env vars for web app + parity flags (idempotent Bicep)

**Files:**
- Modify: `infra/modules/containerapps.bicep` (web app env list; API app env list)
- Modify: `infra/modules/functionapp.bicep` (appSettings list)

> The `appInsightsConnectionString` param already exists in `containerapps.bicep` (line 23) and is consumed by the API app. The web app currently only sets `NODE_ENV`.

- [ ] **Step 1: Add env vars to the web container app**

In `infra/modules/containerapps.bicep`, find the `webApp` container `env` block:

```bicep
          env: [
            { name: 'NODE_ENV', value: 'production' }
          ]
```

Replace with:

```bicep
          env: [
            { name: 'NODE_ENV', value: 'production' }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'Logging__LogSensitiveContent', value: 'true' }
          ]
```

- [ ] **Step 2: Add the parity flag to the API container app**

In `infra/modules/containerapps.bicep`, in the `apiApp` `env` list, immediately after the existing `APPLICATIONINSIGHTS_CONNECTION_STRING` line, add:

```bicep
            { name: 'Logging__LogSensitiveContent', value: 'true' }
```

- [ ] **Step 3: Add the parity flag to the Functions app**

In `infra/modules/functionapp.bicep`, in the `appSettings` list, immediately after the existing `APPLICATIONINSIGHTS_CONNECTION_STRING` line, add:

```bicep
        { name: 'Logging__LogSensitiveContent', value: 'true' }
```

- [ ] **Step 4: Validate the Bicep compiles**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo OK`
Expected: `OK` with no errors. (If `az` is unavailable in the worker, skip with a note — these are additive literal env entries and cannot break references.)

- [ ] **Step 5: Commit**

```bash
git add infra/modules/containerapps.bicep infra/modules/functionapp.bicep
git commit -m "infra: inject App Insights conn string into web app + LogSensitiveContent flag (idempotent)"
```

---

## Task 3: API — wire Azure Monitor OpenTelemetry + log levels

**Files:**
- Modify: `src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
- Modify: `src/api/TectikaAgents.Api/Program.cs`
- Modify: `src/api/TectikaAgents.Api/appsettings.json`

- [ ] **Step 1: Add the Azure Monitor OTel package**

In `src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`, inside the package `ItemGroup`, add:

```xml
    <PackageReference Include="Azure.Monitor.OpenTelemetry.AspNetCore" Version="1.3.0" />
```

- [ ] **Step 2: Wire OpenTelemetry in `Program.cs`**

In `src/api/TectikaAgents.Api/Program.cs`, immediately after the line `var builder = WebApplication.CreateBuilder(args);`, add:

```csharp
// ── Observability (Azure Monitor / OpenTelemetry) ────────────────────────────
// Guarded on the connection string so local dev (no App Insights) is unaffected.
// Routes all ILogger output to App Insights and auto-captures incoming requests,
// outgoing HttpClient dependencies, and exceptions. Sampling left at full capture
// for now (see spec — primary cost knob).
var aiConnStr = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
    ?? Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
if (!string.IsNullOrEmpty(aiConnStr))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor(o =>
    {
        o.ConnectionString = aiConnStr;
        o.SamplingRatio = 1.0f; // capture everything for now
    });
}
```

Add the using at the top of the file (with the other `using` directives):

```csharp
using Azure.Monitor.OpenTelemetry.AspNetCore;
```

- [ ] **Step 3: Set log levels in `appsettings.json`**

In `src/api/TectikaAgents.Api/appsettings.json`, replace the `"Logging"` block:

```json
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
```

with:

```json
  "Logging": {
    "LogSensitiveContent": true,
    "LogLevel": {
      "Default": "Information",
      "TectikaAgents": "Debug",
      "Microsoft": "Warning",
      "Microsoft.AspNetCore": "Warning",
      "System": "Warning"
    }
  },
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/TectikaAgents.Api.csproj \
        src/api/TectikaAgents.Api/Program.cs \
        src/api/TectikaAgents.Api/appsettings.json
git commit -m "feat(api): wire Azure Monitor OpenTelemetry and structured log levels"
```

---

## Task 4: API — register `LoggingSettings` + request-logging middleware

**Files:**
- Create: `src/api/TectikaAgents.Api/Middleware/RequestLoggingMiddleware.cs`
- Modify: `src/api/TectikaAgents.Api/Program.cs`

- [ ] **Step 1: Register `LoggingSettings` in `Program.cs`**

In `src/api/TectikaAgents.Api/Program.cs`, in the `// ── Configuration ──` block (next to the other `builder.Services.Configure<...>` lines), add:

```csharp
builder.Services.Configure<LoggingSettings>(builder.Configuration.GetSection("Logging"));
```

Ensure this using is present at the top (it already is via `TectikaAgents.Core.Configuration`).

- [ ] **Step 2: Create the request-logging middleware**

`src/api/TectikaAgents.Api/Middleware/RequestLoggingMiddleware.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Observability;

namespace TectikaAgents.Api.Middleware;

/// <summary>
/// Logs one structured line per HTTP request: method, path, status, duration, user.
/// The request body is logged only when LogSensitiveContent is enabled.
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;
    private readonly bool _logSensitive;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger,
        IOptions<LoggingSettings> logging)
    {
        _next = next;
        _logger = logger;
        _logSensitive = logging.Value.LogSensitiveContent;
    }

    public async Task Invoke(HttpContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var body = string.Empty;
        if (_logSensitive && (HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method)
            || HttpMethods.IsPatch(ctx.Request.Method)))
        {
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            body = await reader.ReadToEndAsync();
            ctx.Request.Body.Position = 0;
        }

        _logger.LogInformation(
            "[HttpRequest] {Method} {Path} from user {User} body {Body}",
            ctx.Request.Method, ctx.Request.Path, ctx.User?.Identity?.Name ?? "anonymous",
            SensitiveContent.Format(body, _logSensitive));

        try
        {
            await _next(ctx);
        }
        finally
        {
            sw.Stop();
            _logger.LogInformation(
                "[HttpResponse] {Method} {Path} -> {Status} in {ElapsedMs}ms",
                ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
        }
    }
}
```

- [ ] **Step 3: Register the middleware in the pipeline**

In `src/api/TectikaAgents.Api/Program.cs`, in the HTTP pipeline section, add the middleware immediately after `app.UseCors("NextJs");`:

```csharp
app.UseMiddleware<TectikaAgents.Api.Middleware.RequestLoggingMiddleware>();
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Middleware/RequestLoggingMiddleware.cs src/api/TectikaAgents.Api/Program.cs
git commit -m "feat(api): add request/response logging middleware gated by LogSensitiveContent"
```

---

## Task 5: API — enrich controllers

**Files (modify, add `[Topic]` structured logs):**
- `src/api/TectikaAgents.Api/Controllers/RunsController.cs`
- `src/api/TectikaAgents.Api/Controllers/ApprovalsController.cs`
- `src/api/TectikaAgents.Api/Controllers/InteractionsController.cs`

> These three already inject `ILogger<...>`. Pattern: log **action entry** (Information) with route IDs, the **outcome** (Information), and **warnings** for validation/not-found. Keep messages structured with the `[Topic]` prefix. Apply at the start and at each return branch of every public action method.

- [ ] **Step 1: Enrich `RunsController`**

For each public action (e.g. starting a run, getting run status), add at method entry and outcome. Example for the run-start action — add as the first line of the method body, then before the success return:

```csharp
_logger.LogInformation("[RunStart] received start request board={BoardId} task={TaskId}", boardId, taskId);
// ... existing logic ...
_logger.LogInformation("[RunStart] run {RunId} started for task {TaskId}", run.Id, taskId);
```

For not-found / bad-request branches, add before the return:

```csharp
_logger.LogWarning("[RunStart] task {TaskId} not found on board {BoardId}", taskId, boardId);
```

Apply the same shape (entry + outcome + warning) to every other public action in the controller, choosing a `[Topic]` that names the operation (e.g. `[RunStatus]`, `[RunList]`).

- [ ] **Step 2: Enrich `ApprovalsController`**

At the start of the approve/reject action and before each return:

```csharp
_logger.LogInformation("[ApprovalDecision] approval {ApprovalId} decision={Decision} by {User}",
    approvalId, decision, User?.Identity?.Name ?? "anonymous");
```

Warning branch when the approval is missing/already-decided:

```csharp
_logger.LogWarning("[ApprovalDecision] approval {ApprovalId} not actionable (missing or already decided)", approvalId);
```

Apply entry + outcome + warning to each public action.

- [ ] **Step 3: Enrich `InteractionsController`**

At the start of each public action and before returns:

```csharp
_logger.LogInformation("[Interaction] {Method} interaction {InteractionId} for task {TaskId}",
    Request.Method, interactionId, taskId);
```

Use a `[Topic]` per operation (e.g. `[InteractionReply]`, `[InteractionList]`). Add warnings for not-found branches.

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/RunsController.cs \
        src/api/TectikaAgents.Api/Controllers/ApprovalsController.cs \
        src/api/TectikaAgents.Api/Controllers/InteractionsController.cs
git commit -m "feat(api): enrich Runs/Approvals/Interactions controllers with [Topic] structured logs"
```

---

## Task 6: API — enrich services

**Files (modify):**
- `src/api/TectikaAgents.Api/Services/RunStartService.cs`
- `src/api/TectikaAgents.Api/Services/ServiceBusListenerService.cs`
- `src/api/TectikaAgents.Api/Services/CliBridgeManager.cs`
- `src/api/TectikaAgents.Api/Services/SseConnectionManager.cs`
- `src/api/TectikaAgents.Api/Services/CosmosDbService.cs`
- `src/api/TectikaAgents.Api/Services/AppRegistrationIdentityService.cs`

> All except `CosmosDbService` and `AppRegistrationIdentityService` already inject `ILogger`. For those two, add a `ILogger<T>` constructor parameter and a `private readonly ILogger<T> _logger;` field (DI provides it — no registration change needed).

- [ ] **Step 1: Enrich `RunStartService.StartAsync`**

At the start of `StartAsync(string boardId, string taskId, string tenantId, ...)`:

```csharp
_logger.LogInformation("[RunStart] StartAsync board={BoardId} task={TaskId} tenant={TenantId}",
    boardId, taskId, tenantId);
```

Before each return (success and the null/early-exit paths), add an outcome log, e.g.:

```csharp
_logger.LogInformation("[RunStart] created run {RunId} for task {TaskId}", run.Id, taskId);
// and on the null path:
_logger.LogWarning("[RunStart] could not start run for task {TaskId} (not found or not eligible)", taskId);
```

- [ ] **Step 2: Enrich `ServiceBusListenerService`**

On message received, processed, dead-lettered, and on the listener start/stop lifecycle:

```csharp
_logger.LogInformation("[ServiceBusListener] received message {MessageId} subject={Subject}",
    args.Message.MessageId, args.Message.Subject);
_logger.LogInformation("[ServiceBusListener] dispatched event {EventType} for run {RunId}", eventType, runId);
_logger.LogError(ex, "[ServiceBusListener] failed processing message {MessageId}", args.Message.MessageId);
```

Place at the corresponding existing handler points.

- [ ] **Step 3: Enrich `CliBridgeManager` and `SseConnectionManager`**

`SseConnectionManager` — on connect/disconnect/broadcast:

```csharp
_logger.LogInformation("[Sse] client connected channel={Channel} total={Count}", channel, count);
_logger.LogInformation("[Sse] client disconnected channel={Channel} total={Count}", channel, count);
_logger.LogDebug("[Sse] broadcast event={Event} to {Count} clients on channel={Channel}", evt, count, channel);
```

`CliBridgeManager` — on session open/close, command in/out:

```csharp
_logger.LogInformation("[CliBridge] session {SessionId} opened", sessionId);
_logger.LogInformation("[CliBridge] session {SessionId} closed", sessionId);
_logger.LogDebug("[CliBridge] session {SessionId} message kind={Kind}", sessionId, kind);
```

- [ ] **Step 4: Add a logger to `CosmosDbService` and enrich**

Add the ctor param + field, then log key operations (container creation, queries, writes, not-found):

```csharp
_logger.LogDebug("[CosmosWrite] upsert {Type} id={Id} partition={Partition}", typeof(T).Name, id, partition);
_logger.LogDebug("[CosmosRead] query {Type} -> {Count} items", typeof(T).Name, results.Count);
_logger.LogInformation("[CosmosInfra] ensured database {Db} and containers", databaseName);
```

(Use whatever the actual method/variable names are in the file; match the existing generic/method shapes.)

- [ ] **Step 5: Add a logger to `AppRegistrationIdentityService` and enrich**

Add the ctor param + field, then log identity provisioning:

```csharp
_logger.LogInformation("[Identity] ensuring app registration for agent {AgentId}", agentId);
_logger.LogInformation("[Identity] app registration {AppId} ready for agent {AgentId}", appId, agentId);
_logger.LogError(ex, "[Identity] failed provisioning app registration for agent {AgentId}", agentId);
```

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/
git commit -m "feat(api): enrich services (RunStart, ServiceBus, Cli, Sse, Cosmos, Identity) with [Topic] logs"
```

---

## Task 7: Shared AgentRuntime — inject `LoggingSettings` + enrich `FoundryAgentRuntime`

**Files:**
- Modify: `src/agentruntime/FoundryAgentRuntime.cs`
- Modify: `src/api/TectikaAgents.Api/Program.cs` (register `LoggingSettings` — done in Task 4; verify)
- Modify: `src/workflows/Program.cs` (register `LoggingSettings` — see Task 8 Step 1)

> `FoundryAgentRuntime` already injects `ILogger<FoundryAgentRuntime>`. We add `IOptions<LoggingSettings>` so prompts/outputs can be redacted. It is resolved by DI in both hosts; no manual constructions exist (verified), so the only requirement is that both hosts call `Configure<LoggingSettings>(...)`.

- [ ] **Step 1: Add `LoggingSettings` to the constructor**

In `src/agentruntime/FoundryAgentRuntime.cs`, change the constructor (line ~49) from:

```csharp
public FoundryAgentRuntime(IHttpClientFactory httpFactory, IOptions<FoundrySettings> settings, ILogger<FoundryAgentRuntime> logger)
```

to also take logging settings, and store the flag in a field:

```csharp
public FoundryAgentRuntime(IHttpClientFactory httpFactory, IOptions<FoundrySettings> settings,
    IOptions<LoggingSettings> logging, ILogger<FoundryAgentRuntime> logger)
```

Add near the other private fields (around line 41):

```csharp
private readonly bool _logSensitive;
```

And in the constructor body, assign it:

```csharp
_logSensitive = logging.Value.LogSensitiveContent;
```

Add the usings at the top of the file if not present:

```csharp
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Observability;
```

- [ ] **Step 2: Enrich `EnsureAgentAsync`**

At the start of `EnsureAgentAsync(AgentRole role, ...)` and at the result:

```csharp
_logger.LogInformation("[FoundryEnsureAgent] ensuring agent role={RoleId} model={Model}", role.Id, role.Model);
// ... at the end, before return:
_logger.LogInformation("[FoundryEnsureAgent] agent role={RoleId} foundryId={FoundryId} created={Created}",
    role.Id, result.FoundryAgentId, result.Created);
```

- [ ] **Step 3: Enrich `EnsureThreadAsync` and `DeleteAgentAsync`**

```csharp
// EnsureThreadAsync entry/result:
_logger.LogInformation("[FoundryThread] ensuring thread for task {TaskId}", task.Id);
_logger.LogInformation("[FoundryThread] thread {ThreadId} ready for task {TaskId}", threadId, task.Id);
// DeleteAgentAsync:
_logger.LogInformation("[FoundryDeleteAgent] deleting foundry agent {FoundryId}", foundryAgentId);
```

- [ ] **Step 4: Enrich `RunTurnAsync` (the agent invocation) with redacted prompt/output**

At the start of `RunTurnAsync(AgentRunRequest req, ...)`:

```csharp
_logger.LogInformation("[FoundryAgentInvoke] running turn agent={AgentId} thread={ThreadId} model={Model} prompt={Prompt}",
    req.FoundryAgentId, req.ThreadId, req.Model, SensitiveContent.Format(req.Prompt, _logSensitive));
```

> Use the real property names on `AgentRunRequest`/`AgentRunOutcome` (open the file to confirm; the prompt/message field and the model field). If a single prompt string is not present, redact whatever the user/message content field is.

Before the successful return, log the outcome with redacted output and any token/usage info available:

```csharp
_logger.LogInformation("[FoundryAgentInvoke] turn complete agent={AgentId} status={Status} output={Output}",
    req.FoundryAgentId, outcome.Status, SensitiveContent.Format(outcome.Text, _logSensitive));
```

In the `EnsureOkAsync` failure path / catch, log the error:

```csharp
_logger.LogError("[FoundryAgentInvoke] Foundry call failed status={Status} body={Body}",
    (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
```

- [ ] **Step 5: Build agentruntime (both target frameworks)**

Run: `dotnet build src/agentruntime/TectikaAgents.AgentRuntime.csproj`
Expected: Build succeeded for net9.0 and net10.0, 0 errors.

- [ ] **Step 6: Run the existing agentruntime-related tests**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FoundryAgentNameTests|AgentInstructionsHashTests"`
Expected: PASS (constructor change is DI-only; these tests should be unaffected).

- [ ] **Step 7: Commit**

```bash
git add src/agentruntime/FoundryAgentRuntime.cs
git commit -m "feat(agentruntime): enrich FoundryAgentRuntime with [Topic] logs and redacted prompt/output"
```

---

## Task 8: Workflows — register `LoggingSettings` + enrich orchestrator/activities/triggers/services

**Files:**
- Modify: `src/workflows/Program.cs`
- Modify: `src/workflows/Orchestrators/TaskPipelineOrchestrator.cs`
- Modify: `src/workflows/Activities/*.cs` (6 activities)
- Modify: `src/workflows/Triggers/HttpTrigger.cs`
- Modify: `src/workflows/Services/ContextManager.cs`, `WorkflowCosmosService.cs`, `WorkflowEventPublisher.cs`

- [ ] **Step 1: Register `LoggingSettings` in workflows `Program.cs`**

In `src/workflows/Program.cs`, in the `// ── Configuration ──` block, add:

```csharp
builder.Services.Configure<LoggingSettings>(builder.Configuration.GetSection("Logging"));
```

(The `using TectikaAgents.Core.Configuration;` is already present.)

- [ ] **Step 2: Enrich the orchestrator with a replay-safe logger**

`TaskPipelineOrchestrator.RunOrchestration` has **no logger** today. Durable orchestrators must use a **replay-safe** logger or logs duplicate on every replay. At the top of `RunOrchestration`, after `context` is available, add:

```csharp
var logger = context.CreateReplaySafeLogger<TaskPipelineOrchestrator>();
logger.LogInformation("[Pipeline] start task={TaskId} run={RunId}", input.TaskId, input.RunId);
```

> Use the actual property names on the orchestrator input (`PipelineInput` record, near the bottom of the file). Add a log before each major stage transition and at the terminal return:

```csharp
logger.LogInformation("[Pipeline] stage={Stage} task={TaskId}", "AwaitingApproval", input.TaskId);
logger.LogInformation("[Pipeline] complete task={TaskId} status={Status}", input.TaskId, result.Status);
```

Add `using Microsoft.DurableTask;` if `CreateReplaySafeLogger` is not resolved (it lives in the Durable Task extension namespace already referenced by the project).

- [ ] **Step 3: Enrich the activities**

Each activity class injects (or should inject) `ILogger<T>`. `InvokeAgentActivity` already logs with an `InvokeAgent:` prefix — **migrate those to the `[Topic]` format** and add entry/exit/error around the agent call:

```csharp
// replace existing "InvokeAgent: role=..." with:
_logger.LogInformation("[InvokeAgent] role={Role} task={Task} step={Step}", role.Id, taskId, step);
// keep the error log but rebrand:
_logger.LogError(ex, "[InvokeAgent] invocation failed role={Role} task={Task}", role.Id, taskId);
// rebrand the revision-needed log:
_logger.LogInformation("[InvokeAgent] REVISION_NEEDED task={Task} step={Step} reason={Reason}", taskId, step, reason);
```

For the other five activities (`AppendTaskBriefActivity`, `UpdateRunStatusActivity`, `WriteApprovalActivity`, `WriteAuditActivity`, `WriteInteractionActivity`) — if they lack an `ILogger<T>`, add the ctor param + field, then add an entry log naming the operation:

```csharp
_logger.LogInformation("[UpdateRunStatus] run={RunId} -> {Status}", runId, status);
_logger.LogInformation("[WriteApproval] created approval {ApprovalId} for task {TaskId}", approvalId, taskId);
_logger.LogInformation("[WriteAudit] task={TaskId} event={Event}", taskId, auditEvent);
_logger.LogInformation("[WriteInteraction] created interaction {InteractionId} for task {TaskId}", interactionId, taskId);
_logger.LogInformation("[AppendTaskBrief] appended brief to task {TaskId}", taskId);
```

(Use the real parameter names from each activity's `Run`/`[Function]` method.)

- [ ] **Step 4: Enrich `HttpTrigger`**

At the start of each trigger function (pipeline start, etc.):

```csharp
_logger.LogInformation("[HttpTrigger] {Function} invoked", nameof(/* function */));
_logger.LogInformation("[HttpTrigger] started orchestration {InstanceId} for task {TaskId}", instanceId, taskId);
```

(`HttpTrigger` may need an `ILogger<HttpTrigger>` added if not present.)

- [ ] **Step 5: Enrich workflow services with redaction**

`ContextManager`, `WorkflowCosmosService`, `WorkflowEventPublisher` — add `ILogger<T>` where missing and, for context/payload content, gate with `SensitiveContent.Format(..., _logSensitive)` (inject `IOptions<LoggingSettings>` to get `_logSensitive`). Examples:

```csharp
// WorkflowCosmosService
_logger.LogDebug("[WorkflowCosmos] read task {TaskId}", taskId);
_logger.LogDebug("[WorkflowCosmos] wrote {Type} id={Id}", type, id);
// WorkflowEventPublisher
_logger.LogInformation("[WorkflowEvent] published {EventType} for run {RunId}", eventType, runId);
// ContextManager (payload redacted)
_logger.LogDebug("[Context] built context for task {TaskId} content={Content}",
    taskId, SensitiveContent.Format(contextText, _logSensitive));
```

- [ ] **Step 6: Build the workflows project**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Run the workflow-related tests**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "ContextManagerTests|RunPipelineFactoryTests|MockAgentRuntimeTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/workflows/
git commit -m "feat(workflows): register LoggingSettings and enrich orchestrator/activities/triggers/services with [Topic] logs"
```

---

## Task 9: Web — App Insights SDK + runtime-injected `TelemetryProvider`

**Files:**
- Modify: `src/web/tectika-board/package.json`
- Create: `src/web/tectika-board/src/lib/telemetry.ts`
- Create: `src/web/tectika-board/src/components/observability/TelemetryProvider.tsx`
- Modify: `src/web/tectika-board/src/app/layout.tsx`

> **Next.js 16 caveat:** consult `node_modules/next/dist/docs/01-app/` before writing code. We deliberately use the **server layout → client provider** runtime-injection pattern (NOT `instrumentation-client.ts`, which only sees build-time `NEXT_PUBLIC_` env). MSAL is installed but not wired anywhere in `src/`, so user/session context relies on App Insights' built-in session tracking for now (no MSAL dependency).

- [ ] **Step 1: Add the App Insights packages**

Run:

```bash
cd src/web/tectika-board && npm install @microsoft/applicationinsights-web@^3
```

Expected: `@microsoft/applicationinsights-web` added to `dependencies` in `package.json`. (The `-react-js` plugin from the spec is **not** needed: SPA route tracking is handled by `enableAutoRouteTracking` and we expose telemetry via a plain singleton, so we omit the extra dependency — YAGNI.)

- [ ] **Step 2: Create the telemetry singleton + helpers**

`src/web/tectika-board/src/lib/telemetry.ts`:

```ts
// App Insights browser singleton. Initialized once from a runtime-injected connection
// string (see TelemetryProvider). No-ops cleanly when no connection string is present
// (local dev), so call sites never need to guard.
'use client';

import { ApplicationInsights } from '@microsoft/applicationinsights-web';

let ai: ApplicationInsights | null = null;
let logSensitive = true;

export function initTelemetry(connectionString: string, sensitive: boolean): ApplicationInsights | null {
  if (ai || !connectionString) return ai;
  logSensitive = sensitive;
  ai = new ApplicationInsights({
    config: {
      connectionString,
      enableAutoRouteTracking: true,          // SPA route changes -> page views
      enableCorsCorrelation: true,            // correlate fetch() to the API
      enableRequestHeaderTracking: true,
      enableResponseHeaderTracking: true,
      disableFetchTracking: false,            // auto dependency tracking for fetch
      enableUnhandledPromiseRejectionTracking: true,
      autoTrackPageVisitTime: true,
    },
  });
  ai.loadAppInsights();
  ai.trackPageView();
  return ai;
}

/** Redact a value for logging unless sensitive logging is enabled. */
export function redact(value: string | undefined | null): string {
  if (logSensitive) return value ?? '';
  if (!value) return '[redacted]';
  return `[redacted](${value.length} chars)`;
}

export function trackEvent(name: string, properties?: Record<string, unknown>): void {
  ai?.trackEvent({ name }, properties as Record<string, string>);
}

export function trackTrace(message: string, properties?: Record<string, unknown>): void {
  ai?.trackTrace({ message }, properties as Record<string, string>);
}

export function trackException(error: unknown, properties?: Record<string, unknown>): void {
  ai?.trackException(
    { exception: error instanceof Error ? error : new Error(String(error)) },
    properties as Record<string, string>,
  );
}
```

- [ ] **Step 3: Create the `TelemetryProvider` client component**

`src/web/tectika-board/src/components/observability/TelemetryProvider.tsx`:

```tsx
'use client';

import { useEffect } from 'react';
import { initTelemetry } from '@/lib/telemetry';

export function TelemetryProvider({
  connectionString,
  logSensitiveContent,
  children,
}: {
  connectionString: string;
  logSensitiveContent: boolean;
  children: React.ReactNode;
}) {
  useEffect(() => {
    initTelemetry(connectionString, logSensitiveContent);
  }, [connectionString, logSensitiveContent]);

  return <>{children}</>;
}
```

- [ ] **Step 4: Wire the provider into the root server layout**

In `src/web/tectika-board/src/app/layout.tsx`, import the provider and read the runtime env in the (server) component, wrapping the existing tree:

```tsx
import { TelemetryProvider } from '@/components/observability/TelemetryProvider';
```

Inside `RootLayout`, before the `return`, read env (server-side, runtime):

```tsx
  const aiConn = process.env.APPLICATIONINSIGHTS_CONNECTION_STRING ?? '';
  const logSensitive = (process.env.Logging__LogSensitiveContent ?? 'true') !== 'false';
```

Wrap the existing `<SettingsProvider>...</SettingsProvider>` subtree with the provider:

```tsx
        <TelemetryProvider connectionString={aiConn} logSensitiveContent={logSensitive}>
          <SettingsProvider>
            {/* ...existing Navbar/Sidebar/main/Toaster/CommandPalette... */}
          </SettingsProvider>
        </TelemetryProvider>
```

- [ ] **Step 5: Build the web app to verify**

Run: `cd src/web/tectika-board && npm run build`
Expected: build completes with no type errors. (The `output: 'standalone'` build should succeed.)

- [ ] **Step 6: Commit**

```bash
git add src/web/tectika-board/package.json src/web/tectika-board/package-lock.json \
        src/web/tectika-board/src/lib/telemetry.ts \
        src/web/tectika-board/src/components/observability/TelemetryProvider.tsx \
        src/web/tectika-board/src/app/layout.tsx
git commit -m "feat(web): add App Insights browser SDK with runtime-injected TelemetryProvider"
```

---

## Task 10: Web — instrument the API fetch client + key user actions

**Files:**
- Modify: `src/web/tectika-board/src/lib/api.ts`

- [ ] **Step 1: Add logging to the `fetchApi` wrapper**

In `src/web/tectika-board/src/lib/api.ts`, import the helpers at the top:

```ts
import { trackEvent, trackException, redact } from './telemetry';
```

Wrap the body of `fetchApi` to log request, success, and failure (the App Insights SDK already auto-tracks the fetch *dependency*; these add business-level `[Topic]` traces):

```ts
async function fetchApi<T>(path: string, options?: RequestInit): Promise<T> {
  const method = options?.method ?? 'GET';
  trackEvent('[ApiRequest]', { method, path, body: redact(options?.body as string | undefined) });
  try {
    const res = await fetch(`${API_BASE}${path}`, {
      ...options,
      headers: { 'Content-Type': 'application/json', ...options?.headers },
    });
    if (!res.ok) {
      const text = await res.text();
      trackEvent('[ApiError]', { method, path, status: res.status, body: redact(text) });
      throw new ApiError(res.status, `API ${res.status}: ${text}`);
    }
    if (res.status === 204) return undefined as T;
    const text = await res.text();
    return (text ? JSON.parse(text) : undefined) as T;
  } catch (err) {
    if (!(err instanceof ApiError)) trackException(err, { method, path });
    throw err;
  }
}
```

- [ ] **Step 2: Add custom events at key user actions**

Add a `trackEvent` at the highest-value user actions that flow through `api.ts`. These are already centralized in the `api` object, so a single `[ApiRequest]`/`[ApiError]` per call (Step 1) covers run start, approvals, agent upserts, edge edits, etc. No further per-component changes are required for MVP. (Optional: add `trackEvent('[RunStartClick]', ...)` in the component that triggers a run if finer funnel data is wanted — out of scope here.)

- [ ] **Step 3: Build to verify**

Run: `cd src/web/tectika-board && npm run build`
Expected: build completes with no type errors.

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/lib/api.ts
git commit -m "feat(web): instrument API fetch client with [Topic] telemetry events"
```

---

## Task 11: Full verification + integration smoke + handoff

**Files:** none (verification only)

- [ ] **Step 1: Build the entire solution**

Run: `dotnet build TectikaAgents.slnx`
Expected: Build succeeded, 0 errors across all projects.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`
Expected: All tests PASS (including the new `SensitiveContentTests` and `LoggingSettingsTests`).

- [ ] **Step 3: Build the web app**

Run: `cd src/web/tectika-board && npm run build`
Expected: build completes, no type errors.

- [ ] **Step 4: Validate Bicep**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo OK`
Expected: `OK`. (Skip with a note if `az` is unavailable.)

- [ ] **Step 5: Manual integration smoke (requires a real connection string)**

Following the `running-agentboard-for-visual-qa` memory, run API + workflows + web with a real `APPLICATIONINSIGHTS_CONNECTION_STRING` for `ai-agentteam`. Trigger one run end-to-end (start a task → agent invocation → approval). In the Azure portal → `ai-agentteam` → **Logs**, confirm:
  - `traces` contains `[Topic]`-prefixed messages from API, workflows, and `FoundryAgentRuntime`.
  - `requests` and `dependencies` tables are populated (API requests, outgoing HttpClient, browser fetch).
  - The web app emits page views + `[ApiRequest]` custom events (browser → `ai-agentteam`).
  - A single run correlates end-to-end via `operation_Id` across web → API → Functions.

- [ ] **Step 6: Verify the redaction switch**

Set `Logging__LogSensitiveContent=false` for the API (env var) and restart. Re-run a request with a body and confirm the `[HttpRequest]` trace shows `[redacted](N chars)` instead of the body, while metadata logs remain.

- [ ] **Step 7: Final commit (if any verification fixes were needed)**

```bash
git add -A
git commit -m "chore: verification fixes for detailed logging"
```

---

## Notes for the implementer

- **Enrichment placement:** For controllers/services/activities, open each file and place logs at method entry, each meaningful branch/outcome, and catch blocks. The examples above show the exact `[Topic]` and field shape — match property names to what actually exists in each file.
- **`[Topic]` rule:** the bracket tag is always static text at the very start of the template; structured `{Fields}` follow it. Never put a `{Placeholder}` inside the brackets.
- **Sensitive content:** any raw body/prompt/output goes through `SensitiveContent.Format(value, _logSensitive)` (.NET) or `redact(value)` (web). Never log raw sensitive content directly.
- **Durable orchestrator:** only `context.CreateReplaySafeLogger<T>()` — never inject a plain `ILogger` into the orchestrator, or logs duplicate on replay.
- **No new resources / no deploy changes:** the only infra edits are additive env vars; CI build and `deploy.ps1` are untouched.
```
