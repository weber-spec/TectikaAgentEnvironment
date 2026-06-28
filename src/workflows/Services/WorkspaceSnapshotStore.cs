using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TectikaAgents.Workflows.Services;

/// <summary>Durable backing store for a no-repo board's workspace: a git bundle per board, in blob
/// storage. The connected-repo analogue is GitHub origin; this is the durable store for boards with no
/// remote, so their merged deliverable files survive the ACI being recycled.</summary>
public interface IWorkspaceSnapshotStore
{
    /// <summary>Upload (overwrite) the board's snapshot bundle.</summary>
    Task UploadAsync(string boardId, byte[] bundle, CancellationToken ct = default);

    /// <summary>Download the board's snapshot bundle, or null if none exists yet.</summary>
    Task<byte[]?> DownloadAsync(string boardId, CancellationToken ct = default);

    /// <summary>Delete the board's snapshot bundle if present (best-effort, idempotent).</summary>
    Task DeleteAsync(string boardId, CancellationToken ct = default);
}

/// <summary>Blob-backed snapshot store. Reuses the host's storage account (the one behind
/// AzureWebJobsStorage) via managed identity; one container, one blob per board.</summary>
public sealed class BlobWorkspaceSnapshotStore : IWorkspaceSnapshotStore
{
    private const string ContainerName = "workspace-snapshots";
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobWorkspaceSnapshotStore> _logger;
    private int _ensured;

    public BlobWorkspaceSnapshotStore(IConfiguration config, ILogger<BlobWorkspaceSnapshotStore> logger)
    {
        _logger = logger;
        var account = config["WorkspaceSnapshots:AccountName"]
                      ?? config["AzureWebJobsStorage:accountName"]
                      ?? throw new InvalidOperationException(
                          "No storage account configured (WorkspaceSnapshots:AccountName / AzureWebJobsStorage:accountName).");
        var service = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), new DefaultAzureCredential());
        _container = service.GetBlobContainerClient(ContainerName);
    }

    private static string BlobName(string boardId) => $"{boardId}.bundle";

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _ensured, 1) == 0)
            await _container.CreateIfNotExistsAsync(cancellationToken: ct);
    }

    public async Task UploadAsync(string boardId, byte[] bundle, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        using var ms = new MemoryStream(bundle, writable: false);
        await _container.GetBlobClient(BlobName(boardId)).UploadAsync(ms, overwrite: true, ct);
        _logger.LogInformation("[Snapshot] uploaded board {BoardId} snapshot ({Bytes} bytes)", boardId, bundle.Length);
    }

    public async Task<byte[]?> DownloadAsync(string boardId, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(BlobName(boardId));
        if (!await blob.ExistsAsync(ct)) return null;
        using var ms = new MemoryStream();
        await blob.DownloadToAsync(ms, ct);
        _logger.LogInformation("[Snapshot] downloaded board {BoardId} snapshot ({Bytes} bytes)", boardId, ms.Length);
        return ms.ToArray();
    }

    public async Task DeleteAsync(string boardId, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        await _container.GetBlobClient(BlobName(boardId)).DeleteIfExistsAsync(cancellationToken: ct);
        _logger.LogInformation("[Snapshot] deleted board {BoardId} snapshot (if present)", boardId);
    }
}
