# MCP Custom Tools — Phase 1 (Backend Spine) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a board connect a curated MCP integration (token auth, remote HTTP) and have agents that enable it call its tools — read always, write only when opted in — dispatched through `agentruntime` with full scrubbing/observability. No UI yet.

**Architecture:** A static `McpCatalog` pins each integration's tool schema (read/write flagged). The per-role Foundry agent definition carries the namespaced tools for the catalog ids the role enables (`AgentRole.McpServers`); write tools only when opted in (`AgentRole.McpWriteEnabled`). At run time a board supplies the credential: `Board.McpConnections` holds `{catalogId, secretName, …}`, threaded into the round like `BoardGitHub`. A new `McpToolExecutor` plugs into `RoundExecutor` via the existing `CanHandle`/`ExecuteAsync` seam and forwards calls to the MCP server through an `IMcpGateway` abstraction (the only place the MCP SDK lives, so everything else is unit-testable with a fake).

**Tech Stack:** C# / .NET, xUnit (`tests/TectikaAgents.Tests`, net10), Azure Functions isolated (workflows), ASP.NET (api), `ModelContextProtocol` .NET SDK, Azure Key Vault via `ISecretProvider`.

**Spec:** [`docs/superpowers/specs/2026-06-29-mcp-custom-tools-design.md`](../specs/2026-06-29-mcp-custom-tools-design.md)

---

## File Structure

**Create:**
- `src/core/TectikaAgents.Core/Models/McpConnection.cs` — `McpConnection` + `McpConnectionStatus` (per-board connection record).
- `src/core/TectikaAgents.Core/Interfaces/IMcpGateway.cs` — `IMcpGateway`, `McpServerTarget`, `McpToolInfo` (the SDK-insulating boundary).
- `src/agentruntime/Mcp/McpCatalog.cs` — curated catalog + `Version` + tool defs.
- `src/agentruntime/Mcp/McpToolNaming.cs` — `catalogId__tool` qualify/parse.
- `src/agentruntime/Mcp/McpToolExecutor.cs` — runtime dispatch (`CanHandle`/`ExecuteAsync`).
- `src/agentruntime/Mcp/McpGateway.cs` — real `IMcpGateway` over the MCP SDK.
- `src/api/TectikaAgents.Api/Controllers/McpCatalogController.cs` — `GET /api/mcp/catalog`.
- `src/api/TectikaAgents.Api/Controllers/BoardMcpConnectionsController.cs` — per-board connect/list/validate/disconnect.
- Tests: `McpToolNamingTests.cs`, `McpCatalogTests.cs`, `McpToolExecutorTests.cs`, `FakeMcpGateway.cs`, `BoardMcpConnectionsControllerTests.cs`, `McpCatalogControllerTests.cs` (+ additions to `TectikaToolSchemaTests.cs`, `AgentInstructionsHashToolsTests.cs`, `RoundExecutorTests.cs`).

**Modify:**
- `src/core/TectikaAgents.Core/Models/Board.cs` — add `McpConnections`.
- `src/core/TectikaAgents.Core/Models/AgentRole.cs` — add `McpWriteEnabled`; repurpose `McpServers`.
- `src/core/TectikaAgents.Core/Models/RoundContracts.cs` — add `BoardMcp` to `RoundRequest`.
- `src/agentruntime/TectikaToolSchema.cs` — append MCP tools in `ToFoundryToolsJson`; bump `Version`.
- `src/agentruntime/AgentInstructionsHash.cs` — fold MCP enablement + catalog version into the hash.
- `src/agentruntime/RoundExecutor.cs` — new params + MCP dispatch branch.
- `src/agentruntime/FoundryAgentRuntime.cs` — ctor `McpToolExecutor?`; use new projection/hash; pass `BoardMcp` + executor into `RoundExecutor`.
- `src/agentruntime/TectikaAgents.AgentRuntime.csproj` — add the MCP SDK package.
- `src/workflows/Activities/RunAgentRoundActivity.cs` — pass `BoardMcp = board.McpConnections`.
- `src/workflows/Program.cs` and `src/api/TectikaAgents.Api/Program.cs` — DI registration.

**Conventions (used throughout):**
- Build: `dotnet build TectikaAgents.slnx`
- Test: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~<ClassName>"`
- Secret name for a connection: `mcp-{boardId}-{connectionId}` (mirrors `github-pat-board-{boardId}`).
- Qualified tool name on the definition: `{catalogId}__{toolName}` (e.g. `slack__post_message`).

---

## Task 1: Core models — connection + write-enable fields

**Files:**
- Create: `src/core/TectikaAgents.Core/Models/McpConnection.cs`
- Modify: `src/core/TectikaAgents.Core/Models/Board.cs`
- Modify: `src/core/TectikaAgents.Core/Models/AgentRole.cs`

- [ ] **Step 1: Create the connection model**

`src/core/TectikaAgents.Core/Models/McpConnection.cs`:

```csharp
using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public enum McpConnectionStatus { Connected, Error, Disconnected }

/// <summary>A per-board connection to one catalog MCP integration. The token itself lives in Key Vault
/// under <see cref="SecretName"/>; this record only references it (mirrors GitHubRepoConnection.PatSecretName).</summary>
public sealed class McpConnection
{
    [JsonPropertyName("connectionId")] public string ConnectionId { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("catalogId")]    public string CatalogId { get; set; } = string.Empty;
    [JsonPropertyName("displayName")]  public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("secretName")]   public string SecretName { get; set; } = string.Empty;
    [JsonPropertyName("status")]       public McpConnectionStatus Status { get; set; } = McpConnectionStatus.Connected;
    [JsonPropertyName("lastValidatedAt")] public DateTimeOffset? LastValidatedAt { get; set; }
    [JsonPropertyName("createdBy")]    public string? CreatedBy { get; set; }
    [JsonPropertyName("createdAt")]    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 2: Add `McpConnections` to `Board`**

In `src/core/TectikaAgents.Core/Models/Board.cs`, after the `GitHub` property (line 44), add:

```csharp
    [JsonPropertyName("mcpConnections")]
    public List<McpConnection> McpConnections { get; set; } = [];
```

- [ ] **Step 3: Add `McpWriteEnabled` to `AgentRole`**

In `src/core/TectikaAgents.Core/Models/AgentRole.cs`, immediately after the `McpServers` property (line 30), add:

```csharp
    /// <summary>Catalog ids (subset of <see cref="McpServers"/>) for which this role may call WRITE tools.
    /// Write tools are omitted from the agent definition unless their catalog id appears here.</summary>
    [JsonPropertyName("mcpWriteEnabled")]
    public List<string> McpWriteEnabled { get; set; } = [];
```

Also update the `McpServers` doc-comment to its new meaning. Replace lines 29-30:

```csharp
    /// <summary>Catalog ids of MCP integrations this role is allowed to use (e.g. ["slack","notion"]).
    /// Drives which MCP tools are projected onto the Foundry agent definition.</summary>
    [JsonPropertyName("mcpServers")]
    public List<string> McpServers { get; set; } = [];
```

- [ ] **Step 4: Build**

Run: `dotnet build TectikaAgents.slnx`
Expected: build succeeds (no warnings introduced by these files).

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/McpConnection.cs src/core/TectikaAgents.Core/Models/Board.cs src/core/TectikaAgents.Core/Models/AgentRole.cs
git commit -m "feat(core): McpConnection model + Board.McpConnections + AgentRole.McpWriteEnabled"
```

