using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public class NotificationRepository
{
    public const string ContainerName = "notifications";

    private readonly CosmosClient _client;
    private readonly string _dbName;
    private readonly ILogger<NotificationRepository> _logger;

    public NotificationRepository(CosmosClient client, IOptions<CosmosDbSettings> settings, ILogger<NotificationRepository> logger)
    {
        _client = client;
        _dbName = settings.Value.DatabaseName;
        _logger = logger;
    }

    private Container Container => _client.GetContainer(_dbName, ContainerName);

    public virtual async Task SaveAsync(NotificationDocument doc, CancellationToken ct = default)
    {
        await Container.UpsertItemAsync(doc, new PartitionKey(doc.TenantId), cancellationToken: ct);
        _logger.LogDebug("[NotificationRepo] saved id={Id} type={Type}", doc.Id, doc.Type);
    }

    public virtual async Task<IReadOnlyList<NotificationDocument>> GetRecentAsync(string tenantId, int limit = 50, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT TOP @limit * FROM c WHERE c.tenantId = @tenantId ORDER BY c.timestamp DESC")
            .WithParameter("@limit", limit)
            .WithParameter("@tenantId", tenantId);

        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) };
        var iterator = Container.GetItemQueryIterator<NotificationDocument>(query, requestOptions: options);
        var results = new List<NotificationDocument>();

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }

        _logger.LogDebug("[NotificationRepo] fetched {Count} notifications for tenant={TenantId}", results.Count, tenantId);
        return results;
    }
}
