using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Identity.Web;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
builder.Services.Configure<CosmosDbSettings>(builder.Configuration.GetSection("CosmosDb"));
builder.Services.Configure<AzureAdSettings>(builder.Configuration.GetSection("AzureAd"));
builder.Services.Configure<FoundrySettings>(builder.Configuration.GetSection("Foundry"));
builder.Services.Configure<ServiceBusSettings>(builder.Configuration.GetSection("ServiceBus"));
builder.Services.Configure<KeyVaultSettings>(builder.Configuration.GetSection("KeyVault"));

// ── Auth — Entra (placeholder: fill TenantId + ClientId before deployment) ──
builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration);
builder.Services.AddAuthorization();

// ── Cosmos DB ────────────────────────────────────────────────────────────────
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

builder.Services.AddSingleton<CosmosDbService>();

// ── Identity Service (App Registration — MVP) ────────────────────────────────
builder.Services.AddSingleton<IAgentIdentityService, AppRegistrationIdentityService>();

// ── Foundry / Agent execution ────────────────────────────────────────────────
builder.Services.AddHttpClient<FoundryAgentService>();

// ── SSE + Service Bus ────────────────────────────────────────────────────────
builder.Services.AddSingleton<SseConnectionManager>();
builder.Services.AddHostedService<ServiceBusListenerService>();

// ── CLI Bridge WebSocket ─────────────────────────────────────────────────────
builder.Services.AddSingleton<CliBridgeManager>();

// ── Controllers + OpenAPI ────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// ── CORS (Next.js dev server) ─────────────────────────────────────────────────
builder.Services.AddCors(o => o.AddPolicy("NextJs", p =>
    p.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// ── Cosmos DB: ensure database + containers exist ────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var cosmos = scope.ServiceProvider.GetRequiredService<CosmosDbService>();
    await cosmos.EnsureInfrastructureAsync();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors("NextJs");
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
