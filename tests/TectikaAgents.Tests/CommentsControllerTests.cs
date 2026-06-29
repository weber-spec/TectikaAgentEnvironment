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

    [Fact]
    public async Task Update_by_author_edits_body_and_stamps_editedBy()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");
        var created = (TaskComment)((OkObjectResult)await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "v1", null), default)).Value!;

        var res = await ctrl.Update("b1", "t1", created.Id, new UpdateCommentRequest("v2", null), default);
        var updated = (TaskComment)Assert.IsType<OkObjectResult>(res).Value!;
        Assert.Equal("v2", updated.Body);
        Assert.Equal("eli@tectika.com", updated.EditedBy);
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task Update_by_non_author_is_forbidden()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var author = NewController(cosmos, user: "eli@tectika.com");
        var created = (TaskComment)((OkObjectResult)await author.Create("b1", "t1",
            new CreateCommentRequest("message", null, "v1", null), default)).Value!;

        var other = NewController(cosmos, user: "maya@tectika.com");
        Assert.IsType<ForbidResult>(await other.Update("b1", "t1", created.Id, new UpdateCommentRequest("hack", null), default));
    }

    [Fact]
    public async Task Delete_by_author_soft_deletes()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");
        var created = (TaskComment)((OkObjectResult)await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "bye", null), default)).Value!;

        Assert.IsType<OkObjectResult>(await ctrl.Delete("b1", "t1", created.Id, default));
        var reloaded = await cosmos.GetCommentAsync("t1", created.Id);
        Assert.NotNull(reloaded!.DeletedAt);
    }

    [Fact]
    public async Task React_toggles_user_under_emoji()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");
        var c = (TaskComment)((OkObjectResult)await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "x", null), default)).Value!;

        var on = (TaskComment)((OkObjectResult)await ctrl.React("b1", "t1", c.Id, new ReactionRequest("👍"), default)).Value!;
        Assert.Contains("eli@tectika.com", on.Reactions["👍"]);

        var off = (TaskComment)((OkObjectResult)await ctrl.React("b1", "t1", c.Id, new ReactionRequest("👍"), default)).Value!;
        Assert.False(off.Reactions.ContainsKey("👍") && off.Reactions["👍"].Contains("eli@tectika.com"));
    }

    [Fact]
    public async Task Share_sets_flag_and_stamps_on_notes_only()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");
        var note = (TaskComment)((OkObjectResult)await ctrl.Create("b1", "t1",
            new CreateCommentRequest("note", "decision", "ship it", null), default)).Value!;

        var shared = (TaskComment)((OkObjectResult)await ctrl.Share("b1", "t1", note.Id, new ShareRequest(true), default)).Value!;
        Assert.True(shared.SharedWithAgent);
        Assert.Equal("eli@tectika.com", shared.SharedBy);
        Assert.NotNull(shared.SharedAt);
    }

    [Fact]
    public async Task Share_rejected_for_message_kind()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");
        var msg = (TaskComment)((OkObjectResult)await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "not a note", null), default)).Value!;

        Assert.IsType<BadRequestObjectResult>(await ctrl.Share("b1", "t1", msg.Id, new ShareRequest(true), default));
    }

    [Fact]
    public async Task MarkRead_records_marker_for_task()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var ctrl = NewController(cosmos, user: "eli@tectika.com");

        var res = Assert.IsType<OkObjectResult>(await ctrl.MarkRead("b1", "t1", default));
        Assert.NotNull(res.Value);
    }

    [Fact]
    public async Task Create_with_mentions_saves_targeted_notifications()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var notifs = new TestNotificationRepo();
        var ctrl = new CommentsController(cosmos, new TestUserSettingsRepo(), notifs, NullLogger<CommentsController>.Instance);
        ctrl.ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tid", "default"),
                    new Claim("preferred_username", "eli@tectika.com"),
                }, "Test"))
            }
        };

        await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "ping @maya", new List<string> { "maya@tectika.com" }), default);

        var n = Assert.Single(notifs.Saved);
        Assert.Equal("maya@tectika.com", n.RecipientUserId);
        Assert.Equal("default", n.TenantId);
        Assert.Equal("t1", n.TaskId);
        Assert.Equal("mention", n.Type);
    }

    [Fact]
    public async Task Create_does_not_notify_self_mention()
    {
        var cosmos = NewStore();
        await SeedTask(cosmos, "b1", "t1");
        var notifs = new TestNotificationRepo();
        var ctrl = new CommentsController(cosmos, new TestUserSettingsRepo(), notifs, NullLogger<CommentsController>.Instance);
        ctrl.ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tid", "default"),
                    new Claim("preferred_username", "eli@tectika.com"),
                }, "Test"))
            }
        };

        await ctrl.Create("b1", "t1",
            new CreateCommentRequest("message", null, "note to self @eli", new List<string> { "eli@tectika.com" }), default);

        Assert.Empty(notifs.Saved);
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
