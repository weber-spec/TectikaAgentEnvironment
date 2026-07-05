using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

/// <summary>The unified capability catalog: every tool an agent can use, from three sources — board (Tectika),
/// Foundry built-in, and integration (from a connection) — with a tenant-level global enable/disable. Core
/// Explore/Control board tools are the platform's spine and are never overridable. Credentials live on the
/// Connections page; this is purely about capabilities.</summary>
[ApiController]
[Route("api/tools")]
[Authorize]
public class ToolsController : ControllerBase
{
    public sealed record ToolItemDto(
        string ToolId, string Name, string Description, string Source, string Group,
        bool Enabled, bool Lockable, bool NeedsSetup, string? IconKey, bool? IsWrite, string? ConnectionCatalogId);
    public sealed record ToolsCatalogDto(
        IReadOnlyList<ToolItemDto> Board, IReadOnlyList<ToolItemDto> Foundry, IReadOnlyList<ToolItemDto> Integration);
    public sealed record SetEnabledRequest(bool Enabled);

    private readonly ICosmosDbService _cosmos;
    public ToolsController(ICosmosDbService cosmos) => _cosmos = cosmos;

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    // Tool id scheme (stable): board:{name} · foundry:{id} · integration:{catalogId}:{tool}.
    public static string BoardToolId(string name) => $"board:{name}";
    public static string FoundryToolId(string id) => $"foundry:{id}";
    public static string IntegrationToolId(string catalogId, string tool) => $"integration:{catalogId}:{tool}";

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog(CancellationToken ct)
    {
        var overrides = (await _cosmos.GetToolPolicyAsync(TenantId, ct))?.Overrides ?? new();
        var connectedCatalogIds = (await _cosmos.GetConnectionsAsync(TenantId, ct))
            .Select(c => c.CatalogId).ToHashSet(StringComparer.Ordinal);

        bool Effective(string toolId, bool dflt, bool lockable) =>
            !lockable ? dflt : overrides.TryGetValue(toolId, out var v) ? v : dflt;

        // Board tools — Explore/Control default-on & locked; Workspace/GitHub default-on & lockable.
        var board = TectikaToolSchema.Describe().Select(d =>
        {
            var id = BoardToolId(d.Name);
            return new ToolItemDto(id, d.Name, d.Description, "board", d.Group,
                Effective(id, dflt: true, d.Lockable), d.Lockable,
                NeedsSetup: d.RequiresWorkspace || d.RequiresGithubRead, IconKey: null, IsWrite: null, ConnectionCatalogId: null);
        }).ToList();

        // Foundry built-in — default OFF (opt-in). needsSetup when a project connection is required.
        var foundry = FoundryBuiltInTools.All.Select(t =>
        {
            var id = FoundryToolId(t.Id);
            return new ToolItemDto(id, t.Name, t.Description, "foundry", "Foundry",
                Effective(id, dflt: false, lockable: true), Lockable: true,
                NeedsSetup: t.RequiresProjectConnection, IconKey: "foundry", IsWrite: null, ConnectionCatalogId: null);
        }).ToList();

        // Integration tools — from the connection catalog; default-on, "needs a connection" when none exists.
        var integration = McpCatalog.Entries
            .Where(e => e.Tools.Count > 0)
            .SelectMany(e => e.Tools.Select(tool =>
            {
                var id = IntegrationToolId(e.Id, tool.Name);
                return new ToolItemDto(id, tool.Name, tool.Description, "integration", e.DisplayName,
                    Effective(id, dflt: true, lockable: true), Lockable: true,
                    NeedsSetup: !connectedCatalogIds.Contains(e.Id),
                    IconKey: e.Icon, IsWrite: tool.IsWrite, ConnectionCatalogId: e.Id);
            }))
            .ToList();

        return Ok(new ToolsCatalogDto(board, foundry, integration));
    }

    [HttpPut("{toolId}/enabled")]
    public async Task<IActionResult> SetEnabled(string toolId, [FromBody] SetEnabledRequest req, CancellationToken ct)
    {
        if (!IsOverridable(toolId, out var reason))
            return BadRequest(new { error = "NotOverridable", detail = reason });

        var policy = await _cosmos.GetToolPolicyAsync(TenantId, ct)
                     ?? new ToolPolicy { Id = TenantId, TenantId = TenantId };
        policy.Overrides[toolId] = req.Enabled;
        policy.UpdatedAt = DateTimeOffset.UtcNow;
        await _cosmos.UpsertToolPolicyAsync(policy, ct);
        return Ok(new { toolId, enabled = req.Enabled });
    }

    /// <summary>A tool may be globally toggled only if it exists and isn't a core (locked) board tool.</summary>
    private static bool IsOverridable(string toolId, out string reason)
    {
        reason = "";
        var parts = toolId.Split(':', 3);
        switch (parts[0])
        {
            case "board" when parts.Length == 2:
                var d = TectikaToolSchema.Describe().FirstOrDefault(x => x.Name == parts[1]);
                if (d is null) { reason = "Unknown board tool."; return false; }
                if (!d.Lockable) { reason = "Core board tools can't be disabled."; return false; }
                return true;
            case "foundry" when parts.Length == 2:
                if (FoundryBuiltInTools.Find(parts[1]) is null) { reason = "Unknown Foundry tool."; return false; }
                return true;
            case "integration" when parts.Length == 3:
                var entry = McpCatalog.Find(parts[1]);
                if (entry?.Tools.Any(t => t.Name == parts[2]) != true) { reason = "Unknown integration tool."; return false; }
                return true;
            default:
                reason = "Malformed tool id.";
                return false;
        }
    }
}
