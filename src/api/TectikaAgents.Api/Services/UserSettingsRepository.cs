using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public class UserSettingsRepository
{
    public const string ContainerName = "userSettings";

    private readonly CosmosClient _client;
    private readonly string _dbName;
    private readonly ILogger<UserSettingsRepository> _logger;

    public UserSettingsRepository(CosmosClient client, IOptions<CosmosDbSettings> settings, ILogger<UserSettingsRepository> logger)
    {
        _client = client;
        _dbName = settings.Value.DatabaseName;
        _logger = logger;
    }

    private Container Container => _client.GetContainer(_dbName, ContainerName);

    public virtual async Task<UserSettingsDocument> GetOrCreateAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            var res = await Container.ReadItemAsync<UserSettingsDocument>("preferences", new PartitionKey(userId), cancellationToken: ct);
            return res.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var doc = new UserSettingsDocument { UserId = userId };
            await Container.CreateItemAsync(doc, new PartitionKey(userId), cancellationToken: ct);
            _logger.LogInformation("[UserSettingsRepo] created default settings for userId={UserId}", userId);
            return doc;
        }
    }

    public virtual async Task UpsertAsync(UserSettingsDocument doc, CancellationToken ct = default)
    {
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await Container.UpsertItemAsync(doc, new PartitionKey(doc.UserId), cancellationToken: ct);
        _logger.LogDebug("[UserSettingsRepo] upserted settings for userId={UserId}", doc.UserId);
    }
}
