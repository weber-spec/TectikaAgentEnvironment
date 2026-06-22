using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Single writer of preview session state. Provisioning is awaited inline (the POST is held
/// open ~30-60s in prod; acceptable for v1). Replaces any existing preview per board.
/// </summary>
public sealed class PreviewService : IPreviewService
{
    private readonly ICosmosDbService _cosmos;
    private readonly IPreviewProvisioner _prov;
    private readonly ISecretProvider _secrets;
    private readonly PreviewSettings _settings;
    private readonly Func<DateTimeOffset> _now;

    public PreviewService(ICosmosDbService cosmos, IPreviewProvisioner prov, ISecretProvider secrets,
        PreviewSettings settings, Func<DateTimeOffset> now)
    { _cosmos = cosmos; _prov = prov; _secrets = secrets; _settings = settings; _now = now; }

    public async Task<PreviewSession> StartAsync(string tenantId, string boardId, string branch, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(tenantId, boardId, ct) ?? throw new KeyNotFoundException("board");
        if (board.GitHub is null) throw new PreviewNotConnectedException();
        var repo = board.GitHub;

        var existing = await _cosmos.GetPreviewAsync(boardId, ct);
        if (existing?.ContainerName is not null)
        {
            await _prov.DestroyAsync(existing.ContainerName, ct);
            existing.Status = PreviewStatus.Stopped;
            await _cosmos.UpsertPreviewAsync(existing, ct);
        }

        var now = _now();
        var s = new PreviewSession
        {
            Id = PreviewLifecycle.NewDnsLabel(), BoardId = boardId, TenantId = tenantId, Branch = branch,
            Status = PreviewStatus.Provisioning, CreatedAt = now, LastActivityAt = now,
            ExpiresAt = PreviewLifecycle.ComputeExpiry(now, now, _settings.IdleMinutes, _settings.CapMinutes),
        };
        await _cosmos.UpsertPreviewAsync(s, ct);

        try
        {
            var pat = string.IsNullOrEmpty(repo.PatSecretName) ? null : await _secrets.GetSecretAsync(repo.PatSecretName, ct);
            var r = await _prov.ProvisionAsync(repo, branch, pat, s.Id, ct);
            s.Status = PreviewStatus.Running;
            s.ContainerName = r.ContainerName;
            s.Url = $"https://{r.Fqdn}:8080";
        }
        catch (Exception ex)
        {
            s.Status = PreviewStatus.Failed;
            s.Error = ex.Message;
        }
        await _cosmos.UpsertPreviewAsync(s, ct);
        return s;
    }

    public async Task<PreviewSession?> GetAsync(string tenantId, string boardId, CancellationToken ct)
    {
        var s = await _cosmos.GetPreviewAsync(boardId, ct);
        return s is null || s.TenantId != tenantId ? null : s;
    }

    public async Task<PreviewSession?> HeartbeatAsync(string tenantId, string boardId, CancellationToken ct)
    {
        var s = await _cosmos.GetPreviewAsync(boardId, ct);
        if (s is null || s.TenantId != tenantId) return null;
        if (s.Status != PreviewStatus.Running) return s;
        var now = _now();
        s.LastActivityAt = now;
        s.ExpiresAt = PreviewLifecycle.ComputeExpiry(s.CreatedAt, now, _settings.IdleMinutes, _settings.CapMinutes);
        await _cosmos.UpsertPreviewAsync(s, ct);
        return s;
    }

    public async Task StopAsync(string tenantId, string boardId, CancellationToken ct)
    {
        var s = await _cosmos.GetPreviewAsync(boardId, ct);
        if (s is null || s.TenantId != tenantId) return;
        if (s.ContainerName is not null) await _prov.DestroyAsync(s.ContainerName, ct);
        s.Status = PreviewStatus.Stopped;
        await _cosmos.UpsertPreviewAsync(s, ct);
    }
}
