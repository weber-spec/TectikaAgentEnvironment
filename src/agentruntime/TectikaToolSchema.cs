using System.Text.Json.Serialization;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>Declarative catalog of the fixed Tectika agent tools. Attached to EVERY Foundry agent
/// definition (Foundry rejects per-request tools in agent_reference mode). Bump <see cref="Version"/>
/// whenever the toolset changes so AgentInstructionsHash republishes agent versions.</summary>
public static class TectikaToolSchema
{
    public const string Version = "tools-v4";

    public sealed record ToolProp(string Type, string? Description = null, string[]? Enum = null);
    public sealed record ToolDef(
        string Name, string Description,
        IReadOnlyDictionary<string, ToolProp> Properties, string[] Required);

    public static readonly IReadOnlyList<ToolDef> Definitions = new ToolDef[]
    {
        // ── Explore (board-scoped, read-only) ──────────────────────────────────
        new("get_board_overview", "List every task on this board with id, title, status, assignee, and dependency edges. Use this first to understand the whole project.",
            new Dictionary<string, ToolProp>(), []),
        new("search_tasks", "Find tasks on this board whose title/description/brief match a query.",
            new Dictionary<string, ToolProp> { ["query"] = new("string", "Free-text search terms.") }, ["query"]),
        new("get_task", "Get one task's details (title, description, status, brief, current artifact summary).",
            new Dictionary<string, ToolProp> { ["taskId"] = new("string", "The task id.") }, ["taskId"]),
        new("get_artifact", "Get the full artifact content produced by a task (latest version unless one is given).",
            new Dictionary<string, ToolProp> {
                ["taskId"] = new("string", "The task whose artifact to read."),
                ["version"] = new("integer", "Optional specific version; omit for latest.") }, ["taskId"]),
        // ── Control (signal the orchestrator) ──────────────────────────────────
        new("round_intent", "Announce, in one short line, what you are about to do this round. Call this at the START of each round.",
            new Dictionary<string, ToolProp> { ["text"] = new("string", "One-line intent, e.g. 'Gathering data about the project'.") }, ["text"]),
        new("update_brief", "Append a one-line note to this task's running brief (history visible to downstream tasks).",
            new Dictionary<string, ToolProp> { ["text"] = new("string", "One-line brief update.") }, ["text"]),
        new("request_human_input", "Pause and ask the human a question (free-text, or multiple-choice if options given). Only when you genuinely cannot proceed.",
            new Dictionary<string, ToolProp> {
                ["question"] = new("string", "The question to ask."),
                ["options"] = new("array", "Optional choices the user picks from.") }, ["question"]),
        new("request_approval", "Pause and ask the human to approve/reject before continuing.",
            new Dictionary<string, ToolProp> { ["description"] = new("string", "What needs approval.") }, ["description"]),
        new("request_revision", "(QA/validator agents) Signal that an upstream task must be re-run with fixes.",
            new Dictionary<string, ToolProp> { ["reason"] = new("string", "What must be fixed.") }, ["reason"]),
    };

    // ── GitHub tools (appended per agent permissions) ─────────────────────────
    private static readonly IReadOnlyList<(ToolDef Def, Func<GitHubPermissions, bool> Allowed)> GitHubTools =
    [
        (new("github_read_file",
            "Read the content of a file from the connected GitHub repository.",
            new Dictionary<string, ToolProp> {
                ["path"] = new("string", "File path relative to repo root, e.g. 'src/main.cs'."),
                ["branch"] = new("string", "Branch name. Defaults to the repo default branch if omitted.") },
            ["path"]),
         gh => gh.CanRead),

        (new("github_list_files",
            "List files and directories at a path in the connected GitHub repository.",
            new Dictionary<string, ToolProp> {
                ["path"] = new("string", "Directory path, e.g. 'src/'. Use '' or '/' for root."),
                ["branch"] = new("string", "Branch name. Defaults to the repo default branch if omitted.") },
            ["path"]),
         gh => gh.CanRead),

        (new("github_create_branch",
            "Create a new branch in the connected GitHub repository.",
            new Dictionary<string, ToolProp> {
                ["branch"] = new("string", "Name for the new branch."),
                ["from"] = new("string", "Base branch or commit SHA to branch from. Defaults to default branch.") },
            ["branch"]),
         gh => gh.CanCreateBranch),

        (new("github_push_file",
            "Create or update a file on a branch in the connected GitHub repository.",
            new Dictionary<string, ToolProp> {
                ["path"] = new("string", "File path relative to repo root."),
                ["content"] = new("string", "Full UTF-8 file content."),
                ["branch"] = new("string", "Target branch name."),
                ["message"] = new("string", "Commit message.") },
            ["path", "content", "branch", "message"]),
         gh => gh.CanPush),

        (new("github_create_pr",
            "Open a pull request in the connected GitHub repository.",
            new Dictionary<string, ToolProp> {
                ["title"] = new("string", "PR title."),
                ["body"] = new("string", "PR description (markdown)."),
                ["head"] = new("string", "Source branch name."),
                ["base"] = new("string", "Target branch name (e.g. 'main').") },
            ["title", "head", "base"]),
         gh => gh.CanCreatePr),
    ];

    // ── Workspace tools (appended when an ACI workspace is provisioned for the run) ──
    private static readonly ToolDef RunCommandTool = new(
        "run_command",
        "Run a bash shell command in the workspace (the cloned git repo). " +
        "Returns stdout, stderr, and exit_code. " +
        "The working directory is the repo root. " +
        "Use this to read/write files, run builds (dotnet build, npm install), execute tests, git commit, git push, etc. " +
        "Prefer small focused commands; chain them with &&. For file edits prefer 'cat > path <<EOF ... EOF'.",
        new Dictionary<string, ToolProp>
        {
            ["cmd"] = new("string", "The bash command to run, e.g. 'dotnet build src/MyProject.csproj'."),
            ["timeout"] = new("integer", "Max seconds to wait (default 60, max 300).")
        },
        ["cmd"]);

    /// <summary>Project the catalog into the Foundry flat function-tool array (definition.tools).
    /// Pass <paramref name="github"/> to append the permitted GitHub tools.
    /// Pass <paramref name="hasWorkspace"/> true to append the run_command tool.</summary>
    public static IReadOnlyList<object> ToFoundryToolsJson(GitHubPermissions? github = null, bool hasWorkspace = false)
    {
        var tools = Definitions.Select(d => (object)ToFoundryTool(d)).ToList();
        if (github is not null)
            tools.AddRange(GitHubTools
                .Where(t => t.Allowed(github))
                .Select(t => (object)ToFoundryTool(t.Def)));
        if (hasWorkspace)
            tools.Add(ToFoundryTool(RunCommandTool));
        return tools;
    }

    private static FoundryTool ToFoundryTool(ToolDef d) => new(
        "function", d.Name, d.Description,
        new FoundryParams("object",
            d.Properties.ToDictionary(p => p.Key, p => new FoundryProp(p.Value.Type, p.Value.Description, p.Value.Enum)),
            d.Required));

    // Foundry wire shapes (flat function tool).
    private sealed record FoundryTool(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] FoundryParams Parameters);
    private sealed record FoundryParams(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, FoundryProp> Properties,
        [property: JsonPropertyName("required")] string[] Required);
    private sealed record FoundryProp(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("enum")] string[]? Enum);
}
