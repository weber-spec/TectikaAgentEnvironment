using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>In-memory stub used when MockDatabase:Enabled=true. Not persisted across restarts.</summary>
public sealed class InMemoryNotificationRepository : NotificationRepository
{
    private readonly List<NotificationDocument> _store = [];

    public InMemoryNotificationRepository(ILogger<NotificationRepository> logger)
        : base(
            new CosmosClient("AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="),
            Options.Create(new CosmosDbSettings { DatabaseName = "mock" }),
            logger)
    { }

    public override Task SaveAsync(NotificationDocument doc, CancellationToken ct = default)
    {
        lock (_store) _store.Insert(0, doc);
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<NotificationDocument>> GetRecentAsync(string tenantId, int limit = 50, CancellationToken ct = default)
    {
        lock (_store)
        {
            IReadOnlyList<NotificationDocument> result = _store.Take(limit).ToList();
            return Task.FromResult(result);
        }
    }
}

/// <summary>In-memory stub used when MockDatabase:Enabled=true. Not persisted across restarts.</summary>
public sealed class InMemoryUserSettingsRepository : UserSettingsRepository
{
    private readonly Dictionary<string, UserSettingsDocument> _store = [];

    public InMemoryUserSettingsRepository(ILogger<UserSettingsRepository> logger)
        : base(
            new CosmosClient("AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="),
            Options.Create(new CosmosDbSettings { DatabaseName = "mock" }),
            logger)
    { }

    public override Task<UserSettingsDocument> GetOrCreateAsync(string userId, CancellationToken ct = default)
    {
        lock (_store)
        {
            if (!_store.TryGetValue(userId, out var doc))
            {
                doc = new UserSettingsDocument { UserId = userId };
                _store[userId] = doc;
            }
            return Task.FromResult(doc);
        }
    }

    public override Task UpsertAsync(UserSettingsDocument doc, CancellationToken ct = default)
    {
        lock (_store) _store[doc.UserId] = doc;
        return Task.CompletedTask;
    }
}
