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
