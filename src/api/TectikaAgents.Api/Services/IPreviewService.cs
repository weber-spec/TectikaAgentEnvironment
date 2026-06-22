using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public sealed class PreviewSettings { public int IdleMinutes { get; set; } = 15; public int CapMinutes { get; set; } = 45; }
public sealed class PreviewNotConnectedException : Exception { }

public interface IPreviewService
{
    Task<PreviewSession> StartAsync(string tenantId, string boardId, string branch, CancellationToken ct);
    Task<PreviewSession?> GetAsync(string tenantId, string boardId, CancellationToken ct);
    Task<PreviewSession?> HeartbeatAsync(string tenantId, string boardId, CancellationToken ct);
    Task StopAsync(string tenantId, string boardId, CancellationToken ct);
}
