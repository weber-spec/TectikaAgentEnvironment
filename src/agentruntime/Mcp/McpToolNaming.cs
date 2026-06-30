namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>Tool names exposed to the model are `{catalogId}__{toolName}` so MCP tools never collide
/// with built-in tools or each other. The separator is the FIRST "__"; the tool name keeps any others.</summary>
public static class McpToolNaming
{
    public const string Separator = "__";

    public static string Qualify(string catalogId, string toolName) => $"{catalogId}{Separator}{toolName}";

    public static bool TryParse(string qualified, out string catalogId, out string toolName)
    {
        catalogId = toolName = string.Empty;
        var i = qualified.IndexOf(Separator, StringComparison.Ordinal);
        if (i <= 0 || i + Separator.Length >= qualified.Length) return false;
        catalogId = qualified[..i];
        toolName = qualified[(i + Separator.Length)..];
        return true;
    }
}
