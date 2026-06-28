using System.Collections.Concurrent;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Api.Services;

/// <summary>In-memory <see cref="IWorkspaceSnapshotStore"/> for mock-database mode and tests —
/// no Azure Blob storage account required. State is process-local and lost on restart.</summary>
public sealed class InMemoryWorkspaceSnapshotStore : IWorkspaceSnapshotStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    public Task UploadAsync(string boardId, byte[] bundle, CancellationToken ct = default)
    {
        _blobs[boardId] = bundle;
        return Task.CompletedTask;
    }

    public Task<byte[]?> DownloadAsync(string boardId, CancellationToken ct = default) =>
        Task.FromResult(_blobs.TryGetValue(boardId, out var b) ? b : null);

    public Task DeleteAsync(string boardId, CancellationToken ct = default)
    {
        _blobs.TryRemove(boardId, out _);
        return Task.CompletedTask;
    }
}
