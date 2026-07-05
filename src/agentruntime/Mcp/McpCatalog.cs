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

/// <summary>Category buckets used to group connections in the UI. Kept as strings (not an enum) so new
/// buckets can be added without a schema change.</summary>
public static class ConnectionCategory
{
    public const string Model         = "model";          // model providers (Foundry, Anthropic/Claude)
    public const string AgentTool     = "agent-tool";     // external tools agents call (Slack, Email, …)
    public const string SourceControl = "source-control"; // GitHub, …
}

/// <summary>One credential input a connection requires. Most integrations need a single <c>token</c>; a few
/// need several (key + secret). <see cref="Secret"/> masks the field in the UI and marks it for Key Vault.</summary>
public sealed record AuthField(string Name, string Label, string Type, string Hint, bool Secret = true);

/// <summary>Curated registry of connectable integrations. Each entry pins the exact tool surface we
/// expose (read/write flagged) so the per-role agent definition is board-independent. Bump <see cref="Version"/>
/// whenever the catalog changes so AgentInstructionsHash republishes affected agents.</summary>
public static class McpCatalog
{
    public const string Version = "mcp-catalog-v5";  // was v4 — Slack moved to first-party (SlackConnector) + github entry

    /// <summary>Reuses TectikaToolSchema.ToolProp for property shapes so projection stays consistent.</summary>
    public sealed record CatalogTool(
        string Name, string Description,
        IReadOnlyDictionary<string, TectikaToolSchema.ToolProp> Properties, string[] Required, bool IsWrite);

    /// <summary><see cref="Backend"/> selects the execution path. For <see cref="McpBackend.FirstParty"/> entries
    /// Endpoint/AuthHeader/AuthScheme are unused (pass empty strings). <see cref="Category"/>/<see cref="IconKey"/>/
    /// <see cref="SupportsMultiple"/>/<see cref="AuthFields"/> drive the Connections UI; when AuthFields is null a
    /// single password field derived from <see cref="TokenHint"/> is assumed (see <see cref="EffectiveAuthFields"/>).</summary>
    public sealed record CatalogEntry(
        string Id, string DisplayName, string Description,
        string Endpoint, string AuthHeader, string AuthScheme, string TokenHint, string? HelpUrl,
        IReadOnlyList<CatalogTool> Tools, McpBackend Backend = McpBackend.Mcp,
        string Category = ConnectionCategory.AgentTool, string? IconKey = null,
        bool SupportsMultiple = true, IReadOnlyList<AuthField>? AuthFields = null)
    {
        /// <summary>Brand-icon key for the UI; defaults to the entry id.</summary>
        public string Icon => IconKey ?? Id;

        /// <summary>The auth inputs to collect, defaulting to a single masked "token" field from TokenHint.</summary>
        public IReadOnlyList<AuthField> EffectiveAuthFields =>
            AuthFields is { Count: > 0 } ? AuthFields
            : new[] { new AuthField("token", "Token", "password", TokenHint) };
    }

    private static readonly Dictionary<string, TectikaToolSchema.ToolProp> NoProps = new();

    public static readonly IReadOnlyList<CatalogEntry> Entries = new CatalogEntry[]
    {
        // Slack — first-party: executed in-process by SlackConnector against the Slack Web API (auth.test /
        // conversations.list / chat.postMessage) with the workspace's Bot User OAuth token. No remote MCP
        // server; the bot token is the connection credential.
        new("slack", "Slack", "Read channels and post messages to a connected Slack workspace.",
            Endpoint: "", AuthHeader: "", AuthScheme: "",
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
            },
            Backend: McpBackend.FirstParty,
            Category: ConnectionCategory.AgentTool, IconKey: "slack"),

        // Claude (Anthropic) — a MODEL provider, not a tool integration (no tools). Powers ClaudeCode agents.
        // Not tool-executed, so Backend is FirstParty only to keep it off the remote-MCP validation path; the
        // Connections controller skips credential validation for the "model" category.
        new("anthropic", "Claude (Anthropic)",
            "Run agents on Claude models using an Anthropic API key or a Pro/Max OAuth token.",
            Endpoint: "", AuthHeader: "", AuthScheme: "",
            TokenHint: "Anthropic API key (sk-ant-…) or OAuth token", HelpUrl: "https://console.anthropic.com/settings/keys",
            Tools: Array.Empty<CatalogTool>(),
            Backend: McpBackend.FirstParty,
            Category: ConnectionCategory.Model, IconKey: "anthropic", SupportsMultiple: true,
            AuthFields: new[] { new AuthField("token", "API key or OAuth token", "password", "sk-ant-… or an OAuth token from `claude setup-token`") }),

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
            Backend: McpBackend.FirstParty,
            Category: ConnectionCategory.AgentTool, IconKey: "resend"),

        // GitHub — a SOURCE-CONTROL connection: the PAT lives here (tenant-level); each board picks this
        // connection + a specific repo (Board Settings → Integrations). Not tool-executed via this catalog
        // (the GitHub read/write tools resolve the repo's PatSecretName), so no tools + no remote validation.
        new("github", "GitHub",
            "Connect a GitHub account once with a personal access token, then choose the repo per board.",
            Endpoint: "", AuthHeader: "", AuthScheme: "",
            TokenHint: "GitHub PAT (ghp_… or github_pat_…)", HelpUrl: "https://github.com/settings/tokens",
            Tools: Array.Empty<CatalogTool>(),
            Backend: McpBackend.FirstParty,
            Category: ConnectionCategory.SourceControl, IconKey: "github", SupportsMultiple: true,
            AuthFields: new[] { new AuthField("token", "Personal access token", "password", "ghp_… or a fine-grained github_pat_…") }),
    };

    public static CatalogEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);
}
