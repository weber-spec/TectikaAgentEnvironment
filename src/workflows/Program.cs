using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using Azure.Monitor.OpenTelemetry.Exporter;
using TectikaAgents.AgentRuntime;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.AgentRuntime.Workspace;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Workflows.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Services.Configure<CosmosDbSettings>(builder.Configuration.GetSection("CosmosDb"));
builder.Services.Configure<ServiceBusSettings>(builder.Configuration.GetSection("ServiceBus"));
builder.Services.Configure<FoundrySettings>(builder.Configuration.GetSection("Foundry"));
builder.Services.Configure<LoggingSettings>(builder.Configuration.GetSection("Logging"));

// ── Cosmos DB ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(sp =>
{
    // Match the API: models are annotated for System.Text.Json (camelCase, [JsonPropertyName("id")]),
    // so the Cosmos SDK must serialize with System.Text.Json (not its default Newtonsoft) or it writes
    // "Id"/"TenantId" and breaks the required `id` field and the camelCase SQL queries.
    var cosmosJson = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };
    cosmosJson.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    var cosmosOptions = new CosmosClientOptions { UseSystemTextJsonSerializerWithOptions = cosmosJson };

    var endpoint = builder.Configuration["CosmosDb:AccountEndpoint"] ?? string.Empty;
    if (!string.IsNullOrEmpty(endpoint) && !endpoint.StartsWith("__", StringComparison.Ordinal))
        return new CosmosClient(endpoint, new DefaultAzureCredential(), cosmosOptions);

    // Local Cosmos Emulator for dev
    var connStr = builder.Configuration["CosmosDb:ConnectionString"]
        ?? "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2NP9SdVmlDkFNfKKhVvFkTTa25aAWmIBQJrYVTbVA==";
    return new CosmosClient(connStr, cosmosOptions);
});

builder.Services.AddSingleton<WorkflowCosmosService>();

// ── Service Bus publisher ─────────────────────────────────────────────────────
builder.Services.AddSingleton(sp =>
{
    var ns = builder.Configuration["ServiceBus:Namespace"] ?? string.Empty;
    if (string.IsNullOrEmpty(ns) || ns.StartsWith("__", StringComparison.Ordinal))
        return null!; // dev mode: no Service Bus
    return new ServiceBusClient(ns, new DefaultAzureCredential());
});

builder.Services.AddSingleton<WorkflowEventPublisher>();

// ── Secret Provider + GitHub ──────────────────────────────────────────────────
var keyVaultUri = builder.Configuration["KeyVault:VaultUri"];
var useMockDatabase = builder.Configuration.GetValue<bool>("MockDatabase:Enabled");
if (useMockDatabase || string.IsNullOrEmpty(keyVaultUri))
    builder.Services.AddSingleton<ISecretProvider, ConfigSecretProvider>();
else
    builder.Services.AddSingleton<ISecretProvider, KeyVaultSecretProvider>();

builder.Services.AddSingleton<IGitHubToolExecutor, OctokitGitHubToolExecutor>();
builder.Services.AddSingleton<IWorkspaceService, WorkspaceService>();
builder.Services.AddSingleton<WorkspaceToolExecutor>();

// ── Agent runtime (Foundry or Mock) ──────────────────────────────────────────
var useMockAgents = builder.Configuration.GetValue<bool>("Foundry:UseMock", useMockDatabase);
// Transient: Durable Functions resolves activities from the root scope, and the activity mutates
// FoundryAgentRuntime.OnText per call — a shared instance would race across concurrent activities.
if (useMockAgents)
{
    builder.Services.AddTransient<IAgentRuntime, MockAgentRuntime>();
    builder.Services.AddTransient<IAgentProvisioner, MockAgentProvisioner>();
}
else
{
    builder.Services.AddTransient<IAgentRuntime, FoundryAgentRuntime>();
    builder.Services.AddTransient<IAgentProvisioner, FoundryAgentRuntime>();
}

builder.Services.AddScoped<ContextManager>();
builder.Services.AddHttpClient();

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
var aiConnStr = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
if (!string.IsNullOrEmpty(aiConnStr))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

builder.Build().Run();
