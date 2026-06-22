using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Outcome of provisioning a preview container group.</summary>
public sealed record PreviewProvisionResult(string Fqdn, string ContainerName);

/// <summary>A summary of a live preview container group (for orphan reconciliation).
/// <paramref name="Owner"/> is the GitHub owner from the boardOwner tag.</summary>
public sealed record PreviewGroupInfo(string Name, string Owner);

/// <summary>Provisions and tears down ephemeral preview container groups (ACI in prod).</summary>
public interface IPreviewProvisioner
{
    /// <summary>Create a public container group serving the branch on port 8080.
    /// dnsLabel becomes the group name AND the public DNS label. Returns the FQDN (no scheme/port).</summary>
    Task<PreviewProvisionResult> ProvisionAsync(
        GitHubRepoConnection repo, string branch, string? pat, string dnsLabel, CancellationToken ct);

    /// <summary>Delete a container group by name. Idempotent (missing group is a no-op).</summary>
    Task DestroyAsync(string containerName, CancellationToken ct);

    /// <summary>List all preview container groups (tagged tectika-preview) for orphan cleanup.</summary>
    Task<IReadOnlyList<PreviewGroupInfo>> ListPreviewGroupsAsync(CancellationToken ct);
}