---

## Task 2: `IMcpGateway` boundary

**Files:**
- Create: `src/core/TectikaAgents.Core/Interfaces/IMcpGateway.cs`

- [ ] **Step 1: Create the interface and its DTOs**

`src/core/TectikaAgents.Core/Interfaces/IMcpGateway.cs`:

```csharp
namespace TectikaAgents.Core.Interfaces;

/// <summary>Where + how to reach one remote MCP server. Token is the resolved secret value
/// (never logged). AuthScheme is "" for a raw header value, or e.g. "Bearer".</summary>
public sealed record McpServerTarget(string Endpoint, string AuthHeader, string AuthScheme, string Token);

/// <summary>One tool advertised by an MCP server (used only for connect-time validation today).</summary>
public sealed record McpToolInfo(string Name, string? Description);

/// <summary>The single seam over the MCP client SDK. Implemented for real by McpGateway and faked in tests.</summary>
public interface IMcpGateway
{
    /// <summary>Connect and list the server's tools. Throws on auth/transport failure (used to validate a connection).</summary>
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(McpServerTarget target, CancellationToken ct);

    /// <summary>Call one tool. <paramref name="argumentsJson"/> is the raw JSON object the model produced.
    /// Returns the tool result serialized as a JSON string.</summary>
    Task<string> CallToolAsync(McpServerTarget target, string toolName, string argumentsJson, CancellationToken ct);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build TectikaAgents.slnx`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/core/TectikaAgents.Core/Interfaces/IMcpGateway.cs
git commit -m "feat(core): IMcpGateway boundary over the MCP SDK"
```

---

## Task 3: Catalog + tool naming

**Files:**
- Create: `src/agentruntime/Mcp/McpToolNaming.cs`
- Create: `src/agentruntime/Mcp/McpCatalog.cs`
- Test: `tests/TectikaAgents.Tests/McpToolNamingTests.cs`, `tests/TectikaAgents.Tests/McpCatalogTests.cs`

- [ ] **Step 1: Write the failing naming test**

`tests/TectikaAgents.Tests/McpToolNamingTests.cs`:

```csharp
using TectikaAgents.AgentRuntime.Mcp;
using Xunit;

public class McpToolNamingTests
{
    [Fact]
    public void Qualify_joins_with_double_underscore()
        => Assert.Equal("slack__post_message", McpToolNaming.Qualify("slack", "post_message"));

    [Fact]
    public void TryParse_splits_on_first_separator()
    {
        Assert.True(McpToolNaming.TryParse("slack__post_message", out var cid, out var tool));
        Assert.Equal("slack", cid);
        Assert.Equal("post_message", tool);
    }

    [Fact]
    public void TryParse_preserves_underscores_in_tool_name()
    {
        Assert.True(McpToolNaming.TryParse("notion__create_data_source", out var cid, out var tool));
        Assert.Equal("notion", cid);
        Assert.Equal("create_data_source", tool);
    }

