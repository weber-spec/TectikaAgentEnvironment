using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Azure.Cosmos;
using Microsoft.Identity.Web;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Api.Auth;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
builder.Services.Configure<CosmosDbSettings>(builder.Configuration.GetSection("CosmosDb"));
builder.Services.Configure<AzureAdSettings>(builder.Configuration.GetSection("AzureAd"));
builder.Services.Configure<FoundrySettings>(builder.Configuration.GetSection("Foundry"));
builder.Services.Configure<ServiceBusSettings>(builder.Configuration.GetSection("ServiceBus"));
builder.Services.Configure<KeyVaultSettings>(builder.Configuration.GetSection("KeyVault"));

// ── Mock mode toggle ─────────────────────────────────────────────────────────
// When "MockDatabase:Enabled" is true the API serves an in-memory mock DB and accepts
// anonymous dev requests — for FE development before an Azure account exists. Flip it off
// (or remove it) to restore the real Cosmos DB + Microsoft Identity auth path unchanged.
var useMockDatabase = builder.Configuration.GetValue<bool>("MockDatabase:Enabled");

// ── Auth — Entra (placeholder: fill TenantId + ClientId before deployment) ──
if (useMockDatabase)
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
        // בפרודקשן: DefaultAzureCredential (MSI). בפיתוח: connection string מ-appsettings.
        if (!string.IsNullOrEmpty(settings.AccountEndpoint) && settings.AccountEndpoint != "__COSMOS_ENDPOINT__")
            return new CosmosClient(settings.AccountEndpoint, new DefaultAzureCredential());
        // Dev fallback — connection string
        var connStr = builder.Configuration["CosmosDb:ConnectionString"] ?? "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2NP9SdVmlDkFNfKKhVvFkTTa25aAWmIBQJrYVTbVA==";
        return new CosmosClient(connStr);
    });

    builder.Services.AddSingleton<ICosmosDbService, CosmosDbService>();
}

// ── Identity Service (App Registration — MVP) ────────────────────────────────
builder.Services.AddSingleton<IAgentIdentityService, AppRegistrationIdentityService>();

// ── Foundry / Agent execution ────────────────────────────────────────────────
builder.Services.AddHttpClient<FoundryAgentService>();

// ── SSE + Service Bus ────────────────────────────────────────────────────────
builder.Services.AddSingleton<SseConnectionManager>();
builder.Services.AddHostedService<ServiceBusListenerService>();

// ── CLI Bridge WebSocket ─────────────────────────────────────────────────────
builder.Services.AddSingleton<CliBridgeManager>();

// ── HttpClientFactory (used by RunsController to call Durable Functions) ──────
builder.Services.AddHttpClient();

// ── Controllers + OpenAPI ────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// ── CORS (Next.js dev server) ─────────────────────────────────────────────────
builder.Services.AddCors(o => o.AddPolicy("NextJs", p =>
    p.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// ── Ensure data layer is ready (real: create Cosmos containers; mock: no-op) ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ICosmosDbService>();
    await db.EnsureInfrastructureAsync();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors("NextJs");
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
