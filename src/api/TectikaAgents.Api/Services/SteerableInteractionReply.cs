using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>Renders a resolved steerable <see cref="HumanInteraction"/> into the natural-language text
/// fed back to the agent as the control tool's output. The steerable loop resumes on a
/// <c>user_message</c> string, so the structured decision is flattened to a clear sentence.</summary>
public static class SteerableInteractionReply
{
    public static string Render(HumanInteraction i) => i.Type switch
    {
        InteractionType.Approval => (i.Approved == true ? "Approved." : "Rejected.")
            + (string.IsNullOrWhiteSpace(i.Notes) ? "" : " " + i.Notes.Trim()),
        InteractionType.Selection => SelectedTitle(i) ?? i.Answer ?? "",
        _ => i.Answer ?? "",
    };

    private static string? SelectedTitle(HumanInteraction i) =>
        i.SelectedIndex is int idx && i.Items is { } items && idx >= 0 && idx < items.Count
            ? items[idx].Title
            : null;
}
