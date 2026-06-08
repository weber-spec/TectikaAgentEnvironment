using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using Azure.Monitor.OpenTelemetry.Exporter;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Workflows.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Services.Configure<CosmosDbSettings>(builder.Configuration.GetSection("CosmosDb"));
builder.Services.Configure<ServiceBusSettings>(builder.Configuration.GetSection("ServiceBus"));
builder.Services.Configure<FoundrySettings>(builder.Configuration.GetSection("Foundry"));

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

// ── Foundry Agent runner (via HTTP to Azure OpenAI) ──────────────────────────
builder.Services.AddHttpClient<WorkflowAgentRunner>();

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
var aiConnStr = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
if (!string.IsNullOrEmpty(aiConnStr))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

builder.Build().Run();
