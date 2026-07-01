namespace TectikaAgents.Core.Mcp;

/// <summary>Identity of a single agent run. Carried inside the per-run MCP token so the API's board-tools
/// endpoint can scope every tool call to the correct board/task/tenant/role WITHOUT trusting arguments the
/// model passes (a prompt-injected taskId must not be able to reach another board).</summary>
public sealed record RunContext(
    string RunId,
    string TaskId,
    string BoardId,
    string TenantId,
    string RoleId);
