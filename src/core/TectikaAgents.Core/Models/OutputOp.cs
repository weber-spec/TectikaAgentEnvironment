namespace TectikaAgents.Core.Models;

/// <summary>An edit to a task's declared output set, produced by the
/// declare_output / update_output / remove_output tools.</summary>
public enum OutputOpKind { Declare, Update, Remove }

/// <summary>One declare/update/remove instruction. <see cref="Id"/> is the new
/// output's id (Declare) or the target output's id (Update/Remove). For Update,
/// a null <see cref="Label"/> or <see cref="Inline"/> means "leave unchanged".</summary>
public sealed record OutputOp(
    OutputOpKind Kind,
    string Id,
    Output? Declared = null,
    string? Label = null,
    InlineContent? Inline = null);