    [Fact]
    public void TryParse_rejects_unqualified_name()
        => Assert.False(McpToolNaming.TryParse("read_file", out _, out _));
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~McpToolNamingTests"`
Expected: FAIL — `McpToolNaming` does not exist (compile error).

- [ ] **Step 3: Implement the naming helper**

`src/agentruntime/Mcp/McpToolNaming.cs`:

```csharp
namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>Tool names exposed to the model are `{catalogId}__{toolName}` so MCP tools never collide
/// with built-in tools or each other. The separator is the FIRST "__"; the tool name keeps any others.</summary>
public static class McpToolNaming
{
    public const string Separator = "__";

    public static string Qualify(string catalogId, string toolName) => $"{catalogId}{Separator}{toolName}";

    public static bool TryParse(string qualified, out string catalogId, out string toolName)
    {
        catalogId = toolName = string.Empty;
        var i = qualified.IndexOf(Separator, StringComparison.Ordinal);
        if (i <= 0 || i + Separator.Length >= qualified.Length) return false;
        catalogId = qualified[..i];
        toolName = qualified[(i + Separator.Length)..];
        return true;
    }
}
```

- [ ] **Step 4: Run the naming test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~McpToolNamingTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Write the failing catalog test**

`tests/TectikaAgents.Tests/McpCatalogTests.cs`:

```csharp
using System.Linq;
using TectikaAgents.AgentRuntime.Mcp;
using Xunit;

public class McpCatalogTests
{
    [Fact]
    public void Slack_entry_exists_with_read_and_write_tools()
    {
        var slack = McpCatalog.Find("slack");
        Assert.NotNull(slack);
        Assert.Contains(slack!.Tools, t => t.Name == "list_channels" && !t.IsWrite);
        Assert.Contains(slack.Tools, t => t.Name == "post_message" && t.IsWrite);
    }

    [Fact]
    public void Find_returns_null_for_unknown_id()
        => Assert.Null(McpCatalog.Find("does-not-exist"));

    [Fact]
    public void Version_is_set()
        => Assert.False(string.IsNullOrEmpty(McpCatalog.Version));
}
```

- [ ] **Step 6: Run it to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~McpCatalogTests"`
Expected: FAIL — `McpCatalog` does not exist.

- [ ] **Step 7: Implement the catalog**

`src/agentruntime/Mcp/McpCatalog.cs`:

```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>Curated registry of connectable MCP integrations. Each entry pins the exact tool surface we
/// expose (read/write flagged) so the per-role agent definition is board-independent. Bump <see cref="Version"/>
/// whenever the catalog changes so AgentInstructionsHash republishes affected agents.</summary>
public static class McpCatalog
{
    public const string Version = "mcp-catalog-v1";

    /// <summary>Reuses TectikaToolSchema.ToolProp for property shapes so projection stays consistent.</summary>
    public sealed record CatalogTool(
        string Name, string Description,
        IReadOnlyDictionary<string, TectikaToolSchema.ToolProp> Properties, string[] Required, bool IsWrite);

    public sealed record CatalogEntry(
        string Id, string DisplayName, string Description,
        string Endpoint, string AuthHeader, string AuthScheme, string TokenHint, string? HelpUrl,
        IReadOnlyList<CatalogTool> Tools);

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
    };

    public static CatalogEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);
}
```

- [ ] **Step 8: Run the catalog test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~McpCatalogTests"`
Expected: PASS (3 tests).

- [ ] **Step 9: Commit**

```bash
git add src/agentruntime/Mcp/McpToolNaming.cs src/agentruntime/Mcp/McpCatalog.cs tests/TectikaAgents.Tests/McpToolNamingTests.cs tests/TectikaAgents.Tests/McpCatalogTests.cs
git commit -m "feat(agentruntime): curated MCP catalog + tool-name namespacing"
```

---

## Task 4: Project MCP tools onto the agent definition

**Files:**
- Modify: `src/agentruntime/TectikaToolSchema.cs`
- Test: `tests/TectikaAgents.Tests/TectikaToolSchemaTests.cs` (add cases)

- [ ] **Step 1: Write the failing projection test**

Append to `tests/TectikaAgents.Tests/TectikaToolSchemaTests.cs` (inside the existing test class; if it has a different name, add a new class `TectikaToolSchemaMcpTests` in the same file):

```csharp
    [Fact]
    public void Mcp_enabled_role_gets_read_tools_qualified()
    {
        var tools = TectikaToolSchema.ToFoundryToolsJson(
            new AgentPermissions(), github: null,
            mcpEnabled: new[] { "slack" }, mcpWriteEnabled: System.Array.Empty<string>());
        var names = ToolNames(tools);
        Assert.Contains("slack__list_channels", names);
        Assert.DoesNotContain("slack__post_message", names); // write not opted in
    }

    [Fact]
    public void Mcp_write_optin_adds_write_tools()
    {
        var tools = TectikaToolSchema.ToFoundryToolsJson(
            new AgentPermissions(), github: null,
            mcpEnabled: new[] { "slack" }, mcpWriteEnabled: new[] { "slack" });
        var names = ToolNames(tools);
        Assert.Contains("slack__list_channels", names);
        Assert.Contains("slack__post_message", names);
    }

    [Fact]
    public void Mcp_unknown_id_is_ignored()
    {
        var tools = TectikaToolSchema.ToFoundryToolsJson(
            new AgentPermissions(), github: null,
            mcpEnabled: new[] { "nope" }, mcpWriteEnabled: System.Array.Empty<string>());
        Assert.DoesNotContain(ToolNames(tools), n => n.StartsWith("nope__"));
    }

    // Reflects over the anonymous/record Foundry tool objects to pull each tool's "name".
    private static System.Collections.Generic.List<string> ToolNames(System.Collections.Generic.IReadOnlyList<object> tools)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(tools);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var list = new System.Collections.Generic.List<string>();
        foreach (var el in doc.RootElement.EnumerateArray())
            if (el.TryGetProperty("name", out var n)) list.Add(n.GetString()!);
        return list;
    }
```

Add `using TectikaAgents.Core.Models;` and `using TectikaAgents.AgentRuntime;` at the top of the file if not present.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~TectikaToolSchema"`
Expected: FAIL — `ToFoundryToolsJson` has no `mcpEnabled` overload.

- [ ] **Step 3: Extend `ToFoundryToolsJson` and bump the version**

In `src/agentruntime/TectikaToolSchema.cs`:

(a) Add the catalog using at the top:

```csharp
using TectikaAgents.AgentRuntime.Mcp;
```

(b) Bump the version (line 11):

```csharp
    public const string Version = "tools-v11";
```

(c) Replace the signature and add MCP projection. Replace the method header (line 167) and add the MCP block just before `return tools;` (line 185):

```csharp
    public static IReadOnlyList<object> ToFoundryToolsJson(AgentPermissions permissions, GitHubPermissions? github = null,
        IReadOnlyList<string>? mcpEnabled = null, IReadOnlyList<string>? mcpWriteEnabled = null)
    {
        var tools = Definitions.Select(d => (object)ToFoundryTool(d)).ToList();
```

…and immediately before `return tools;`:

```csharp
        // MCP integration tools (layer 3): read tools whenever the role enables the integration;
        // write tools only when its catalog id is also write-opted-in.
        if (mcpEnabled is not null)
        {
            var writeSet = new HashSet<string>(mcpWriteEnabled ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (var catalogId in mcpEnabled)
            {
                var entry = McpCatalog.Find(catalogId);
                if (entry is null) continue;
                var allowWrite = writeSet.Contains(catalogId);
                foreach (var t in entry.Tools)
                {
                    if (t.IsWrite && !allowWrite) continue;
                    tools.Add(ToFoundryTool(new ToolDef(
                        McpToolNaming.Qualify(catalogId, t.Name), t.Description, t.Properties, t.Required)));
                }
            }
        }
```

- [ ] **Step 4: Run the projection test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~TectikaToolSchema"`
Expected: PASS (existing tests + 3 new).

- [ ] **Step 5: Commit**

```bash
git add src/agentruntime/TectikaToolSchema.cs tests/TectikaAgents.Tests/TectikaToolSchemaTests.cs
git commit -m "feat(agentruntime): project MCP catalog tools onto the agent definition (read/write-gated)"
```

---

## Task 5: Fold MCP enablement into the agent hash

**Files:**
- Modify: `src/agentruntime/AgentInstructionsHash.cs`
- Test: `tests/TectikaAgents.Tests/AgentInstructionsHashToolsTests.cs` (add cases)

- [ ] **Step 1: Write the failing hash test**

Append to `tests/TectikaAgents.Tests/AgentInstructionsHashToolsTests.cs` (same class, or a new `AgentInstructionsHashMcpTests` class in the file). Add `using TectikaAgents.AgentRuntime;` and `using TectikaAgents.Core.Models;` if missing:

```csharp
    [Fact]
    public void Hash_changes_when_mcp_enabled_changes()
    {
        var perms = new AgentPermissions();
        var baseHash = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, null, null);
        var withSlack = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, new[] { "slack" }, null);
        Assert.NotEqual(baseHash, withSlack);
    }

    [Fact]
    public void Hash_changes_when_mcp_write_optin_changes()
    {
        var perms = new AgentPermissions();
        var readOnly = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, new[] { "slack" }, null);
        var write    = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, new[] { "slack" }, new[] { "slack" });
        Assert.NotEqual(readOnly, write);
    }

    [Fact]
    public void Hash_is_order_independent_for_mcp_lists()
    {
        var perms = new AgentPermissions();
        var a = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, new[] { "slack", "notion" }, null);
        var b = AgentInstructionsHash.Compute("p", "m", "tools-v11", perms, null, new[] { "notion", "slack" }, null);
        Assert.Equal(a, b);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~AgentInstructionsHash"`
Expected: FAIL — `Compute` has no MCP parameters.

- [ ] **Step 3: Extend `Compute`**

Replace `src/agentruntime/AgentInstructionsHash.cs` body with:

```csharp
using System.Security.Cryptography;
using System.Text;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>Deterministic hash of the agent-defining fields, to detect when a Foundry agent needs updating.</summary>
public static class AgentInstructionsHash
{
    public static string Compute(string systemPrompt, string model, string toolsVersion,
        AgentPermissions permissions, GitHubPermissions? github = null,
        IReadOnlyList<string>? mcpEnabled = null, IReadOnlyList<string>? mcpWriteEnabled = null)
    {
        var gh = github is null ? "" : $"gh:{github.CanRead}";
        var ws = $"ws:{permissions.CanUseWorkspace}";
        // Order-independent: a role's enabled/write lists are sets, not sequences.
        var mcp = $"mcp:{Join(mcpEnabled)}|mcpw:{Join(mcpWriteEnabled)}|cat:{McpCatalog.Version}";
        var bytes = Encoding.UTF8.GetBytes($"{model}\n{toolsVersion}\n{ws}|{gh}|{mcp}\n{systemPrompt}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string Join(IReadOnlyList<string>? items) =>
        items is null || items.Count == 0 ? "" : string.Join(",", items.OrderBy(x => x, StringComparer.Ordinal));
}
```

- [ ] **Step 4: Run the hash test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~AgentInstructionsHash"`
Expected: PASS (existing + 3 new).

- [ ] **Step 5: Commit**

```bash
git add src/agentruntime/AgentInstructionsHash.cs tests/TectikaAgents.Tests/AgentInstructionsHashToolsTests.cs
git commit -m "feat(agentruntime): fold MCP enablement + catalog version into the agent hash"
```

---

## Task 6: `McpToolExecutor`

**Files:**
- Create: `src/agentruntime/Mcp/McpToolExecutor.cs`
- Test: `tests/TectikaAgents.Tests/FakeMcpGateway.cs`, `tests/TectikaAgents.Tests/McpToolExecutorTests.cs`

- [ ] **Step 1: Create the fake gateway test double**

`tests/TectikaAgents.Tests/FakeMcpGateway.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.Core.Interfaces;

public sealed class FakeMcpGateway : IMcpGateway
{
    public bool ThrowOnList { get; set; }
    public McpServerTarget? LastTarget { get; private set; }
    public string? LastTool { get; private set; }
    public string? LastArgsJson { get; private set; }
    public string Result { get; set; } = "{\"ok\":true}";

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(McpServerTarget target, CancellationToken ct)
    {
        LastTarget = target;
        if (ThrowOnList) throw new System.Exception("auth failed");
        return Task.FromResult<IReadOnlyList<McpToolInfo>>(new[] { new McpToolInfo("list_channels", null) });
    }

    public Task<string> CallToolAsync(McpServerTarget target, string toolName, string argumentsJson, CancellationToken ct)
    {
        LastTarget = target; LastTool = toolName; LastArgsJson = argumentsJson;
        return Task.FromResult(Result);
    }
}
```

- [ ] **Step 2: Create an in-memory secret provider double (if not already present)**

Check whether the repo already has a fake `ISecretProvider`: `grep -rn "ISecretProvider" tests/`. If none exists, create `tests/TectikaAgents.Tests/FakeSecretProvider.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.Core.Interfaces;

public sealed class FakeSecretProvider : ISecretProvider
{
    public readonly Dictionary<string, string> Store = new();
    public Task<string?> GetSecretAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Store.TryGetValue(name, out var v) ? v : null);
    public Task SetSecretAsync(string name, string value, CancellationToken ct = default)
    { Store[name] = value; return Task.CompletedTask; }
}
```

(Confirm the exact `ISecretProvider` signatures in `src/core/TectikaAgents.Core/Interfaces/ISecretProvider.cs` and match them — `GetSecretAsync` returns `Task<string?>`, `SetSecretAsync` returns `Task`.)

- [ ] **Step 3: Write the failing executor tests**

`tests/TectikaAgents.Tests/McpToolExecutorTests.cs`:

```csharp
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Core.Models;
using Xunit;

public class McpToolExecutorTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static (McpToolExecutor exec, FakeMcpGateway gw, FakeSecretProvider secrets) Build()
    {
        var gw = new FakeMcpGateway();
        var secrets = new FakeSecretProvider();
        return (new McpToolExecutor(gw, secrets), gw, secrets);
    }

    private static List<McpConnection> SlackConn(string secretName = "s1") => new()
    {
        new McpConnection { CatalogId = "slack", SecretName = secretName, Status = McpConnectionStatus.Connected }
    };

    [Fact]
    public void CanHandle_only_known_catalog_tools()
    {
        var (exec, _, _) = Build();
        Assert.True(exec.CanHandle("slack__list_channels"));
        Assert.True(exec.CanHandle("slack__post_message"));
        Assert.False(exec.CanHandle("slack__not_a_tool"));
        Assert.False(exec.CanHandle("read_file"));
    }

    [Fact]
    public async Task Read_tool_calls_gateway_with_resolved_token()
    {
        var (exec, gw, secrets) = Build();
        secrets.Store["s1"] = "xoxb-abc";
        var role = new AgentRole { McpServers = { "slack" } };

        var result = await exec.ExecuteAsync("slack__list_channels", Args("{}"), SlackConn(), role, CancellationToken.None);

        Assert.Equal("list_channels", gw.LastTool);
        Assert.Equal("xoxb-abc", gw.LastTarget!.Token);
        Assert.Equal("Bearer", gw.LastTarget!.AuthScheme);
        Assert.Equal(gw.Result, result);
    }

    [Fact]
    public async Task No_connection_returns_friendly_error_and_does_not_call_gateway()
    {
        var (exec, gw, _) = Build();
        var role = new AgentRole { McpServers = { "slack" } };

        var result = await exec.ExecuteAsync("slack__list_channels", Args("{}"), new List<McpConnection>(), role, CancellationToken.None);

        Assert.Contains("not connected", result);
        Assert.Null(gw.LastTool);
    }

    [Fact]
    public async Task Write_tool_without_optin_is_refused()
    {
        var (exec, gw, secrets) = Build();
        secrets.Store["s1"] = "xoxb-abc";
        var role = new AgentRole { McpServers = { "slack" } }; // no McpWriteEnabled

        var result = await exec.ExecuteAsync("slack__post_message",
            Args("{\"channel\":\"#x\",\"text\":\"hi\"}"), SlackConn(), role, CancellationToken.None);

        Assert.Contains("not permitted", result);
        Assert.Null(gw.LastTool);
    }

    [Fact]
    public async Task Write_tool_with_optin_calls_gateway()
    {
        var (exec, gw, secrets) = Build();
        secrets.Store["s1"] = "xoxb-abc";
        var role = new AgentRole { McpServers = { "slack" }, McpWriteEnabled = { "slack" } };

        await exec.ExecuteAsync("slack__post_message",
            Args("{\"channel\":\"#x\",\"text\":\"hi\"}"), SlackConn(), role, CancellationToken.None);

        Assert.Equal("post_message", gw.LastTool);
    }

    [Fact]
    public async Task Gateway_exception_becomes_structured_error()
    {
        var gw = new FakeMcpGateway();
        var secrets = new FakeSecretProvider { };
        secrets.Store["s1"] = "xoxb-abc";
        var exec = new McpToolExecutor(new ThrowingGateway(), secrets);
        var role = new AgentRole { McpServers = { "slack" } };

        var result = await exec.ExecuteAsync("slack__list_channels", Args("{}"), SlackConn(), role, CancellationToken.None);
        Assert.Contains("error", result);
    }

    private sealed class ThrowingGateway : TectikaAgents.Core.Interfaces.IMcpGateway
    {
        public Task<IReadOnlyList<TectikaAgents.Core.Interfaces.McpToolInfo>> ListToolsAsync(TectikaAgents.Core.Interfaces.McpServerTarget t, CancellationToken ct) => throw new System.Exception("boom");
        public Task<string> CallToolAsync(TectikaAgents.Core.Interfaces.McpServerTarget t, string n, string a, CancellationToken ct) => throw new System.Exception("boom");
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~McpToolExecutorTests"`
Expected: FAIL — `McpToolExecutor` does not exist.

- [ ] **Step 5: Implement `McpToolExecutor`**

`src/agentruntime/Mcp/McpToolExecutor.cs`:

```csharp
using System.Text.Json;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>Dispatches namespaced MCP tool calls (`{catalogId}__{tool}`) to the connected board server.
/// Resolves the board's connection + Key Vault token, enforces the write opt-in, and forwards through
/// IMcpGateway. Plugs into RoundExecutor via CanHandle/ExecuteAsync (same shape as the GitHub executor).</summary>
public sealed class McpToolExecutor
{
    private readonly IMcpGateway _gateway;
    private readonly ISecretProvider _secrets;

    public McpToolExecutor(IMcpGateway gateway, ISecretProvider secrets)
    {
        _gateway = gateway;
        _secrets = secrets;
    }

    public bool CanHandle(string toolName) =>
        McpToolNaming.TryParse(toolName, out var cid, out var tool)
        && McpCatalog.Find(cid)?.Tools.Any(t => t.Name == tool) == true;

    public async Task<string> ExecuteAsync(string toolName, JsonElement args,
        IReadOnlyList<McpConnection>? boardConnections, AgentRole? role, CancellationToken ct)
    {
        if (!McpToolNaming.TryParse(toolName, out var catalogId, out var tool))
            return Err($"'{toolName}' is not a valid MCP tool name.");

        var entry = McpCatalog.Find(catalogId);
        var def = entry?.Tools.FirstOrDefault(t => t.Name == tool);
        if (entry is null || def is null)
            return Err($"Unknown MCP tool '{toolName}'.");

        var conn = boardConnections?.FirstOrDefault(c =>
            c.CatalogId == catalogId && c.Status == McpConnectionStatus.Connected);
        if (conn is null)
            return Err($"{entry.DisplayName} is not connected to this board. Ask a board admin to connect it in Board Settings → Integrations.");

        if (def.IsWrite && !(role?.McpWriteEnabled.Contains(catalogId) ?? false))
            return Err($"Write actions for {entry.DisplayName} are not permitted for this agent.");

        try
        {
            var token = await _secrets.GetSecretAsync(conn.SecretName, ct);
            if (string.IsNullOrEmpty(token))
                return Err($"{entry.DisplayName} credential is missing or expired. Reconnect it in Board Settings.");

            var target = new McpServerTarget(entry.Endpoint, entry.AuthHeader, entry.AuthScheme, token);
            return await _gateway.CallToolAsync(target, tool, args.GetRawText(), ct);
        }
        catch (Exception ex)
        {
            return Err($"{entry.DisplayName} call '{tool}' failed: {ex.Message}");
        }
    }

    private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg });
}
```

- [ ] **Step 6: Run the executor tests to verify they pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~McpToolExecutorTests"`
Expected: PASS (6 tests).

- [ ] **Step 7: Commit**

```bash
git add src/agentruntime/Mcp/McpToolExecutor.cs tests/TectikaAgents.Tests/FakeMcpGateway.cs tests/TectikaAgents.Tests/FakeSecretProvider.cs tests/TectikaAgents.Tests/McpToolExecutorTests.cs
git commit -m "feat(agentruntime): McpToolExecutor with no-connection + write-gate enforcement"
```

---

## Task 7: Real `McpGateway` over the MCP SDK

**Files:**
- Modify: `src/agentruntime/TectikaAgents.AgentRuntime.csproj`
- Create: `src/agentruntime/Mcp/McpGateway.cs`

> This is the ONLY file that touches the MCP SDK. **VERIFY** the exact transport/client API against the installed package version before finishing — the `IMcpGateway` boundary means nothing else depends on these specifics. Tests cover the executor via `FakeMcpGateway`, so this task is build-verified, not unit-tested.

- [ ] **Step 1: Add the package**

Add to the `<ItemGroup>` of `src/agentruntime/TectikaAgents.AgentRuntime.csproj`:

```xml
    <PackageReference Include="ModelContextProtocol" Version="*" />
```

Then run `dotnet restore TectikaAgents.slnx` and pin `Version="*"` to the concrete version it resolves (do not leave a floating wildcard in the final commit). Confirm the package's TFM is compatible with the agentruntime project's `TargetFramework`.

- [ ] **Step 2: Implement the adapter**

`src/agentruntime/Mcp/McpGateway.cs`. The shape below uses the SDK's HTTP/SSE client transport; adjust class/method names to the installed version (the public surface is small: build a client over an HTTP transport with an auth header, list tools, call a tool):

```csharp
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>Real IMcpGateway over the ModelContextProtocol client SDK (remote Streamable HTTP/SSE).
/// The auth token is injected as a request header; it is never logged.</summary>
public sealed class McpGateway : IMcpGateway
{
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(McpServerTarget target, CancellationToken ct)
    {
        await using var client = await ConnectAsync(target, ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return tools.Select(t => new McpToolInfo(t.Name, t.Description)).ToList();
    }

    public async Task<string> CallToolAsync(McpServerTarget target, string toolName, string argumentsJson, CancellationToken ct)
    {
        await using var client = await ConnectAsync(target, ct);
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) ?? new();
        var result = await client.CallToolAsync(toolName, args, cancellationToken: ct);
        return JsonSerializer.Serialize(result.Content);
    }

    private static async Task<IMcpClient> ConnectAsync(McpServerTarget target, CancellationToken ct)
    {
        var headerValue = string.IsNullOrEmpty(target.AuthScheme) ? target.Token : $"{target.AuthScheme} {target.Token}";
        var transport = new SseClientTransport(new SseClientTransportOptions
        {
            Endpoint = new Uri(target.Endpoint),
            AdditionalHeaders = new Dictionary<string, string> { [target.AuthHeader] = headerValue },
        });
        return await McpClientFactory.CreateAsync(transport, cancellationToken: ct);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build TectikaAgents.slnx`
Expected: build succeeds. If SDK symbol names differ, fix them here only.

- [ ] **Step 4: Commit**

```bash
git add src/agentruntime/TectikaAgents.AgentRuntime.csproj src/agentruntime/Mcp/McpGateway.cs
git commit -m "feat(agentruntime): McpGateway adapter over the ModelContextProtocol SDK"
```

---

## Task 8: Thread board connections through `RoundRequest` + `RoundExecutor`

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/RoundContracts.cs`
- Modify: `src/agentruntime/RoundExecutor.cs`
- Test: `tests/TectikaAgents.Tests/RoundExecutorTests.cs` (add a case)

- [ ] **Step 1: Add `BoardMcp` to `RoundRequest`**

In `src/core/TectikaAgents.Core/Models/RoundContracts.cs`, add a parameter to the `RoundRequest` record (after `Workspace`):

```csharp
    GitHubRepoConnection? BoardGitHub = null,
    TectikaAgents.Core.Interfaces.IWorkspaceProvider? Workspace = null,
    IReadOnlyList<McpConnection>? BoardMcp = null);
