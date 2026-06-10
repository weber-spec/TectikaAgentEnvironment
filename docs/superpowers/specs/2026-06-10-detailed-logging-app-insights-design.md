# Detailed Logging → `ai-agentteam` Application Insights — Design

Date: 2026-06-10
Status: Approved (pending spec review)

## Goal

Add full, detailed, structured logging across the whole stack — **web** (Next.js),
**API** (ASP.NET, net10), and **workflows** (Azure Functions, net9) — all shipping to the
existing dedicated Application Insights resource **`ai-agentteam`**.

This is **wire-up + enrich**: install/configure the telemetry SDKs everywhere AND add
explicit structured log statements at the meaningful points in the code (not just rely on
auto-instrumentation).

## Current state (verified)

- `ai-agentteam` (workspace-based App Insights) is **already provisioned** in
  [`infra/modules/observability.bicep`](../../../infra/modules/observability.bicep); its
  connection string is output as `appInsightsConnectionString`.
- `APPLICATIONINSIGHTS_CONNECTION_STRING` is already injected into the **API** and
  **Functions** apps (`containerapps.bicep`, `functionapp.bicep`). It is **not** yet on the
  **web** container app (only `NODE_ENV` is set there).
- **Workflows** already wires OpenTelemetry → Azure Monitor in
  [`src/workflows/Program.cs`](../../../src/workflows/Program.cs)
  (`UseFunctionsWorkerDefaults().UseAzureMonitorExporter()`), guarded on the conn string.
- **API** uses `ILogger` via DI throughout, but **no** App Insights / OTel SDK is wired, so
  those logs do not reach Azure today.
- **Web** has **zero** telemetry and **zero** `console.*` calls.
- `FoundryAgentRuntime` (in the shared `agentruntime` project, multi-targets net9.0;net10.0,
  used by **both** API and workflows) **already has `ILogger<FoundryAgentRuntime>` injected**.

## Decisions (from brainstorming)

1. **Depth:** Wire-up + enrich (structured log statements at key points), not just SDK wiring.
2. **Web scope:** Full browser telemetry (App Insights JS): page views/route changes,
   fetch/XHR dependency correlation to the API, unhandled JS exceptions, user/session context.
3. **Sensitive data:** Log **everything now** (full request/response bodies, agent prompts and
   model outputs), behind a single toggle that can be flipped off for production later.
4. **.NET stack:** OpenTelemetry / Azure Monitor (consistent with workflows).

## Cross-cutting design

### Single redaction switch (default = log everything)

- A single config flag gates **every** sensitive log site (request/response bodies, agent
  prompt text, model output text).
- .NET: `Logging:LogSensitiveContent` (bool, **default `true`**). Read via
  `IConfiguration`/`IOptions`. When `false`, sensitive sites log only metadata (IDs, sizes,
  durations, status, token counts) — never raw content.
- Web: the same intent — the server passes a `logSensitiveContent` boolean to the client
  telemetry provider (default `true`).
- Production hardening later = flip the flag to `false` via env var; **no code change**.
- Env var name for parity across services: `Logging__LogSensitiveContent`.

### Message format convention (`[Topic]` prefix)

Every enrichment log message we add starts with a `[Topic]` tag in square brackets
identifying the file/process/topic it belongs to, so messages are easy to scan and filter in
App Insights. Examples (text is illustrative only):

- `[FoundryAgentInvoke] invoking agent {AgentId} with model {Model}`
- `[RunStart] run {RunId} created for task {TaskId}`
- `[QaLoop] iteration {Iteration}/{MaxIterations} for task {TaskId}`
- `[ServiceBusDispatch] published {EventType} for run {RunId}`

Rules:
- The tag goes at the very start of the message template, before any structured fields.
- The tag is a short PascalCase topic, not the raw class name necessarily — it names the
  operation/topic (e.g. `[FoundryAgentInvoke]`, `[CosmosWrite]`, `[ApprovalDecision]`).
- Structured fields still use `{Placeholder}` templates after the tag (the tag itself is
  static text, not a parameter).
- Applies uniformly to .NET (`ILogger`) and web (`trackTrace`/`trackEvent`) enrichment logs.

### Log levels

- `TectikaAgents.*` namespaces → `Debug`/`Information`.
- `Microsoft.*` / `System.*` framework noise → `Warning`.
- Set in `appsettings.json` (API), `host.json`/config (workflows).

### Sampling

- **Disabled** for now (full capture for debugging). Documented as the primary cost knob to
  revisit before production.

### Correlation

- OTel W3C trace-context propagates web → API → Functions automatically (browser fetch
  correlation headers + ASP.NET instrumentation + Functions worker instrumentation), so one
  user action stitches into a single end-to-end trace in App Insights.

## API (`TectikaAgents.Api`, net10)

### Wire-up

