using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Background service שמאזין ל-Service Bus ומעביר events ל-SSE clients.
/// </summary>
public class ServiceBusListenerService : BackgroundService
{
    private readonly SseConnectionManager _sse;
    private readonly ServiceBusSettings _settings;
    private readonly ILogger<ServiceBusListenerService> _logger;
    private ServiceBusProcessor? _processor;

    public ServiceBusListenerService(
        SseConnectionManager sse,
        IOptions<ServiceBusSettings> settings,
        ILogger<ServiceBusListenerService> logger)
    {
        _sse = sse;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Skip if not configured (dev without Service Bus)
        if (string.IsNullOrEmpty(_settings.Namespace) || _settings.Namespace.StartsWith("__"))
        {
            _logger.LogWarning("Service Bus not configured — SSE events will not stream from agents.");
            return;
        }

        var client = new ServiceBusClient(_settings.Namespace, new DefaultAzureCredential());
        _processor = client.CreateProcessor(_settings.AgentEventsTopic, _settings.AgentEventsSubscription, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 10,
            AutoCompleteMessages = false
        });

        _processor.ProcessMessageAsync += OnMessageAsync;
        _processor.ProcessErrorAsync += OnErrorAsync;

        await _processor.StartProcessingAsync(stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        try
        {
            var agentEvent = JsonSerializer.Deserialize<AgentEvent>(args.Message.Body.ToString());
            if (agentEvent is not null)
                await _sse.BroadcastAsync(agentEvent, args.CancellationToken);

            await args.CompleteMessageAsync(args.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Service Bus message");
            await args.AbandonMessageAsync(args.Message);
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Service Bus processor error: {Source}", args.ErrorSource);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
            await _processor.StopProcessingAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
