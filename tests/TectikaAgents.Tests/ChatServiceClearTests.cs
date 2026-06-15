using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;
using Xunit;

public class ChatServiceClearTests
{
    private static IHttpClientFactory HttpFactory() =>
        new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

    private static ChatService Make(InMemoryCosmosDbService cosmos) =>
        new(cosmos, HttpFactory(), Options.Create(new DurableFunctionsSettings()), NullLogger<ChatService>.Instance);

    [Fact]
    public async Task ClearAsync_ResetsThreadBriefAndSetsBoundary()
    {
        var cosmos = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);
        await cosmos.CreateTaskAsync(new AgentTask {
            Id = "t1", BoardId = "b1", TenantId = "default", Title = "T",
            FoundryThreadId = "conv_x", TaskBrief = "old notes" });

        var ok = await Make(cosmos).ClearAsync("b1", "t1");

        Assert.True(ok);
        var after = await cosmos.GetTaskAsync("b1", "t1");
        Assert.Null(after!.FoundryThreadId);
        Assert.Equal("", after.TaskBrief);
        Assert.NotNull(after.ChatClearedAt);
    }

    [Fact]
    public async Task ClearAsync_ReturnsFalse_WhenTaskMissing() =>
        Assert.False(await Make(new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance)).ClearAsync("b1", "nope"));
}
