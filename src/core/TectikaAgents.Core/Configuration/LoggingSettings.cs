namespace TectikaAgents.Core.Configuration;

/// <summary>
/// Bound from the "Logging" configuration section. Controls whether sensitive content
/// (request/response bodies, agent prompts, model outputs) is written to logs.
/// Defaults to true (log everything); flip to false for production via
/// the Logging__LogSensitiveContent environment variable.
/// </summary>
public class LoggingSettings
{
    public bool LogSensitiveContent { get; set; } = true;
}