```

- [ ] **Step 2: Write the failing routing test**

Add to `tests/TectikaAgents.Tests/RoundExecutorTests.cs` (match its existing usings/explorer double — it uses `NullProjectExplorer`/`NnullProjectExplorer`; reuse whatever the file already uses):

```csharp
    [Fact]
    public async Task Routes_mcp_tool_call_to_executor()
    {
        var gw = new FakeMcpGateway { Result = "{\"channels\":[]}" };
        var secrets = new FakeSecretProvider();
        secrets.Store["s1"] = "xoxb-abc";
        var mcp = new TectikaAgents.AgentRuntime.Mcp.McpToolExecutor(gw, secrets);
        var conns = new System.Collections.Generic.List<McpConnection>
        {
            new() { CatalogId = "slack", SecretName = "s1", Status = McpConnectionStatus.Connected }
        };
        var role = new AgentRole { McpServers = { "slack" } };
        var resp = RoundResponse.Tools(new[] { new ToolCall("slack__list_channels", "{}", "call-1") });

        var result = await RoundExecutor.ExecuteOneRoundAsync(
            resp, new NullProjectExplorer(), (_, _) => { },
            gitHub: null, boardRepo: null, role: role,
            workspace: null, workspaceProvider: null,
            mcp: mcp, boardMcp: conns, ct: System.Threading.CancellationToken.None);

        Assert.Single(result.ToolOutputs);
        Assert.Contains("channels", result.ToolOutputs[0].Output);
        Assert.Equal("list_channels", gw.LastTool);
    }
