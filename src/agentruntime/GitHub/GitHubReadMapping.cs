using System.Text;

namespace TectikaAgents.AgentRuntime.GitHub;

/// <summary>Pure helpers for mapping raw GitHub blob data to read DTOs.</summary>
public static class GitHubReadMapping
{
    public const long MaxTextBytes = 1_000_000; // 1 MB: above this we treat as binary (show "view on GitHub")

    /// <summary>Decode a base64 blob into (isBinary, text). Binary when it exceeds the size
    /// threshold or contains a NUL byte; text is null in that case. Null/empty encoded → ("", not binary).</summary>
    public static (bool IsBinary, string? Text) DecodeBlob(string? encodedContent, long size)
    {
        if (string.IsNullOrEmpty(encodedContent)) return (false, "");
        if (size > MaxTextBytes) return (true, null);

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encodedContent); }
        catch (FormatException) { return (false, encodedContent); } // already-decoded text

        if (bytes.Length > MaxTextBytes) return (true, null);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return (true, null);
        return (false, Encoding.UTF8.GetString(bytes));
    }
}