- Add package `Azure.Monitor.OpenTelemetry.AspNetCore`.
- In `Program.cs`, guarded on `APPLICATIONINSIGHTS_CONNECTION_STRING` being present:
  `builder.Services.AddOpenTelemetry().UseAzureMonitor(...)`. This routes all `ILogger`
  output to App Insights and auto-captures incoming requests, outgoing HttpClient
  dependencies, and exceptions.
- Disable adaptive sampling in the Azure Monitor options for now.
- Configure logging levels in `appsettings.json`.

### Enrich (structured logs at key points)

Add `ILogger` usage (where missing) and structured log statements to:

- **Controllers:** `RunsController`, `ApprovalsController`, `InteractionsController` — log
  action entry with route/IDs/params, the outcome (created/accepted/rejected), and warnings
  on validation/not-found. Request/response bodies behind the flag.
- **Services:** `RunStartService`, `ServiceBusListenerService`, `CliBridgeManager`,
  `SseConnectionManager`, `CosmosDbService`, `AppRegistrationIdentityService` — log key
  operations, IDs, durations, retries, and errors.
- Use structured message templates (`_logger.LogInformation("Run {RunId} started for task
  {TaskId}", ...)`) so fields are queryable in App Insights.

### Optional request/body logging middleware

- A lightweight middleware logging method, path, status, duration, and authenticated user.
- Request/response **bodies** logged only when `LogSensitiveContent` is `true`.

## Workflows (`TectikaAgents.Workflows`, net9)

Already exports to App Insights — **enrich only**:

- **Orchestrators:** pipeline start/finish, each stage transition, fan-out/fan-in decisions,
  QA-loop iterations, terminal status.
- **Activities:** entry with inputs (IDs), agent calls, results, durations, exceptions.
- **Triggers:** message received, dedup/idempotency decisions, dispatch.
- **Services:** `ContextManager`, `WorkflowCosmosService`, `WorkflowEventPublisher` — Cosmos
  reads/writes, event publishes, errors.
- Sensitive content (context payloads, agent prompts/outputs) behind the flag.

## Shared AgentRuntime (`FoundryAgentRuntime`)

Instrumented **once** here (already has `ILogger` injected); benefits both API and workflows:

- Log agent/run lifecycle: agent id, model, run created/started/completed/failed, polling,
  token usage and durations where available.
- Full prompt text + model output text behind the `LogSensitiveContent` flag.
- The flag is read from `FoundrySettings`/config already available to the runtime (add a
  `LogSensitiveContent` option resolved from `Logging:LogSensitiveContent`, plumbed from each
  host's configuration).

## Web (`tectika-board`, Next.js 16 / React 19)

### Packages

- `@microsoft/applicationinsights-web`
- `@microsoft/applicationinsights-react-js`

### Runtime connection-string injection (build-once / deploy-anywhere)

- **No `NEXT_PUBLIC_` build-time inlining.** The root **server** layout (`app/layout.tsx`)
  reads `process.env.APPLICATIONINSIGHTS_CONNECTION_STRING` and
  `process.env.Logging__LogSensitiveContent` at runtime and passes them as props to a
  **client** `<TelemetryProvider>`.
- `<TelemetryProvider>` initializes App Insights with: auto page-view + route-change
  tracking, fetch/XHR dependency correlation to the API
  (`enableCorsCorrelation`, correlation header domains), unhandled exception capture, and
  user/session context bound to the MSAL account.
- If the connection string is absent (local dev), the provider no-ops cleanly.

### Telemetry helper + enrichment

- `src/lib/telemetry.ts` wrapper exposing `trackEvent`, `trackException`, `trackTrace`.
- Instrument key user actions (run start, approvals accept/reject, agent edits/saves) and the
  API fetch client (request issued, status, errors). Payload content behind the flag.

### Deployment impact

- **CI build: unchanged** (no new build args/secrets; image stays environment-agnostic).
- **Deploy: unchanged pipeline**, one Bicep edit (below).

## Infra (kept fully idempotent — per project rule)

- `infra/modules/containerapps.bicep`: add to the **web** container app's `env` list:
  - `{ name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }`
    (the param already exists at module scope).
  - `{ name: 'Logging__LogSensitiveContent', value: 'true' }`.
- Add `{ name: 'Logging__LogSensitiveContent', value: 'true' }` to the **API** app env
  (`containerapps.bicep`) and the **Functions** app settings (`functionapp.bicep`) for parity.
- No new resources; `ai-agentteam` and the connection-string output already exist.

## Out of scope

- Dashboards / alerts / saved KQL queries in App Insights (can be a follow-up).
- Changing retention (currently 30 days) or enabling sampling tuning.
- Distributed tracing of Cosmos internals beyond what the SDK emits.

## Testing / verification

- Build all .NET projects + `dotnet test`; `npm run build` for web.
- Local smoke: run API + workflows + web with a real connection string and confirm traces,
  requests, dependencies, and custom logs appear in `ai-agentteam` (Logs / Transaction
  search), correlated end-to-end across a single run.
- Confirm flipping `Logging__LogSensitiveContent=false` removes raw bodies/prompts/outputs
  while keeping metadata logs.
