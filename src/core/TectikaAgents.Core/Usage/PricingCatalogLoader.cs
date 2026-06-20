using System.Reflection;
using System.Text.Json;

namespace TectikaAgents.Core.Usage;

public static class PricingCatalogLoader
{
    private const string ResourceName = "TectikaAgents.Core.Resources.pricing-catalog.json";

    public static PricingCatalog LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded pricing catalog '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<PricingCatalog>(json)
            ?? throw new InvalidOperationException("Pricing catalog deserialized to null.");
    }
}
