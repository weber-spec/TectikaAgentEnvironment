using System.Text.Json.Serialization;

namespace TectikaAgents.AgentRuntime;

/// <summary>Declarative catalog of the fixed Tectika agent tools. Attached to EVERY Foundry agent
/// definition (Foundry rejects per-request tools in agent_reference mode). Bump <see cref="Version"/>
/// whenever the toolset changes so AgentInstructionsHash republishes agent versions.</summary>
public static class TectikaToolSchema
{
    public const string Version = "tools-v1";

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

    /// <summary>Project the catalog into the Foundry flat function-tool array (definition.tools).</summary>
    public static IReadOnlyList<object> ToFoundryToolsJson() =>
        Definitions.Select(d => (object)new FoundryTool(
            "function", d.Name, d.Description,
            new FoundryParams("object",
                d.Properties.ToDictionary(p => p.Key, p => new FoundryProp(p.Value.Type, p.Value.Description, p.Value.Enum)),
                d.Required))).ToList();

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
