using TectikaAgents.AgentRuntime;

namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>How a catalog integration's tool calls are executed.</summary>
public enum McpBackend
{
    /// <summary>Reached through IMcpGateway as a remote MCP server (uses Endpoint/AuthHeader/AuthScheme).</summary>
    Mcp,
    /// <summary>Executed in-process by an IFirstPartyConnector against the provider's API (Endpoint unused).
    /// Mirrors the first-party GitHub executor; the path for providers with no token-auth remote MCP server.</summary>
    FirstParty,
}

/// <summary>Curated registry of connectable integrations. Each entry pins the exact tool surface we
/// expose (read/write flagged) so the per-role agent definition is board-independent. Bump <see cref="Version"/>
/// whenever the catalog changes so AgentInstructionsHash republishes affected agents.</summary>
public static class McpCatalog
{
    public const string Version = "mcp-catalog-v3";  // was v2 — send_email 'from' optional (defaults to the connection's From)

    /// <summary>Reuses TectikaToolSchema.ToolProp for property shapes so projection stays consistent.</summary>
    public sealed record CatalogTool(
        string Name, string Description,
        IReadOnlyDictionary<string, TectikaToolSchema.ToolProp> Properties, string[] Required, bool IsWrite);

    /// <summary><see cref="Backend"/> selects the execution path. For <see cref="McpBackend.FirstParty"/> entries
    /// Endpoint/AuthHeader/AuthScheme are unused (pass empty strings).</summary>
    public sealed record CatalogEntry(
        string Id, string DisplayName, string Description,
        string Endpoint, string AuthHeader, string AuthScheme, string TokenHint, string? HelpUrl,
        IReadOnlyList<CatalogTool> Tools, McpBackend Backend = McpBackend.Mcp);

    private static readonly Dictionary<string, TectikaToolSchema.ToolProp> NoProps = new();

    public static readonly IReadOnlyList<CatalogEntry> Entries = new CatalogEntry[]
    {
        // NOTE (curation): confirm the production Slack MCP endpoint before shipping; tests use a fake gateway,
        // so the URL does not affect them. Slack bot tokens auth via `Authorization: Bearer xoxb-…`.
        new("slack", "Slack", "Read channels and post messages to a connected Slack workspace.",
            Endpoint: "https://mcp.slack.example/mcp", AuthHeader: "Authorization", AuthScheme: "Bearer",
            TokenHint: "Slack Bot Token (xoxb-…)", HelpUrl: "https://api.slack.com/authentication/token-types",
            Tools: new CatalogTool[]
            {
                new("list_channels", "List channels in the connected Slack workspace.",
                    NoProps, [], IsWrite: false),
                new("post_message", "Post a message to a Slack channel.",
                    new Dictionary<string, TectikaToolSchema.ToolProp>
                    {
                        ["channel"] = new("string", "Channel id or name, e.g. '#general'."),
                        ["text"]    = new("string", "Message text to post."),
                    },
                    ["channel", "text"], IsWrite: true),
            }),

        // Email (Resend) — first-party: executed in-process by ResendEmailConnector against the Resend
        // REST API. No token-auth remote MCP server exists for Resend, so we connect first-party (like GitHub)
        // rather than self-host one. The board's Resend API key is the connection credential.
        new("email", "Email", "Send emails from your agents through a connected Resend account.",
            Endpoint: "", AuthHeader: "", AuthScheme: "",
            TokenHint: "Resend API key (re_…)", HelpUrl: "https://resend.com/api-keys",
            Tools: new CatalogTool[]
            {
                new("send_email", "Send an email through the connected Resend account.",
                    new Dictionary<string, TectikaToolSchema.ToolProp>
                    {
                        ["from"]    = new("string", "Optional sender override, e.g. 'Acme <noreply@yourdomain.com>'. Defaults to the board's configured sender; must be on a domain verified in the connected Resend account."),
                        ["to"]      = new("string", "Recipient email address."),
                        ["subject"] = new("string", "Email subject line."),
                        ["body"]    = new("string", "Plain-text body of the email."),
                    },
                    ["to", "subject", "body"], IsWrite: true),
            },
            Backend: McpBackend.FirstParty),
    };

    public static CatalogEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);
}
