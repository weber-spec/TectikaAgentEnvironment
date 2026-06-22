using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.Preview;

/// <summary>
/// Options for the ACI live-preview provisioner.
/// </summary>
public sealed class AciPreviewOptions
{
    public string ResourceGroup { get; set; } = string.Empty;
    public string Region { get; set; } = "westeurope";
    public string AcrImage { get; set; } = string.Empty;
    public string AcrLoginServer { get; set; } = "tacragentteam.azurecr.io";
    public string? MiResourceId { get; set; }
}

/// <summary>
/// Real Azure Container Instance adapter for live previews. Mirrors the proven
/// SDK patterns in <c>WorkspaceService</c> (Workflows) but is intentionally
/// separate from the run-workspace path.
///
/// Each preview is one ACI container group:
///   - one "preview" container, image AcrImage, 2 GB / 1 CPU
///   - public IP with DnsNameLabel = group name, TCP :8080 exposed
///   - restart policy Never (one-shot preview)
///   - tagged tectika-preview / boardOwner so the reaper can find it
/// </summary>
public sealed class AciPreviewProvisioner : IPreviewProvisioner
{
    private const int AppPort = 8080;
    private const string PreviewTagKey = "tectika-preview";
    private const string BoardOwnerTagKey = "boardOwner";

    private readonly AciPreviewOptions _opt;
    private readonly ILogger<AciPreviewProvisioner> _log;

    public AciPreviewProvisioner(AciPreviewOptions opt, ILogger<AciPreviewProvisioner> log)
    {
        _opt = opt;
        _log = log;
    }

    private async Task<ResourceGroupResource> RgAsync(CancellationToken ct)
    {
        var arm = new ArmClient(new DefaultAzureCredential());
        var subscription = await arm.GetDefaultSubscriptionAsync(ct);
        return (await subscription.GetResourceGroupAsync(_opt.ResourceGroup, ct)).Value;
    }

    public async Task<PreviewProvisionResult> ProvisionAsync(
        GitHubRepoConnection repo, string branch, string? pat, string dnsLabel, CancellationToken ct)
    {
        _log.LogInformation("[Preview] provisioning ACI {Name}", dnsLabel);

        var previewContainer = new ContainerInstanceContainer(
            name: "preview",
            image: _opt.AcrImage,
            resources: new ContainerResourceRequirements(
                new ContainerResourceRequestsContent(memoryInGB: 2, cpu: 1)))
        {
            Ports = { new ContainerPort(AppPort) },
        };
        previewContainer.EnvironmentVariables.Add(new ContainerEnvironmentVariable("REPO_URL") { Value = repo.RepoUrl });
        previewContainer.EnvironmentVariables.Add(new ContainerEnvironmentVariable("GIT_BRANCH") { Value = branch });
        if (!string.IsNullOrEmpty(pat))
            previewContainer.EnvironmentVariables.Add(new ContainerEnvironmentVariable("GIT_PAT") { SecureValue = pat });

        var groupData = new ContainerGroupData(
            new AzureLocation(_opt.Region),
            [previewContainer],
            ContainerInstanceOperatingSystemType.Linux)
        {
            RestartPolicy = ContainerGroupRestartPolicy.Never,
            IPAddress = new ContainerGroupIPAddress(
                [new ContainerGroupPort(AppPort) { Protocol = ContainerGroupNetworkProtocol.Tcp }],
                ContainerGroupIPAddressType.Public)
            {
                DnsNameLabel = dnsLabel,
            },
        };
        groupData.Tags[PreviewTagKey] = repo.Repo;
        groupData.Tags[BoardOwnerTagKey] = repo.Owner;

        // Use the user-assigned managed identity so ACI can pull from ACR without admin creds.
        if (!string.IsNullOrEmpty(_opt.MiResourceId))
        {
            groupData.Identity = new ManagedServiceIdentity(ManagedServiceIdentityType.UserAssigned);
            groupData.Identity.UserAssignedIdentities[new ResourceIdentifier(_opt.MiResourceId)] =
                new UserAssignedIdentity();
            groupData.ImageRegistryCredentials.Add(
                new ContainerGroupImageRegistryCredential(_opt.AcrLoginServer)
                {
                    Identity = _opt.MiResourceId,
                });
        }

        var rg = await RgAsync(ct);
        var op = await rg.GetContainerGroups().CreateOrUpdateAsync(
            WaitUntil.Completed, dnsLabel, groupData, ct);

        var fqdn = op.Value.Data.IPAddress?.Fqdn
                   ?? $"{dnsLabel}.{_opt.Region}.azurecontainer.io";
        _log.LogInformation("[Preview] provisioned {Name} -> {Fqdn}", dnsLabel, fqdn);
        return new PreviewProvisionResult(fqdn, dnsLabel);
    }

    public async Task DestroyAsync(string containerName, CancellationToken ct)
    {
        try
        {
            var rg = await RgAsync(ct);
            var group = (await rg.GetContainerGroupAsync(containerName, ct)).Value;
            await group.DeleteAsync(WaitUntil.Started, ct);
            _log.LogInformation("[Preview] destroyed {Name}", containerName);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _log.LogInformation("[Preview] destroy no-op, {Name} already gone", containerName);
        }
        catch (Exception ex)
        {
            // Best-effort teardown: swallow transient failures (auth blip, 429, 5xx) so
            // cleanup paths don't fail. Mirrors WorkspaceService.DestroyAsync.
            _log.LogWarning(ex, "[Preview] destroy failed for {Name}", containerName);
        }
    }

    public async Task<IReadOnlyList<PreviewGroupInfo>> ListPreviewGroupsAsync(CancellationToken ct)
    {
        var rg = await RgAsync(ct);
        var result = new List<PreviewGroupInfo>();
        await foreach (var g in rg.GetContainerGroups().GetAllAsync(ct))
        {
            if (g.Data.Tags.ContainsKey(PreviewTagKey))
            {
                var owner = g.Data.Tags.TryGetValue(BoardOwnerTagKey, out var o) ? o : "";
                result.Add(new PreviewGroupInfo(g.Data.Name, owner));
            }
        }
        return result;
    }
}
