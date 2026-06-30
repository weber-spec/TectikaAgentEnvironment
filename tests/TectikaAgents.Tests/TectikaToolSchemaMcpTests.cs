using System;
using System.Collections.Generic;
using System.Text.Json;
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

public class TectikaToolSchemaMcpTests
{
    [Fact]
    public void Mcp_enabled_role_gets_read_tools_qualified()
    {
        var tools = TectikaToolSchema.ToFoundryToolsJson(
            new AgentPermissions(), github: null,
            mcpEnabled: new[] { "slack" }, mcpWriteEnabled: Array.Empty<string>());
        var names = ToolNames(tools);
        Assert.Contains("slack__list_channels", names);
        Assert.DoesNotContain("slack__post_message", names); // write not opted in
    }

    [Fact]
    public void Mcp_write_optin_adds_write_tools()
    {
        var tools = TectikaToolSchema.ToFoundryToolsJson(
            new AgentPermissions(), github: null,
            mcpEnabled: new[] { "slack" }, mcpWriteEnabled: new[] { "slack" });
        var names = ToolNames(tools);
        Assert.Contains("slack__list_channels", names);
        Assert.Contains("slack__post_message", names);
    }

    [Fact]
    public void First_party_email_send_tool_is_projected_only_with_write_optin()
    {
        var readOnly = ToolNames(TectikaToolSchema.ToFoundryToolsJson(
            new AgentPermissions(), github: null,
            mcpEnabled: new[] { "email" }, mcpWriteEnabled: Array.Empty<string>()));
        Assert.DoesNotContain("email__send_email", readOnly); // send_email is a write tool

        var withWrite = ToolNames(TectikaToolSchema.ToFoundryToolsJson(
            new AgentPermissions(), github: null,
            mcpEnabled: new[] { "email" }, mcpWriteEnabled: new[] { "email" }));
        Assert.Contains("email__send_email", withWrite);
    }

    [Fact]
    public void Mcp_unknown_id_is_ignored()
    {
        var tools = TectikaToolSchema.ToFoundryToolsJson(
            new AgentPermissions(), github: null,
            mcpEnabled: new[] { "nope" }, mcpWriteEnabled: Array.Empty<string>());
        Assert.DoesNotContain(ToolNames(tools), n => n.StartsWith("nope__"));
    }

    private static List<string> ToolNames(IReadOnlyList<object> tools)
    {
        var json = JsonSerializer.Serialize(tools);
        using var doc = JsonDocument.Parse(json);
        var list = new List<string>();
        foreach (var el in doc.RootElement.EnumerateArray())
            if (el.TryGetProperty("name", out var n)) list.Add(n.GetString()!);
        return list;
    }
}
