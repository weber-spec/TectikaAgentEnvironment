using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Azure.Cosmos;
using Microsoft.Identity.Web;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Api.Auth;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.AgentRuntime;

var builder = WebApplication.CreateBuilder(args);

// ── Observability (Azure Monitor / OpenTelemetry) ────────────────────────────
// Guarded on the connection string so local dev (no App Insights) is unaffected.
// Routes all ILogger output to App Insights and auto-captures incoming requests,
// outgoing HttpClient dependencies, and exceptions. Sampling left at full capture
// for now (see spec — primary cost knob).
var aiConnStr = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrEmpty(aiConnStr))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor(o =>
    {
        o.ConnectionString = aiConnStr;
        o.SamplingRatio = 1.0f; // capture everything for now
    });
}

// ── Configuration ────────────────────────────────────────────────────────────
builder.Services.Configure<CosmosDbSettings>(builder.Configuration.GetSection("CosmosDb"));
builder.Services.Configure<AzureAdSettings>(builder.Configuration.GetSection("AzureAd"));
builder.Services.Configure<FoundrySettings>(builder.Configuration.GetSection("Foundry"));
builder.Services.Configure<ServiceBusSettings>(builder.Configuration.GetSection("ServiceBus"));
builder.Services.Configure<DurableFunctionsSettings>(builder.Configuration.GetSection("DurableFunctions"));
builder.Services.Configure<KeyVaultSettings>(builder.Configuration.GetSection("KeyVault"));
builder.Services.Configure<LoggingSettings>(builder.Configuration.GetSection("Logging"));

// ── Toggles (independent) ────────────────────────────────────────────────────
// "MockDatabase:Enabled" selects the DB backend: in-memory mock vs real Cosmos DB.
// "DevAuth:Enabled"       selects the auth mode: anonymous dev handler vs Microsoft Identity (Entra).
// They are decoupled so we can run the REAL Cosmos DB while still accepting anonymous dev requests
// (the FE keeps working unchanged). DevAuth defaults to the DB flag to preserve prior behavior.
var useMockDatabase = builder.Configuration.GetValue<bool>("MockDatabase:Enabled");
var useDevAuth = builder.Configuration.GetValue<bool>("DevAuth:Enabled", useMockDatabase);

// ── Auth — dev handler or Entra (placeholder: fill TenantId + ClientId before deployment) ──
if (useDevAuth)
    builder.Services.AddAuthentication(MockAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, MockAuthHandler>(MockAuthHandler.SchemeName, _ => { });
else
    builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration);
builder.Services.AddAuthorization();

// ── Cosmos DB (real) or in-memory mock ───────────────────────────────────────
if (useMockDatabase)
{
    builder.Services.AddSingleton<ICosmosDbService, InMemoryCosmosDbService>();
}
else
{
    builder.Services.AddSingleton(sp =>
    {
        var settings = builder.Configuration.GetSection("CosmosDb").Get<CosmosDbSettings>()!;
        // The models are annotated for System.Text.Json ([JsonPropertyName("id")], camelCase),
        // but the Cosmos SDK defaults to Newtonsoft — which would write "Id"/"TenantId" and break
        // both the required lowercase `id` and the camelCase SQL queries. Use System.Text.Json with
        // the same string-enum convention the controllers use (so e.g. status is stored as "Pending").
        var cosmosJson = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };
        cosmosJson.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var cosmosOptions = new CosmosClientOptions { UseSystemTextJsonSerializerWithOptions = cosmosJson };

        // בפרודקשן: DefaultAzureCredential (MSI). בפיתוח: connection string מ-appsettings.
        if (!string.IsNullOrEmpty(settings.AccountEndpoint) && settings.AccountEndpoint != "__COSMOS_ENDPOINT__")
            return new CosmosClient(settings.AccountEndpoint, new DefaultAzureCredential(), cosmosOptions);
        // Dev fallback — connection string
        var connStr = builder.Configuration["CosmosDb:ConnectionString"] ?? "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2NP9SdVmlDkFNfKKhVvFkTTa25aAWmIBQJrYVTbVA==";
        return new CosmosClient(connStr, cosmosOptions);
    });

    builder.Services.AddSingleton<ICosmosDbService, CosmosDbService>();
}

