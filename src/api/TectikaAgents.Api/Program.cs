using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Azure.Cosmos;
using Microsoft.Identity.Web;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Api.Auth;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.AgentRuntime;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
builder.Services.Configure<CosmosDbSettings>(builder.Configuration.GetSection("CosmosDb"));
builder.Services.Configure<AzureAdSettings>(builder.Configuration.GetSection("AzureAd"));
builder.Services.Configure<FoundrySettings>(builder.Configuration.GetSection("Foundry"));
builder.Services.Configure<ServiceBusSettings>(builder.Configuration.GetSection("ServiceBus"));
builder.Services.Configure<KeyVaultSettings>(builder.Configuration.GetSection("KeyVault"));

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

// ── Foundry / Agent provisioning ─────────────────────────────────────────────
// "Foundry:UseMock" selects mock vs real Foundry provisioning; defaults to the DB flag.
var useMockAgents = builder.Configuration.GetValue<bool>("Foundry:UseMock", useMockDatabase);
if (useMockAgents)
    builder.Services.AddSingleton<IAgentProvisioner, MockAgentProvisioner>();
else
    builder.Services.AddSingleton<IAgentProvisioner, FoundryAgentRuntime>();

// ── SSE + Service Bus ────────────────────────────────────────────────────────
builder.Services.AddSingleton<SseConnectionManager>();
builder.Services.AddHostedService<ServiceBusListenerService>();

// ── CLI Bridge WebSocket ─────────────────────────────────────────────────────
builder.Services.AddSingleton<CliBridgeManager>();

// ── HttpClientFactory (used by RunsController to call Durable Functions) ──────
builder.Services.AddHttpClient();

// ── Run orchestration service ────────────────────────────────────────────────
builder.Services.AddScoped<IRunStartService, RunStartService>();

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
app.UseAuthorization();
app.MapControllers();

app.Run();