```

(If `RoundExecutorTests` uses a differently-named explorer double, use that. The signature here adds the two trailing args `mcp:` and `boardMcp:`.)

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~RoundExecutorTests.Routes_mcp"`
Expected: FAIL — `ExecuteOneRoundAsync` has no `mcp`/`boardMcp` parameters.

- [ ] **Step 4: Add the params and dispatch branch**

In `src/agentruntime/RoundExecutor.cs`:

(a) add `using TectikaAgents.AgentRuntime.Mcp;` at the top.

(b) extend the method signature (line 27-31) — add two trailing parameters with defaults so existing callers still compile:

```csharp
    public static async Task<RoundProcessResult> ExecuteOneRoundAsync(
        RoundResponse resp, IProjectExplorer explorer, Action<string, string> onToolCall,
        IGitHubToolExecutor? gitHub, GitHubRepoConnection? boardRepo, AgentRole? role,
        WorkspaceToolExecutor? workspace, TectikaAgents.Core.Interfaces.IWorkspaceProvider? workspaceProvider,
        CancellationToken ct,
        McpToolExecutor? mcp = null, IReadOnlyList<McpConnection>? boardMcp = null)
```

(c) In the `default:` branch, BEFORE the GitHub block (line 172), add the MCP dispatch:

```csharp
                    if (mcp is not null && mcp.CanHandle(call.Name))
                    {
                        var mcpResult = await mcp.ExecuteAsync(call.Name, args, boardMcp, role, ct);
                        outputs.Add(new(call.CallId, mcpResult));
                        traced.Add(new(call.Name, call.ArgumentsJson, Summarize(mcpResult)));
                        break;
                    }
```

