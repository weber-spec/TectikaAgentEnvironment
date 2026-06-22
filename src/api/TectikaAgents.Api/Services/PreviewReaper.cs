using Microsoft.Extensions.DependencyInjection;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public static class PreviewReaper
{
    public static IEnumerable<PreviewSession> SelectExpired(IEnumerable<PreviewSession> sessions, DateTimeOffset now)
        => sessions.Where(s => PreviewLifecycle.IsExpired(s, now));

    public static IEnumerable<PreviewGroupInfo> SelectOrphans(
        IEnumerable<PreviewGroupInfo> groups, IEnumerable<PreviewSession> active)
    {
        var live = active
            .SelectMany(a => new[] { a.ContainerName, a.Id })
            .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToHashSet();
        return groups.Where(g => !live.Contains(g.Name));
    }
}

public sealed class PreviewIdleReaperService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IPreviewProvisioner _prov;
    private readonly ILogger<PreviewIdleReaperService> _log;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    public PreviewIdleReaperService(IServiceProvider sp, IPreviewProvisioner prov, ILogger<PreviewIdleReaperService> log)
    { _sp = sp; _prov = prov; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var cosmos = scope.ServiceProvider.GetRequiredService<ICosmosDbService>();
                var active = await cosmos.ListActivePreviewsAsync(ct);
                var now = DateTimeOffset.UtcNow;
                foreach (var s in PreviewReaper.SelectExpired(active, now))
                {
                    if (s.ContainerName is not null) await _prov.DestroyAsync(s.ContainerName, ct);
                    s.Status = PreviewStatus.Stopped;
                    await cosmos.UpsertPreviewAsync(s, ct);
                    _log.LogInformation("[PreviewReaper] reaped expired {Id}", s.Id);
                }
                var groups = await _prov.ListPreviewGroupsAsync(ct);
                foreach (var g in PreviewReaper.SelectOrphans(groups, active))
                {
                    await _prov.DestroyAsync(g.Name, ct);
                    _log.LogInformation("[PreviewReaper] reaped orphan {Name}", g.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "[PreviewReaper] sweep failed");
            }
            await Task.Delay(Interval, ct);
        }
    }
}
