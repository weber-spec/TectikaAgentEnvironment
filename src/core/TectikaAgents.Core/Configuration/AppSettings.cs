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
}

public class ServiceBusSettings
{
    public string Namespace { get; set; } = string.Empty;     // <namespace>.servicebus.windows.net
    public string AgentEventsTopic { get; set; } = "agent-events";
    public string TaskTriggerQueue { get; set; } = "task-trigger";
    public string ApprovalsQueue { get; set; } = "approvals";
}

public class KeyVaultSettings
{
    public string VaultUri { get; set; } = string.Empty;      // https://<vault>.vault.azure.net/
}