// ── Identity Service (App Registration — MVP) ────────────────────────────────
builder.Services.AddSingleton<IAgentIdentityService, AppRegistrationIdentityService>();

// ── Secret Provider ────────────────────────────────────────────────────────
var keyVaultUri = builder.Configuration["KeyVault:VaultUri"];
if (useMockDatabase || string.IsNullOrEmpty(keyVaultUri))
    builder.Services.AddSingleton<ISecretProvider, ConfigSecretProvider>();
else
    builder.Services.AddSingleton<ISecretProvider, KeyVaultSecretProvider>();

// ── GitHub Tool Executor ──────────────────────────────────────────────────
builder.Services.AddSingleton<IGitHubToolExecutor, OctokitGitHubToolExecutor>();
builder.Services.AddSingleton<TectikaAgents.Core.Interfaces.IMcpGateway, TectikaAgents.AgentRuntime.Mcp.McpGateway>();
builder.Services.AddSingleton<TectikaAgents.AgentRuntime.Mcp.IFirstPartyConnector, TectikaAgents.AgentRuntime.Mcp.ResendEmailConnector>();
builder.Services.AddSingleton<TectikaAgents.AgentRuntime.Mcp.IFirstPartyConnector, TectikaAgents.AgentRuntime.Mcp.SlackConnector>();
builder.Services.AddSingleton<TectikaAgents.AgentRuntime.Mcp.IResendDomainsClient, TectikaAgents.AgentRuntime.Mcp.ResendDomainsClient>();
builder.Services.AddSingleton<TectikaAgents.AgentRuntime.Mcp.McpToolExecutor>();

