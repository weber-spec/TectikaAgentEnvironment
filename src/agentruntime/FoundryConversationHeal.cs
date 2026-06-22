using System.Text.RegularExpressions;

namespace TectikaAgents.AgentRuntime;

/// <summary>
/// Helpers for recovering a Foundry conversation that was left awaiting a tool output.
///
/// Background: the Responses API requires that every <c>function_call</c> the model emits is
/// answered by a matching <c>function_call_output</c> before any further input is accepted. If a
/// turn ends with an unanswered call (control-tool pause, max-rounds, or a crash), the persistent
/// per-task conversation is "stuck", and the next turn that posts into it fails with
/// HTTP 400 <c>invalid_request_error</c>: "No tool output found for function call call_…".
/// </summary>
public static class FoundryConversationHeal
{
    // Matches the dangling call id in the Foundry 400 body, e.g.
    //   "No tool output found for function call call_oiv3mfXoOwiP2LOP9E7HMT6E."
    private static readonly Regex MissingToolOutput = new(
        @"No tool output found for function call\s+(call_[A-Za-z0-9_\-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns the dangling <c>call_id</c> referenced by a Foundry "No tool output found" 400 body,
    /// or <c>null</c> when the body is not that error (so callers leave other failures untouched).
    /// </summary>
    public static string? ParseMissingToolCallId(string? errorBody)
    {
        if (string.IsNullOrEmpty(errorBody)) return null;
        var m = MissingToolOutput.Match(errorBody);
        return m.Success ? m.Groups[1].Value : null;
    }
}
