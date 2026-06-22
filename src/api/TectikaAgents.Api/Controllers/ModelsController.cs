using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.Api.Controllers;

/// <summary>Lists the models available for agents (the model picker). Backed by <see cref="IModelCatalog"/>:
/// a static list in mock mode, the Foundry project's deployments in real mode.</summary>
[ApiController]
[Route("api/models")]
[Authorize]
public class ModelsController : ControllerBase
{
    private readonly IModelCatalog _catalog;
    private readonly ILogger<ModelsController> _logger;

    public ModelsController(IModelCatalog catalog, ILogger<ModelsController> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>200 with the model-name list; 502 if the live Foundry catalog is unreachable.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<string>>> List(CancellationToken ct)
    {
        try
        {
            return Ok(await _catalog.ListModelsAsync(ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Models] could not list models from the Foundry catalog");
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Could not load models from Foundry." });
        }
    }
}