// ── Board-tools MCP endpoint (for Claude Code agents) ────────────────────────
// A streamable-HTTP MCP server that re-exposes the board tools (same source of truth as the
// Foundry projection) to the `claude` CLI running in the workspace container. Needs real Cosmos
// (WorkflowCosmosService + BoardProjectExplorer), so it's wired only in the non-mock path. Handlers
// resolve the singleton BoardToolsMcp from the built provider (RequestContext exposes no DI); per-run
// identity comes from the McpRun bearer token via IHttpContextAccessor.
IServiceProvider? mcpSp = null;
if (!useMockDatabase)
{
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<TectikaAgents.Workflows.Services.WorkflowCosmosService>();
    builder.Services.AddSingleton<TectikaAgents.Api.Mcp.BoardToolsMcp>();
    builder.Services.AddAuthentication()
        .AddScheme<AuthenticationSchemeOptions, TectikaAgents.Api.Mcp.McpRunAuthHandler>(
            TectikaAgents.Api.Mcp.McpRunAuthHandler.SchemeName, null);
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithListToolsHandler(async (req, ct) => await mcpSp!.GetRequiredService<TectikaAgents.Api.Mcp.BoardToolsMcp>().ListToolsAsync(req, ct))
        .WithCallToolHandler(async (req, ct) => await mcpSp!.GetRequiredService<TectikaAgents.Api.Mcp.BoardToolsMcp>().CallToolAsync(req, ct));
}

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OctokitGitHubReadService>();
builder.Services.AddSingleton<IGitHubReadService>(sp =>
    new CachedGitHubReadService(
        sp.GetRequiredService<OctokitGitHubReadService>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

// ── Live Preview ────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new PreviewSettings
{
    IdleMinutes = builder.Configuration.GetValue("Preview:IdleMinutes", 15),
    CapMinutes  = builder.Configuration.GetValue("Preview:CapMinutes", 45),
});
builder.Services.AddSingleton(new TectikaAgents.AgentRuntime.Preview.AciPreviewOptions
{
    ResourceGroup  = builder.Configuration["Preview:ResourceGroup"] ?? "rg-agentteam-dev-001",
    Region         = builder.Configuration["Preview:Region"] ?? "westeurope",
    AcrImage       = builder.Configuration["Preview:AcrImage"] ?? "tacragentteam.azurecr.io/preview-runner:latest",
    AcrLoginServer = builder.Configuration["Preview:AcrLoginServer"] ?? "tacragentteam.azurecr.io",
    MiResourceId   = builder.Configuration["Preview:MiResourceId"],
});
builder.Services.AddSingleton<TectikaAgents.Core.Interfaces.IPreviewProvisioner, TectikaAgents.AgentRuntime.Preview.AciPreviewProvisioner>();
builder.Services.AddSingleton<Func<DateTimeOffset>>(_ => () => DateTimeOffset.UtcNow);
builder.Services.AddSingleton<IPreviewService, PreviewService>();
if (!useMockDatabase)
    builder.Services.AddHostedService<PreviewIdleReaperService>();

// ── Foundry / Agent provisioning ─────────────────────────────────────────────
// "Foundry:UseMock" selects mock vs real Foundry provisioning; defaults to the DB flag.
var useMockAgents = builder.Configuration.GetValue<bool>("Foundry:UseMock", useMockDatabase);
if (useMockAgents)
{
    builder.Services.AddSingleton<IAgentProvisioner, MockAgentProvisioner>();
    builder.Services.AddSingleton<IModelCatalog, MockModelCatalog>();
}
else
{
    builder.Services.AddSingleton<IAgentProvisioner, FoundryAgentRuntime>();
    builder.Services.AddSingleton<IModelCatalog, FoundryModelCatalog>();
}

// Foundry project catalog (connections + deployments) for the Connections → Foundry tab. Degrades to empty
// when Foundry isn't reachable, so it's safe to register in both mock and real modes.
builder.Services.AddSingleton<TectikaAgents.AgentRuntime.FoundryConnectionsCatalog>();

// Live Claude model catalog for the Claude model picker (Anthropic /v1/models per connection). Degrades to a
// curated fallback on OAuth connections or any failure, so it's safe to register in both mock and real modes.
builder.Services.AddSingleton<IClaudeModelCatalog, TectikaAgents.AgentRuntime.ClaudeModelCatalog>();

// ── SSE + Service Bus ────────────────────────────────────────────────────────
builder.Services.AddSingleton<SseConnectionManager>();
builder.Services.AddHostedService<ServiceBusListenerService>();

// ── Notifications ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton<NotificationConnectionManager>();
if (!useMockDatabase)
{
    builder.Services.AddSingleton<NotificationRepository>();
    builder.Services.AddSingleton<UserSettingsRepository>();
}
else
{
    builder.Services.AddSingleton<NotificationRepository, InMemoryNotificationRepository>();
    builder.Services.AddSingleton<UserSettingsRepository, InMemoryUserSettingsRepository>();
}

// ── CLI Bridge WebSocket ─────────────────────────────────────────────────────
builder.Services.AddSingleton<CliBridgeManager>();

// ── Workspace Service (board-level ACI containers) ───────────────────────────
builder.Services.AddSingleton<TectikaAgents.Core.Interfaces.IWorkspaceService, TectikaAgents.Workflows.Services.WorkspaceService>();

// ── Workspace snapshot store (blob in prod; in-memory in mock mode) ───────────
if (useMockDatabase)
    builder.Services.AddSingleton<TectikaAgents.Workflows.Services.IWorkspaceSnapshotStore, TectikaAgents.Api.Services.InMemoryWorkspaceSnapshotStore>();
else
    builder.Services.AddSingleton<TectikaAgents.Workflows.Services.IWorkspaceSnapshotStore, TectikaAgents.Workflows.Services.BlobWorkspaceSnapshotStore>();

// ── Board maintenance (reset/clone) + workspace control ──────────────────────
builder.Services.AddScoped<TectikaAgents.Api.Services.IBoardMaintenanceService, TectikaAgents.Api.Services.BoardMaintenanceService>();
builder.Services.AddScoped<TectikaAgents.Api.Services.IWorkspaceControlService, TectikaAgents.Api.Services.WorkspaceControlService>();

// ── HttpClientFactory (used by RunsController to call Durable Functions) ──────
builder.Services.AddHttpClient();

// ── Run orchestration service ────────────────────────────────────────────────
builder.Services.AddScoped<IRunStartService, RunStartService>();
builder.Services.AddScoped<IChatService, ChatService>();

// ── Pricing / Cost Calculator ────────────────────────────────────────────────
builder.Services.AddSingleton(_ => new TectikaAgents.Core.Usage.CostCalculator(
    TectikaAgents.Core.Usage.PricingCatalogLoader.LoadEmbedded()));

// ── Usage Backfill (one-time admin migration) ────────────────────────────────
builder.Services.AddTransient<TectikaAgents.Api.Services.UsageBackfill>();

// ── Controllers + OpenAPI ────────────────────────────────────────────────────
// Serialize enums as their string names (e.g. "InProgress") so the Next.js client's
// string-union types line up with the API contract instead of receiving raw integers.
builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// ── CORS (Next.js dev server) ─────────────────────────────────────────────────
// In Development, reflect any origin so the app is reachable over the LAN / WSL2
// VM IP (not just localhost). Production keeps the strict allow-list.
builder.Services.AddCors(o => o.AddPolicy("NextJs", p =>
{
    if (builder.Environment.IsDevelopment())
        p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    else
        p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

var app = builder.Build();
mcpSp = app.Services;   // MCP tool handlers resolve BoardToolsMcp (singleton) from the built provider

// ── Ensure data layer is ready (real: create Cosmos containers; mock: no-op) ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ICosmosDbService>();
    try
    {
        await db.EnsureInfrastructureAsync();

        // Seed the demo dataset into real Cosmos (idempotent — skips if already populated).
        // Mock mode self-seeds in-memory, so only do this on the real DB path.
        if (!useMockDatabase && builder.Configuration.GetValue<bool>("CosmosDb:SeedData"))
        {
            var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("CosmosDataSeeder");
            await TectikaAgents.Api.Services.MockData.CosmosDataSeeder.SeedAsync(db, seedLogger);
        }
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "EnsureInfrastructure/seed failed — containers may already exist, continuing startup.");
    }
}

// One-shot seed mode: provision + seed above, then exit without starting the web server.
if (args.Contains("--seed-only"))
    return;

// One-time backfill: read old downstreamTaskIds from task docs and create TaskEdge documents.
// Real-DB only — the mock store is already seeded with edges by MockDataSeeder.
if (!useMockDatabase && args.Contains("--backfill-edges"))
{
    using var scope = app.Services.CreateScope();
    var cosmos = scope.ServiceProvider.GetRequiredService<ICosmosDbService>();
    var client = scope.ServiceProvider.GetRequiredService<CosmosClient>();
    var dbName = builder.Configuration["CosmosDb:DatabaseName"] ?? "tectikaagents";
    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("EdgeBackfill");
    await TectikaAgents.Api.Services.MockData.EdgeBackfill.RunAsync(client, dbName, cosmos, log);
    return;
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors("NextJs");
app.UseWebSockets();
app.UseAuthentication();
app.UseMiddleware<TectikaAgents.Api.Middleware.RequestLoggingMiddleware>();
app.UseAuthorization();
app.MapControllers();

// Board-tools MCP endpoint — authenticated by the per-run McpRun bearer token (Claude Code only).
if (!useMockDatabase)
    app.MapMcp("/mcp").RequireAuthorization(
        new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
            TectikaAgents.Api.Mcp.McpRunAuthHandler.SchemeName).RequireAuthenticatedUser().Build());

app.Run();
