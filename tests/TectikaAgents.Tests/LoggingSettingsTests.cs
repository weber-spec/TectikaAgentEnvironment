using Microsoft.Extensions.Configuration;
using TectikaAgents.Core.Configuration;
using Xunit;

namespace TectikaAgents.Tests;

public class LoggingSettingsTests
{
    [Fact]
    public void LogSensitiveContent_DefaultsToTrue()
    {
        Assert.True(new LoggingSettings().LogSensitiveContent);
    }

    [Fact]
    public void Binds_LogSensitiveContent_False_FromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogSensitiveContent"] = "false",
            })
            .Build();

        var settings = config.GetSection("Logging").Get<LoggingSettings>()!;
        Assert.False(settings.LogSensitiveContent);
    }
}