- [ ] **Step 5: Run the routing test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~RoundExecutorTests"`
Expected: PASS (existing + new). The `ct` is now a positional middle arg with `mcp`/`boardMcp` trailing — confirm existing callers in tests still compile (they pass `ct` as the last positional arg, which still binds to `ct`).

- [ ] **Step 6: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/RoundContracts.cs src/agentruntime/RoundExecutor.cs tests/TectikaAgents.Tests/RoundExecutorTests.cs
git commit -m "feat(agentruntime): thread board MCP connections into RoundExecutor dispatch"
```

---

## Task 9: Wire `FoundryAgentRuntime`

**Files:**
- Modify: `src/agentruntime/FoundryAgentRuntime.cs`

- [ ] **Step 1: Inject the executor**

Add the field + constructor param. Add `using TectikaAgents.AgentRuntime.Mcp;` at the top. Add field (after `_workspaceExecutor`, line 49):

```csharp
    private readonly McpToolExecutor? _mcp;
```

Extend the constructor signature (line 55-57) and assignment (line 65):

```csharp
    public FoundryAgentRuntime(IHttpClientFactory httpFactory, IOptions<FoundrySettings> settings,
        IOptions<LoggingSettings> logging, ILogger<FoundryAgentRuntime> logger,
        IGitHubToolExecutor? gitHub = null, WorkspaceToolExecutor? workspaceExecutor = null,
        McpToolExecutor? mcp = null)
```
```csharp
        _workspaceExecutor = workspaceExecutor;
        _mcp = mcp;
```

- [ ] **Step 2: Use the MCP projection + hash in `EnsureAgentAsync`**

Replace lines 82-84 (hash + definition):

```csharp
            var hash = AgentInstructionsHash.Compute(role.SystemPrompt, model, TectikaToolSchema.Version,
                role.Permissions, role.GitHubPermissions, role.McpServers, role.McpWriteEnabled);
            var definition = new AgentDefinition("prompt", model, role.SystemPrompt, role.DisplayName,
                TectikaToolSchema.ToFoundryToolsJson(role.Permissions, role.GitHubPermissions, role.McpServers, role.McpWriteEnabled));
```

- [ ] **Step 3: Pass `BoardMcp` + executor into the round in `RunRoundAsync`**

In `RunRoundAsync`, the `RoundExecutor.ExecuteOneRoundAsync` call (lines 277-280) gains the two trailing args:

```csharp
            var p = await RoundExecutor.ExecuteOneRoundAsync(round, explorer,
                (n, _) => OnText?.Invoke($"\n[using tool: {n}]\n"),
                _gitHub, req.BoardGitHub, req.Role,
                _workspaceExecutor, req.Workspace, ct,
                _mcp, req.BoardMcp).ConfigureAwait(false);
```

(The legacy in-proc `AgentToolLoop` path is left unchanged — it passes no MCP executor, so MCP tools degrade to "unknown tool" there, which is fine for Phase 1; that path is not used by the steerable runner.)

- [ ] **Step 4: Build**

Run: `dotnet build TectikaAgents.slnx`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/agentruntime/FoundryAgentRuntime.cs
git commit -m "feat(agentruntime): wire McpToolExecutor + MCP projection/hash into FoundryAgentRuntime"
```

---

## Task 10: Thread `BoardMcp` in the workflow activity

**Files:**
- Modify: `src/workflows/Activities/RunAgentRoundActivity.cs`

- [ ] **Step 1: Pass the board's connections into the round**

In `RunAgentRoundActivity.Run`, the `new RoundRequest(...) { ... }` initializer (lines 130-136) gains `BoardMcp`:

```csharp
            new RoundRequest(role, task, threadId, userInput, input.PendingToolOutputs, _maxCompletionTokens, input.RunId, input.Round)
            {
                BoardGitHub = board.GitHub,
                BoardMcp = board.McpConnections,
                Workspace = role.Permissions.CanUseWorkspace
                    ? new RunWorkspaceProvider(_cosmos, _workspace, _snapshots, _secrets, board, input.RunId, input.TaskId, role.Permissions.CanPushCode, _logger)
                    : null,
            },
```

- [ ] **Step 2: Build**

Run: `dotnet build TectikaAgents.slnx`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/workflows/Activities/RunAgentRoundActivity.cs
git commit -m "feat(workflows): pass board MCP connections into the agent round"
```

---

## Task 11: DI registration

**Files:**
- Modify: `src/workflows/Program.cs`
- Modify: `src/api/TectikaAgents.Api/Program.cs`

- [ ] **Step 1: Register in workflows**

In `src/workflows/Program.cs`, just after the `WorkspaceToolExecutor` registration (line 87), add:

```csharp
builder.Services.AddSingleton<TectikaAgents.Core.Interfaces.IMcpGateway, TectikaAgents.AgentRuntime.Mcp.McpGateway>();
builder.Services.AddSingleton<TectikaAgents.AgentRuntime.Mcp.McpToolExecutor>();
```

`FoundryAgentRuntime` will receive the `McpToolExecutor` automatically via its optional constructor parameter (confirm how `IAgentRuntime`/`FoundryAgentRuntime` is registered in this file and that DI can resolve the new singleton; if it is registered via a factory lambda, add `sp.GetService<McpToolExecutor>()` to that construction).

- [ ] **Step 2: Register in api**

In `src/api/TectikaAgents.Api/Program.cs`, just after the `IGitHubToolExecutor` registration (line 98), add:

```csharp
builder.Services.AddSingleton<TectikaAgents.Core.Interfaces.IMcpGateway, TectikaAgents.AgentRuntime.Mcp.McpGateway>();
builder.Services.AddSingleton<TectikaAgents.AgentRuntime.Mcp.McpToolExecutor>();
```

(The API needs `IMcpGateway` for connect-time validation in Task 13. `McpToolExecutor` is harmless to register here and lets `FoundryAgentRuntime` resolve uniformly.)

- [ ] **Step 3: Build**

Run: `dotnet build TectikaAgents.slnx`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/workflows/Program.cs src/api/TectikaAgents.Api/Program.cs
git commit -m "chore(di): register IMcpGateway + McpToolExecutor in workflows and api"
```

