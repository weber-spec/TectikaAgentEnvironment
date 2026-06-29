namespace TectikaAgents.Api.Controllers;

public record CreateCommentRequest(string Kind, string? NoteType, string Body, List<string>? Mentions);
public record UpdateCommentRequest(string Body, string? NoteType);
public record ReactionRequest(string Emoji);
public record ShareRequest(bool Shared);
