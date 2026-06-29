using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Controllers;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class CommentsControllerTests
{
    private static InMemoryCosmosDbService NewStore() => new(NullLogger<InMemoryCosmosDbService>.Instance);

    private static CommentsController NewController(ICosmosDbService cosmos, string user = "eli@tectika.com", string tenant = "default")
    {
        var ctrl = new CommentsController(cosmos,
            new TestUserSettingsRepo(), new TestNotificationRepo(),
            NullLogger<CommentsController>.Instance);
        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tid", tenant),
            new Claim("preferred_username", user),
        }, "Test"));
        ctrl.ControllerContext = new() { HttpContext = new DefaultHttpContext { User = claims } };
        return ctrl;
    }

    private static async Task SeedTask(ICosmosDbService cosmos, string boardId, string taskId, string tenant = "default") =>
        await cosmos.CreateTaskAsync(new AgentTask { Id = taskId, BoardId = boardId, TenantId = tenant, Title = "T" });

    [Fact]
    public async Task Create_then_List_returns_the_comment()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos);

        var created = await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "  hello team  ", null), default);
        Assert.IsType<OkObjectResult>(created);

        var list = Assert.IsType<OkObjectResult>(await ctrl.List("b1", "t1", default));
        var comments = Assert.IsAssignableFrom<IReadOnlyList<TaskComment>>(list.Value);
        Assert.Single(comments);
        Assert.Equal("hello team", comments[0].Body);
        Assert.Equal("eli@tectika.com", comments[0].AuthorId);
        Assert.Equal("default", comments[0].TenantId);
    }

    [Fact]
    public async Task Create_rejects_blank_body()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos);
        var res = await ctrl.Create("b1", "t1", new CreateCommentRequest("message", null, "   ", null), default);
        Assert.IsType<BadRequestObjectResult>(res);
    }

    [Fact]
    public async Task Create_rejects_invalid_kind()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos);
        var res = await ctrl.Create("b1", "t1", new CreateCommentRequest("banana", null, "x", null), default);
        Assert.IsType<BadRequestObjectResult>(res);
    }

    [Fact]
    public async Task List_returns_NotFound_for_cross_tenant_task()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1", tenant: "otherTenant");
        var ctrl = NewController(cosmos, tenant: "default");
        Assert.IsType<NotFoundObjectResult>(await ctrl.List("b1", "t1", default));
    }

    private sealed class TestUserSettingsRepo : UserSettingsRepository
    {
        public TestUserSettingsRepo() : base(NullLogger<UserSettingsRepository>.Instance) { }
        private readonly Dictionary<string, UserSettingsDocument> _docs = new();
        public override Task<UserSettingsDocument> GetOrCreateAsync(string userId, CancellationToken ct = default)
        {
            if (!_docs.TryGetValue(userId, out var d)) { d = new UserSettingsDocument { UserId = userId }; _docs[userId] = d; }
            return Task.FromResult(d);
        }
        public override Task UpsertAsync(UserSettingsDocument doc, CancellationToken ct = default)
        { _docs[doc.UserId] = doc; return Task.CompletedTask; }
    }

    private sealed class TestNotificationRepo : NotificationRepository
    {
        public TestNotificationRepo() : base(NullLogger<NotificationRepository>.Instance) { }
        public readonly List<NotificationDocument> Saved = new();
        public override Task SaveAsync(NotificationDocument doc, CancellationToken ct = default)
        { Saved.Add(doc); return Task.CompletedTask; }
    }
}