---

## Task 12: `GET /api/mcp/catalog`

**Files:**
- Create: `src/api/TectikaAgents.Api/Controllers/McpCatalogController.cs`
- Test: `tests/TectikaAgents.Tests/McpCatalogControllerTests.cs`

- [ ] **Step 1: Write the failing controller test**

`tests/TectikaAgents.Tests/McpCatalogControllerTests.cs`:

```csharp
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Controllers;
using Xunit;

public class McpCatalogControllerTests
{
    [Fact]
    public void Get_returns_catalog_entries_without_endpoints()
    {
        var result = new McpCatalogController().Get() as OkObjectResult;
        Assert.NotNull(result);
        var items = Assert.IsAssignableFrom<System.Collections.Generic.IEnumerable<McpCatalogController.CatalogDto>>(result!.Value);
        var slack = items.FirstOrDefault(i => i.Id == "slack");
        Assert.NotNull(slack);
        Assert.Equal("Slack", slack!.DisplayName);
        Assert.True(slack.ReadToolCount >= 1);
        Assert.True(slack.WriteToolCount >= 1);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~McpCatalogControllerTests"`
Expected: FAIL — `McpCatalogController` does not exist.

- [ ] **Step 3: Implement the controller**

`src/api/TectikaAgents.Api/Controllers/McpCatalogController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime.Mcp;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/mcp/catalog")]
[Authorize]
public class McpCatalogController : ControllerBase
{
    /// <summary>UI-facing catalog projection. Deliberately omits Endpoint/auth internals.</summary>
    public sealed record CatalogDto(
        string Id, string DisplayName, string Description, string TokenHint, string? HelpUrl,
        int ReadToolCount, int WriteToolCount);

    [HttpGet]
    public IActionResult Get() =>
        Ok(McpCatalog.Entries.Select(e => new CatalogDto(
            e.Id, e.DisplayName, e.Description, e.TokenHint, e.HelpUrl,
            e.Tools.Count(t => !t.IsWrite), e.Tools.Count(t => t.IsWrite))));
}
```

- [ ] **Step 4: Run the controller test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~McpCatalogControllerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/McpCatalogController.cs tests/TectikaAgents.Tests/McpCatalogControllerTests.cs
git commit -m "feat(api): GET /api/mcp/catalog"
```

---

## Task 13: Per-board connect / list / validate / disconnect

**Files:**
- Create: `src/api/TectikaAgents.Api/Controllers/BoardMcpConnectionsController.cs`
- Test: `tests/TectikaAgents.Tests/BoardMcpConnectionsControllerTests.cs`

> Read `RepoControllerTests.cs` first for how controllers are unit-tested here (claims principal, in-memory/fake Cosmos). Reuse `FakeMcpGateway` (Task 6) and `FakeSecretProvider`. For the Cosmos double, reuse whatever `RepoControllerTests` / `FakeCosmosForRepo.cs` provides; if a board getter/updater isn't there, use `InMemoryCosmosDbService`.

- [ ] **Step 1: Write the failing controller tests**

`tests/TectikaAgents.Tests/BoardMcpConnectionsControllerTests.cs`:

```csharp
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Controllers;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

