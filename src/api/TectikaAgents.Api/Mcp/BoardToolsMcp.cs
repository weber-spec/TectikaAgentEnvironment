using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TectikaAgents.AgentRuntime;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Core.Mcp;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Api.Mcp;

/// <summary>Serves the Tectika board tools over MCP to Claude Code agents. It mirrors the tool semantics of
/// <c>RoundExecutor.ExecuteOneRoundAsync</c> (the Foundry path) but drives them from a per-run token instead
/// of a model tool-call loop: reads go through a board-scoped <see cref="BoardProjectExplorer"/>, outputs are
/// applied with <see cref="OutputAccumulator"/> and persisted to <c>task.PendingOutputs</c> (so the round's
/// Artifact carries them, identical to Foundry), and integration tools reuse the shared
/// <see cref="McpToolExecutor"/>. Every call is scoped to the token's board/task/tenant — never to arguments.</summary>
public sealed class BoardToolsMcp
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly McpToolExecutor _mcp;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<BoardToolsMcp> _logger;

    public BoardToolsMcp(WorkflowCosmosService cosmos, McpToolExecutor mcp,
        IHttpContextAccessor http, ILogger<BoardToolsMcp> logger)
    {
        _cosmos = cosmos; _mcp = mcp; _http = http; _logger = logger;
    }

    private RunContext RequireCtx() =>
        _http.HttpContext?.Items[McpRunAuthHandler.RunContextItem] as RunContext
        ?? throw new InvalidOperationException("No MCP run context on the request.");

    // ── tools/list ──────────────────────────────────────────────────────────────
    public async Task<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> _, CancellationToken ct)
    {
        var ctx = RequireCtx();
        var role = await _cosmos.GetAgentRoleAsync(ctx.TenantId, ctx.RoleId, ct);
        var defs = role is null
            ? Array.Empty<TectikaToolSchema.ToolDef>()
            : TectikaToolSchema.McpBoardTools(role);
        return new ListToolsResult { Tools = defs.Select(ToMcpTool).ToList() };
    }

    // ── tools/call ──────────────────────────────────────────────────────────────
    public async Task<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> req, CancellationToken ct)
    {
        var ctx = RequireCtx();
        var name = req.Params?.Name ?? "";
        var args = req.Params?.Arguments is { } a
            ? JsonSerializer.SerializeToElement(a)
            : JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        try
        {
            var text = await DispatchAsync(ctx, name, args, ct);
            return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BoardToolsMcp] tool {Tool} failed for run {RunId}", name, ctx.RunId);
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"error: {ex.Message}" }] };
        }
    }

    private async Task<string> DispatchAsync(RunContext ctx, string name, JsonElement args, CancellationToken ct)
    {
        var explorer = new BoardProjectExplorer(_cosmos, ctx.BoardId, ctx.TenantId);
        switch (name)
        {
            case "get_board_overview": return Json(await explorer.GetBoardOverviewAsync(ct));
            case "search_tasks":       return Json(await explorer.SearchTasksAsync(Str(args, "query"), ct));
            case "get_task":           return Json(await explorer.GetTaskAsync(Str(args, "taskId"), ct));
            case "get_artifact":       return Json(await explorer.GetArtifactAsync(Str(args, "taskId"), IntOrNull(args, "version"), ct));
            case "read_team_notes":    return Json(await explorer.GetSharedNotesAsync(Str(args, "taskId"), ct));
            case "update_brief":       await UpdateBriefAsync(ctx, Str(args, "text"), ct); return "ok";
            case "declare_output":     return await ApplyOutputAsync(ctx, DeclareOp(args, await BoardRepoAsync(ctx, ct)), ct);
            case "update_output":      return await ApplyOutputAsync(ctx, UpdateOp(args), ct);
            case "remove_output":      return await ApplyOutputAsync(ctx, new OutputOp(OutputOpKind.Remove, Str(args, "id")), ct);
            default:
                if (_mcp.CanHandle(name))
                {
                    var board = await _cosmos.GetBoardAsync(ctx.BoardId, ctx.TenantId, ct);
                    var role = await _cosmos.GetAgentRoleAsync(ctx.TenantId, ctx.RoleId, ct);
                    var conns = board is not null && role is not null
                        ? ConnectionResolver.Effective(role, board, await _cosmos.GetConnectionsAsync(ctx.TenantId, ct))
                        : (IReadOnlyList<Connection>)Array.Empty<Connection>();
                    return await _mcp.ExecuteAsync(name, args, conns, role, ct);
                }
                return $"Unknown tool '{name}'.";
        }
    }

    // declare_output returns its new id; update/remove return "ok".
    private (OutputOp Op, string Reply) DeclareOp(JsonElement args, GitHubRepoConnection? repo)
    {
        var id = Guid.NewGuid().ToString();
        var declared = new Output
        {
            Id = id,
            Kind = OutputKind.Document,
            Label = StrOrNull(args, "label"),
            Inline = new InlineContent { ContentType = ParseContentType(StrOrNull(args, "contentType")), Content = Str(args, "content") },
            Links = FileLinks(args, repo),
        };
        return (new OutputOp(OutputOpKind.Declare, id, Declared: declared), $"{{\"id\":\"{id}\"}}");
    }

    private static (OutputOp Op, string Reply) UpdateOp(JsonElement args)
    {
        var newContent = StrOrNull(args, "content");
        InlineContent? inline = newContent is null ? null
            : new InlineContent { ContentType = ParseContentType(StrOrNull(args, "contentType")), Content = newContent };
        return (new OutputOp(OutputOpKind.Update, Str(args, "id"), Label: StrOrNull(args, "label"), Inline: inline), "ok");
    }

    private Task<string> ApplyOutputAsync(RunContext ctx, OutputOp op, CancellationToken ct)
        => ApplyOutputAsync(ctx, (op, "ok"), ct);

    // Read current PendingOutputs, fold the op through the SAME reducer the Foundry path uses, persist.
    // Claude's tool calls are sequential within a run, so read-modify-write is safe here.
    private async Task<string> ApplyOutputAsync(RunContext ctx, (OutputOp Op, string Reply) opReply, CancellationToken ct)
    {
        var task = await _cosmos.GetTaskAsync(ctx.BoardId, ctx.TaskId, ct);
        var updated = OutputAccumulator.Apply(task?.PendingOutputs ?? [], new[] { opReply.Op });
        await _cosmos.PatchTaskPendingOutputsAsync(ctx.BoardId, ctx.TaskId, updated, ct);
        return opReply.Reply;
    }

    private async Task UpdateBriefAsync(RunContext ctx, string text, CancellationToken ct)
    {
        var task = await _cosmos.GetTaskAsync(ctx.BoardId, ctx.TaskId, ct);
        var brief = $"{task?.TaskBrief ?? ""}\n[{ctx.RoleId}, {Short(ctx.RunId)}]: {text}";
        await _cosmos.PatchTaskBriefAsync(ctx.BoardId, ctx.TaskId, brief, ct);
    }

    private async Task<GitHubRepoConnection?> BoardRepoAsync(RunContext ctx, CancellationToken ct)
        => (await _cosmos.GetBoardAsync(ctx.BoardId, ctx.TenantId, ct))?.GitHub;

    // ── helpers (mirror RoundExecutor) ────────────────────────────────────────────
    private static Tool ToMcpTool(TectikaToolSchema.ToolDef d)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = d.Properties.ToDictionary(p => p.Key, p => (object)BuildProp(p.Value)),
            ["required"] = d.Required,
        };
        return new Tool { Name = d.Name, Description = d.Description, InputSchema = JsonSerializer.SerializeToElement(schema) };
    }

    private static Dictionary<string, object?> BuildProp(TectikaToolSchema.ToolProp p)
    {
        var m = new Dictionary<string, object?> { ["type"] = p.Type };
        if (p.Description is not null) m["description"] = p.Description;
        if (p.Enum is not null) m["enum"] = p.Enum;
        return m;
    }

    private static List<FileLink> FileLinks(JsonElement e, GitHubRepoConnection? repo)
    {
        var paths = StrArr(e, "files");
        if (paths is null) return [];
        var source = repo is not null ? FileLinkSource.Repo : FileLinkSource.Workspace;
        return paths.Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => new FileLink { Path = p.Trim(), Source = source }).ToList();
    }

    private static ArtifactContentType ParseContentType(string? s) =>
        Enum.TryParse<ArtifactContentType>(s, ignoreCase: true, out var v) ? v : ArtifactContentType.Markdown;

    private static string Json(object? value) => JsonSerializer.Serialize(value);
    private static string Short(string s) => s.Length <= 8 ? s : s[..8];

    private static bool Obj(JsonElement e, string p, out JsonElement v)
    {
        v = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out v);
    }
    private static string Str(JsonElement e, string p) => Obj(e, p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static string? StrOrNull(JsonElement e, string p) => Obj(e, p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? IntOrNull(JsonElement e, string p) => Obj(e, p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static List<string>? StrArr(JsonElement e, string p) =>
        Obj(e, p, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : null;
}
