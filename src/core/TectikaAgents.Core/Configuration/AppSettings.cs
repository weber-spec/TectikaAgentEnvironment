namespace TectikaAgents.Core.Configuration;

public class CosmosDbSettings
{
    public string AccountEndpoint { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = "tectikaagents";
}

public class AzureAdSettings
{
    public string TenantId { get; set; } = string.Empty;      // __TENANT_ID__
    public string ClientId { get; set; } = string.Empty;      // tectika-agents-identity app id
    public string Audience { get; set; } = string.Empty;      // api://<tectika-agents-id>
    public string PlatformClientId { get; set; } = string.Empty; // tectika-platform app id (frontend)
}

public class FoundrySettings
{
    public string Endpoint { get; set; } = string.Empty;   // https://<resource>.openai.azure.com  OR  https://api.openai.com/v1
    public string ProjectName { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = "gpt-4o";
    public string? ApiKey { get; set; }                    // אם מוגדר — משתמש ב-API key ולא ב-MSI
    public bool IsOpenAiDirect { get; set; }               // true = api.openai.com, false = Azure OpenAI
    public int MaxInputTokens { get; set; } = 100_000;
    public int SummaryThresholdTokens { get; set; } = 8_000;

    /// <summary>Foundry project (data-plane) endpoint for the Agent Service SDK:
    /// https://&lt;subdomain&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</summary>
    public string ProjectEndpoint { get; set; } = string.Empty;

    /// <summary>Per-turn output token cap passed to the Foundry run.</summary>
    public int MaxCompletionTokens { get; set; } = 4096;

    /// <summary>When true, use the no-Azure mock runtime/provisioner. Defaults to MockDatabase:Enabled in DI.</summary>
    public bool UseMock { get; set; }
}

public class DurableFunctionsSettings
{
    public string StartUrl { get; set; } = "http://localhost:7071/api/pipelines/start";
    public string? FunctionKey { get; set; }
    public string? ManagementKey { get; set; }
}

public class ServiceBusSettings
{
    public string Namespace { get; set; } = string.Empty;     // <namespace>.servicebus.windows.net
    public string AgentEventsTopic { get; set; } = "agent-events";
    public string AgentEventsSubscription { get; set; } = "api-sub";
    public string TaskTriggerQueue { get; set; } = "task-trigger";
    public string ApprovalsQueue { get; set; } = "approvals";
}

public class KeyVaultSettings
{
    public string VaultUri { get; set; } = string.Empty;      // https://<vault>.vault.azure.net/
}