public class BoardMcpConnectionsControllerTests
{
    private static BoardMcpConnectionsController Build(ICosmosDbService cosmos, FakeMcpGateway gw, FakeSecretProvider secrets)
    {
        var ctrl = new BoardMcpConnectionsController(cosmos, gw, secrets);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tid", "t1"),
            new Claim("preferred_username", "eli@tectika.com"),
        }, "test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return ctrl;
    }

    private static async Task<(ICosmosDbService cosmos, string boardId)> SeedBoardAsync()
    {
        var cosmos = new InMemoryCosmosDbService();
        var board = await cosmos.CreateBoardAsync(new Board { TenantId = "t1", Name = "B", OwnerId = "eli@tectika.com" });
        return (cosmos, board.Id);
    }

    [Fact]
    public async Task Connect_validates_stores_secret_and_persists_connection()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var gw = new FakeMcpGateway();
        var secrets = new FakeSecretProvider();
        var ctrl = Build(cosmos, gw, secrets);

        var res = await ctrl.Connect(boardId, new BoardMcpConnectionsController.ConnectRequest("slack", "My Slack", "xoxb-abc"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(res);
        var conn = Assert.IsType<McpConnection>(ok.Value);
        Assert.Equal("slack", conn.CatalogId);
        Assert.Equal("xoxb-abc", secrets.Store[conn.SecretName]);
        var board = await cosmos.GetBoardAsync("t1", boardId, CancellationToken.None);
        Assert.Single(board!.McpConnections);
    }

    [Fact]
    public async Task Connect_validation_failure_returns_400_and_stores_nothing()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var gw = new FakeMcpGateway { ThrowOnList = true };
        var secrets = new FakeSecretProvider();
        var ctrl = Build(cosmos, gw, secrets);

        var res = await ctrl.Connect(boardId, new BoardMcpConnectionsController.ConnectRequest("slack", "My Slack", "bad"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(res);
        Assert.Empty(secrets.Store);
        var board = await cosmos.GetBoardAsync("t1", boardId, CancellationToken.None);
        Assert.Empty(board!.McpConnections);
    }

    [Fact]
    public async Task Connect_unknown_catalog_id_returns_400()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var ctrl = Build(cosmos, new FakeMcpGateway(), new FakeSecretProvider());
        var res = await ctrl.Connect(boardId, new BoardMcpConnectionsController.ConnectRequest("nope", "x", "t"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(res);
    }

    [Fact]
    public async Task Disconnect_removes_connection_and_deletes_secret()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var gw = new FakeMcpGateway();
        var secrets = new FakeSecretProvider();
        var ctrl = Build(cosmos, gw, secrets);
        var ok = (OkObjectResult)await ctrl.Connect(boardId, new BoardMcpConnectionsController.ConnectRequest("slack", "My Slack", "xoxb-abc"), CancellationToken.None);
        var conn = (McpConnection)ok.Value!;

        var res = await ctrl.Disconnect(boardId, conn.ConnectionId, CancellationToken.None);

        Assert.IsType<NoContentResult>(res);
        var board = await cosmos.GetBoardAsync("t1", boardId, CancellationToken.None);
        Assert.Empty(board!.McpConnections);
        Assert.False(secrets.Store.ContainsKey(conn.SecretName));
    }

    [Fact]
    public async Task List_returns_board_connections()
    {
        var (cosmos, boardId) = await SeedBoardAsync();
        var ctrl = Build(cosmos, new FakeMcpGateway(), new FakeSecretProvider());
        await ctrl.Connect(boardId, new BoardMcpConnectionsController.ConnectRequest("slack", "My Slack", "xoxb-abc"), CancellationToken.None);

        var res = await ctrl.List(boardId, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(res);
        var items = Assert.IsAssignableFrom<System.Collections.Generic.List<McpConnection>>(ok.Value);
        Assert.Single(items);
    }
}
```

> If `FakeSecretProvider` lacks a delete capability, add `public Task DeleteSecretAsync(string name, ...)` only if `ISecretProvider` declares one; otherwise the controller "deletes" by overwriting/removing from the store via a method that exists. Check `ISecretProvider` — if it has no delete, the controller should null the reference and simply remove the connection (document that the orphaned secret is cleaned up by a later phase), and the disconnect test should assert only connection removal. Adjust the test to match the real `ISecretProvider` surface.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~BoardMcpConnectionsControllerTests"`
Expected: FAIL — controller does not exist.

- [ ] **Step 3: Implement the controller**

`src/api/TectikaAgents.Api/Controllers/BoardMcpConnectionsController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/boards/{boardId}/mcp")]
[Authorize]
public class BoardMcpConnectionsController : ControllerBase
{
    public sealed record ConnectRequest(string CatalogId, string? DisplayName, string Token);

    private readonly ICosmosDbService _cosmos;
    private readonly IMcpGateway _gateway;
    private readonly ISecretProvider _secrets;

    public BoardMcpConnectionsController(ICosmosDbService cosmos, IMcpGateway gateway, ISecretProvider secrets)
    {
        _cosmos = cosmos;
        _gateway = gateway;
        _secrets = secrets;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId   => User.FindFirst("preferred_username")?.Value ?? "unknown";

    [HttpGet]
    public async Task<IActionResult> List(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        return board is null ? NotFound() : Ok(board.McpConnections);
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect(string boardId, [FromBody] ConnectRequest req, CancellationToken ct)
    {
        // NB: never log req.Token — it is a third-party credential.
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();

        var entry = McpCatalog.Find(req.CatalogId);
        if (entry is null) return BadRequest(new { error = "UnknownIntegration" });

        // Validate the credential by connecting + listing tools BEFORE storing anything.
        try
        {
            await _gateway.ListToolsAsync(
                new McpServerTarget(entry.Endpoint, entry.AuthHeader, entry.AuthScheme, req.Token), ct);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = "ValidationFailed", detail = ex.Message });
        }

        var connectionId = Guid.NewGuid().ToString();
        var secretName = $"mcp-{boardId}-{connectionId}";
        await _secrets.SetSecretAsync(secretName, req.Token, ct);

        var conn = new McpConnection
        {
            ConnectionId = connectionId,
            CatalogId = entry.Id,
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? entry.DisplayName : req.DisplayName!,
            SecretName = secretName,
            Status = McpConnectionStatus.Connected,
            LastValidatedAt = DateTimeOffset.UtcNow,
            CreatedBy = UserId,
        };
        board.McpConnections.Add(conn);
        await _cosmos.UpdateBoardAsync(board, ct);
        return Ok(conn);
    }

    [HttpPost("{connectionId}/validate")]
    public async Task<IActionResult> Validate(string boardId, string connectionId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        var conn = board.McpConnections.FirstOrDefault(c => c.ConnectionId == connectionId);
        if (conn is null) return NotFound();
        var entry = McpCatalog.Find(conn.CatalogId);
        if (entry is null) return BadRequest(new { error = "UnknownIntegration" });

        var token = await _secrets.GetSecretAsync(conn.SecretName, ct);
        try
        {
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Credential missing.");
            await _gateway.ListToolsAsync(new McpServerTarget(entry.Endpoint, entry.AuthHeader, entry.AuthScheme, token!), ct);
            conn.Status = McpConnectionStatus.Connected;
        }
        catch
        {
            conn.Status = McpConnectionStatus.Error;
        }
        conn.LastValidatedAt = DateTimeOffset.UtcNow;
        await _cosmos.UpdateBoardAsync(board, ct);
        return Ok(conn);
    }

    [HttpDelete("{connectionId}")]
    public async Task<IActionResult> Disconnect(string boardId, string connectionId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        var conn = board.McpConnections.FirstOrDefault(c => c.ConnectionId == connectionId);
        if (conn is null) return NotFound();

        board.McpConnections.Remove(conn);
        await _cosmos.UpdateBoardAsync(board, ct);
        // Best-effort secret cleanup (ISecretProvider may not expose delete; see note in plan).
        try { await _secrets.SetSecretAsync(conn.SecretName, string.Empty, ct); } catch { /* ignore */ }
        return NoContent();
    }
}
```

> The disconnect test asserts the secret is gone. If `ISecretProvider` has no delete, change `FakeSecretProvider` so that `SetSecretAsync(name, "")` removes the key (`if (value == "") Store.Remove(name); else Store[name] = value;`) — that keeps the test meaningful and matches the controller's best-effort cleanup. If `ISecretProvider` DOES have a delete method, call it instead and assert on that.

- [ ] **Step 4: Run the controller tests to verify they pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~BoardMcpConnectionsControllerTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/BoardMcpConnectionsController.cs tests/TectikaAgents.Tests/BoardMcpConnectionsControllerTests.cs tests/TectikaAgents.Tests/FakeSecretProvider.cs
git commit -m "feat(api): per-board MCP connect/list/validate/disconnect"
```

---

## Task 14: Full sweep + mock-path check

**Files:**
- Verify: `src/agentruntime/MockAgentRuntime.cs` (no change expected; confirm it compiles and does not dispatch tools)

- [ ] **Step 1: Confirm the mock runtime is unaffected**

Run: `grep -n "RoundExecutor\|ToFoundryToolsJson\|ExecuteOneRound" src/agentruntime/MockAgentRuntime.cs`
Expected: no matches (the mock returns canned outcomes and does not call the real dispatch). If it DOES call `ToFoundryToolsJson` or `RoundExecutor`, update those call sites to the new signatures (pass `null` for the MCP args) and re-run.

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build TectikaAgents.slnx`
Expected: build succeeds with no new warnings.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`
Expected: all tests pass (existing suite + the ~22 new MCP tests). Investigate any failure before continuing — a likely culprit is an existing caller of `ToFoundryToolsJson` / `RoundExecutor.ExecuteOneRoundAsync` / `AgentInstructionsHash.Compute` that needs the new optional args (they default, so should compile, but confirm).

- [ ] **Step 4: Commit (if Step 1 required a change)**

```bash
git add -A
git commit -m "chore(agentruntime): keep MockAgentRuntime aligned with new tool-dispatch signatures"
```

---

## Done-when (Phase 1 acceptance)

- A board can connect a catalog integration (token validated, secret stored, connection persisted) and disconnect it, via the API.
- An agent role that lists a catalog id in `McpServers` gets that integration's read tools on its Foundry definition; write tools appear only when the id is also in `McpWriteEnabled`; changing either republishes the agent (hash changes).
- At run time, a connected integration's tool call is dispatched through `McpToolExecutor` to the board's connection; an un-connected integration returns a friendly error; a write tool without opt-in is refused; outputs flow through the existing scrub/cap path.
- Full test suite green. No UI yet (Phase 2).

## Notes carried forward (out of Phase 1)

- **Phase 2:** Integrations tab in Board Settings + agent-editor enablement toggles (web).
- **Phase 3:** per-action write approval (executor pauses the run for human sign-off, then runs the held call on resume) + run-time "connected integrations on this board" context line injected by `RunAgentRoundActivity`.
- **Secret deletion:** if `ISecretProvider` gains a real delete, replace the best-effort empty-set cleanup in `Disconnect`.
- **infra/:** no new Azure resources for remote-HTTP v1; revisit only if a new secret-naming convention needs documenting (per the deployment-idempotency rule).
