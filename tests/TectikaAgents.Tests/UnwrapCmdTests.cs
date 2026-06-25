using TectikaAgents.AgentRuntime.Workspace;
using Xunit;

namespace TectikaAgents.Tests;

/// <summary>QA S3 §4.4 — run_command must run (and log) the real command even when a model double-wraps
/// it as cmd="{\"cmd\":\"...\"}".</summary>
public class UnwrapCmdTests
{
    [Fact] public void PlainCommand_IsUnchanged() =>
        Assert.Equal("dotnet build", WorkspaceToolExecutor.UnwrapCmd("dotnet build"));

    [Fact] public void DoubleWrapped_IsUnwrapped() =>
        Assert.Equal("dotnet run --project App",
            WorkspaceToolExecutor.UnwrapCmd("{\"cmd\":\"dotnet run --project App\"}"));

    [Fact] public void DoubleWrapped_WithLeadingWhitespace_IsUnwrapped() =>
        Assert.Equal("ls -la", WorkspaceToolExecutor.UnwrapCmd("  {\"cmd\": \"ls -la\"}"));

    [Fact] public void JsonObjectWithoutCmd_IsLeftAsIs() =>
        Assert.Equal("{\"foo\":\"bar\"}", WorkspaceToolExecutor.UnwrapCmd("{\"foo\":\"bar\"}"));

    [Fact] public void MalformedJson_IsLeftAsIs() =>
        Assert.Equal("{not json", WorkspaceToolExecutor.UnwrapCmd("{not json"));

    [Fact] public void CommandThatHappensToStartWithBrace_IsLeftAsIs() =>
        Assert.Equal("{ echo hi; }", WorkspaceToolExecutor.UnwrapCmd("{ echo hi; }"));

    [Fact] public void Empty_IsUnchanged() =>
        Assert.Equal("", WorkspaceToolExecutor.UnwrapCmd(""));
}
