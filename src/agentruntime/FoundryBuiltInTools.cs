namespace TectikaAgents.AgentRuntime;

/// <summary>A built-in tool TYPE the Foundry Agent Service offers (web search, code interpreter, …). These are
/// a fixed platform catalog — Foundry has no "list tool types" endpoint — so we describe them statically.
/// <see cref="RequiresProjectConnection"/> marks tools that need a Foundry project connection or data source
/// (e.g. web_search → a Bing grounding connection; file_search / azure_ai_search → a vector store / index).</summary>
public sealed record FoundryBuiltInTool(string Id, string Name, string Description, bool RequiresProjectConnection);

public static class FoundryBuiltInTools
{
    public static readonly IReadOnlyList<FoundryBuiltInTool> All =
    [
        new("code_interpreter", "Code Interpreter", "Run code in a sandbox to compute, analyze, and chart.", false),
        new("web_search",       "Web Search",       "Ground answers in live web results (Bing grounding).", true),
        new("file_search",      "File Search",      "Retrieve over files/vector stores attached to the agent.", true),
        new("azure_ai_search",  "Azure AI Search",  "Query an Azure AI Search index as a knowledge source.", true),
        new("function",         "Functions",        "Call your own functions / OpenAPI tools.", false),
        new("mcp",              "MCP Servers",      "Connect remote MCP tool servers to the agent.", false),
    ];

    /// <summary>The subset we currently support attaching to an agent from the UI (Part 4). code_interpreter
    /// is no-config; web_search needs a Bing grounding connection in the project.</summary>
    public static readonly IReadOnlyList<string> AgentSelectable = ["code_interpreter", "web_search"];

    public static FoundryBuiltInTool? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}
