using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class NotificationRecipientFilterTests
{
    private static InMemoryNotificationRepository NewRepo() =>
        new(NullLogger<NotificationRepository>.Instance);

    [Fact]
    public async Task GetRecent_returns_tenant_wide_and_own_targeted_only()
    {
        var repo = NewRepo();
        await repo.SaveAsync(new NotificationDocument { TenantId = "default", Title = "broadcast" });                 // recipient null
        await repo.SaveAsync(new NotificationDocument { TenantId = "default", Title = "for-maya", RecipientUserId = "maya@tectika.com" });
        await repo.SaveAsync(new NotificationDocument { TenantId = "default", Title = "for-eli", RecipientUserId = "eli@tectika.com" });

        var eli = await repo.GetRecentAsync("default", "eli@tectika.com");
        Assert.Contains(eli, n => n.Title == "broadcast");
        Assert.Contains(eli, n => n.Title == "for-eli");
        Assert.DoesNotContain(eli, n => n.Title == "for-maya");
    }
}
