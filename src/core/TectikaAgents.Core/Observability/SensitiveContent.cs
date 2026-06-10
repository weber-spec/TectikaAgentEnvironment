namespace TectikaAgents.Core.Observability;

/// <summary>
/// Single gate for sensitive log content. Pass the resolved LogSensitiveContent flag and the
/// raw value; when logging is disabled, only a redaction marker (with length) is returned so
/// the field stays queryable without leaking content.
/// </summary>
public static class SensitiveContent
{
    public const string RedactedPlaceholder = "[redacted]";

    public static string Format(string? content, bool logSensitive)
    {
        if (logSensitive) return content ?? string.Empty;
        if (string.IsNullOrEmpty(content)) return RedactedPlaceholder;
        return $"{RedactedPlaceholder}({content.Length} chars)";
    }
}
