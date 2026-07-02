using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.Api.Controllers;

/// <summary>Read-only view of what the connected Azure AI Foundry project offers, for the Connections → Foundry
/// tab: live connections + model deployments enumerated from the project, plus the fixed catalog of built-in
/// agent tool types (which Foundry exposes via the SDK/portal, not an enumeration endpoint).</summary>
[ApiController]
[Route("api/foundry/catalog")]
[Authorize]
public class FoundryCatalogController : ControllerBase
{
    public sealed record FoundryConnectionDto(string Name, string Type, string? Target);
    public sealed record BuiltInToolDto(string Id, string Name, string Description);
    public sealed record FoundryCatalogDto(
        bool Available,
        IReadOnlyList<FoundryConnectionDto> Connections,
        IReadOnlyList<string> Deployments,
        IReadOnlyList<BuiltInToolDto> BuiltInTools);

    // Foundry's built-in tool types are a fixed platform catalog (no "list tool types" endpoint), so we
    // describe them statically. Kept here so the tab can render them alongside the live data.
    private static readonly BuiltInToolDto[] BuiltInTools =
    {
        new("web_search",       "Web Search",       "Ground answers in live web results."),
        new("file_search",      "File Search",      "Retrieve over files/vector stores attached to the agent."),
        new("code_interpreter", "Code Interpreter", "Run code in a sandbox to compute, analyze, and chart."),
        new("azure_ai_search",  "Azure AI Search",  "Query an Azure AI Search index as a knowledge source."),
        new("function",         "Functions",        "Call your own functions/OpenAPI tools."),
        new("mcp",              "MCP Servers",      "Connect remote MCP tool servers to the agent."),
    };

    private readonly FoundryConnectionsCatalog _connections;
    private readonly IModelCatalog _models;
    private readonly ILogger<FoundryCatalogController> _logger;

    public FoundryCatalogController(FoundryConnectionsCatalog connections, IModelCatalog models,
        ILogger<FoundryCatalogController> logger)
    {
        _connections = connections; _models = models; _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        IReadOnlyList<FoundryConnectionDto> conns = [];
        IReadOnlyList<string> deployments = [];
        var available = true;
        try
        {
            var raw = await _connections.ListAsync(ct);
            conns = raw.Where(c => !string.IsNullOrEmpty(c.Name))
                       .Select(c => new FoundryConnectionDto(c.Name!, c.Type ?? "Unknown", c.Target))
                       .ToList();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[FoundryCatalog] connections unavailable"); available = false; }

        try { deployments = await _models.ListModelsAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[FoundryCatalog] deployments unavailable"); available = false; }

        return Ok(new FoundryCatalogDto(available, conns, deployments, BuiltInTools));
    }
}
