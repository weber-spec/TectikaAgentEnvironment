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
    private readonly NotificationRepository _notificationRepo;
    private readonly NotificationConnectionManager _notificationManager;
    private readonly ServiceBusSettings _settings;
    private readonly ILogger<ServiceBusListenerService> _logger;
    private ServiceBusProcessor? _processor;

    public ServiceBusListenerService(
        SseConnectionManager sse,
        NotificationRepository notificationRepo,
        NotificationConnectionManager notificationManager,
        IOptions<ServiceBusSettings> settings,
        ILogger<ServiceBusListenerService> logger)
    {
        _sse = sse;
        _notificationRepo = notificationRepo;
        _notificationManager = notificationManager;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Skip if not configured (dev without Service Bus)
        if (string.IsNullOrEmpty(_settings.Namespace) || _settings.Namespace.StartsWith("__"))
        {
            _logger.LogWarning("[ServiceBusListener] Service Bus not configured — SSE events will not stream from agents");
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

        try
        {
            await _processor.StartProcessingAsync(stoppingToken);
            _logger.LogInformation("[ServiceBusListener] started topic={Topic} subscription={Subscription}", _settings.AgentEventsTopic, _settings.AgentEventsSubscription);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown / hot-reload: the host cancelled stoppingToken. Complete gracefully —
            // letting this propagate trips the default BackgroundServiceExceptionBehavior=StopHost,
            // which tears the whole API down (and the restart then races the port).
        }
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        _logger.LogInformation("[ServiceBusListener] received message {MessageId} subject={Subject}", args.Message.MessageId, args.Message.Subject);
        try
        {
            var agentEvent = JsonSerializer.Deserialize<AgentEvent>(args.Message.Body.ToString());
            if (agentEvent is not null)
            {
                await _sse.BroadcastAsync(agentEvent, args.CancellationToken);
                _logger.LogInformation("[ServiceBusListener] dispatched event {EventType} for run {RunId}", agentEvent.Type, agentEvent.RunId);

                var notification = NotificationMapper.Map(agentEvent, tenantId: "default");
                if (notification is not null)
                {
                    await _notificationRepo.SaveAsync(notification, args.CancellationToken);
                    await _notificationManager.BroadcastAsync(notification, args.CancellationToken);
                }
            }

            await args.CompleteMessageAsync(args.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ServiceBusListener] failed processing message {MessageId}", args.Message.MessageId);
            await args.AbandonMessageAsync(args.Message);
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "[ServiceBusListener] processor error source={Source}", args.ErrorSource);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            _logger.LogInformation("[ServiceBusListener] stopped");
        }
        await base.StopAsync(cancellationToken);
    }
}
