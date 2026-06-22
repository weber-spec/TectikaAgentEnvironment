using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Controllers;
using TectikaAgents.Core.Interfaces;
using Xunit;

public class ModelsControllerTests
{
    [Fact]
    public async Task List_ReturnsCatalogModels()
    {
        var controller = new ModelsController(
            new StubCatalog(["gpt-4o", "o3"]), NullLogger<ModelsController>.Instance);

        var result = await controller.List(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(new[] { "gpt-4o", "o3" }, (IReadOnlyList<string>)ok.Value!);
    }

    [Fact]
    public async Task List_Returns502_WhenCatalogThrows()
    {
        var controller = new ModelsController(
            new ThrowingCatalog(), NullLogger<ModelsController>.Instance);

        var result = await controller.List(default);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, obj.StatusCode);
    }

    private sealed class StubCatalog(IReadOnlyList<string> models) : IModelCatalog
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult(models);
    }

    private sealed class ThrowingCatalog : IModelCatalog
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            throw new HttpRequestException("foundry down");
    }
}
