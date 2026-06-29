using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class TaskCommentsStoreTests
{
    private static InMemoryCosmosDbService NewStore() =>
        new(NullLogger<InMemoryCosmosDbService>.Instance);

    [Fact]
    public async Task Create_then_GetByTask_returns_ordered_comments()
    {
        var store = NewStore();
        await store.CreateCommentAsync(new TaskComment
        {
            TaskId = "t1", BoardId = "b1", TenantId = "default",
            Kind = "message", AuthorId = "eli@tectika.com", Body = "first",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        });
        await store.CreateCommentAsync(new TaskComment
        {
            TaskId = "t1", BoardId = "b1", TenantId = "default",
            Kind = "message", AuthorId = "maya@tectika.com", Body = "second",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await store.CreateCommentAsync(new TaskComment
        {
            TaskId = "t2", BoardId = "b1", TenantId = "default",
            Kind = "message", AuthorId = "eli@tectika.com", Body = "other task"
        });

        var list = await store.GetCommentsByTaskAsync("t1");

        Assert.Equal(2, list.Count);
        Assert.Equal("first", list[0].Body);
        Assert.Equal("second", list[1].Body);
    }

    [Fact]
    public async Task GetComment_scoped_to_task_partition()
    {
        var store = NewStore();
        var c = await store.CreateCommentAsync(new TaskComment { TaskId = "t1", Body = "x" });

        Assert.NotNull(await store.GetCommentAsync("t1", c.Id));
        Assert.Null(await store.GetCommentAsync("tWRONG", c.Id));
    }

    [Fact]
    public async Task Upsert_replaces_existing()
    {
        var store = NewStore();
        var c = await store.CreateCommentAsync(new TaskComment { TaskId = "t1", Body = "old" });
        c.Body = "new";
        await store.UpsertCommentAsync(c);

        var reloaded = await store.GetCommentAsync("t1", c.Id);
        Assert.Equal("new", reloaded!.Body);
    }
}
