using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>
/// Manages ephemeral ACI workspaces for agent runs.
///
/// Each workspace is one ACI container group:
///   - Image: agent-workspace (from ACR, pulled via managed identity)
///   - Entrypoint: clones the GitHub repo + starts executor.py on :8080
///   - Lifetime: provisioned at the start of a steerable run, destroyed in finally
///
/// Config keys (appsettings / env vars):
///   Workspace:ResourceGroup  (default: rg-agentteam-dev-001)
///   Workspace:Image          (default: tacragentteam.azurecr.io/agent-workspace:latest)
///   Workspace:MiResourceId   — full ARM resource id of the workflows user-assigned MI
///                              e.g. /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mi-agentteam-workflows
/// </summary>
public class WorkspaceService : IWorkspaceService
{
    private const string AciLocation = "westeurope";
    private const int ExecutorPort = 8080;
    private const int HealthPollIntervalMs = 3_000;
    private const int HealthPollTimeoutSec = 300;

    private readonly string _resourceGroup;
    private readonly string _acrImage;
    private readonly string? _miResourceId;
    private readonly ISecretProvider _secrets;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WorkspaceService> _logger;

    public WorkspaceService(
        IConfiguration config,
        ISecretProvider secrets,
        IHttpClientFactory httpFactory,
        ILogger<WorkspaceService> logger)
    {
        _resourceGroup = config["Workspace:ResourceGroup"] ?? "rg-agentteam-dev-001";
        _acrImage = config["Workspace:Image"] ?? "tacragentteam.azurecr.io/agent-workspace:latest";
        _miResourceId = config["Workspace:MiResourceId"];
        _secrets = secrets;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Env vars for the workspace container. Repo vars (REPO_URL/GIT_BRANCH/GIT_PAT) are
    /// included only when a repo is connected — the entrypoint provisions a bare, git-free /workspace
    /// otherwise.</summary>
    public static List<ContainerEnvironmentVariable> BuildEnv(
        GitHubRepoConnection? github, string branchName, string token, string? pat)
    {
        var env = new List<ContainerEnvironmentVariable>
        {
            new("EXECUTOR_TOKEN") { SecureValue = token },
        };
        if (github is not null)
        {
            env.Add(new("REPO_URL")   { Value = github.RepoUrl });
            env.Add(new("GIT_BRANCH") { Value = branchName });
            env.Add(new("GIT_PAT")    { SecureValue = pat });
        }
        return env;
    }

    public async Task<WorkspaceInfo?> ProvisionAsync(
        Board board, string branchName, string runId, CancellationToken ct = default)
    {
        var pat = board.GitHub is null ? null : await _secrets.GetSecretAsync(board.GitHub.PatSecretName, ct);
        var token = GenerateToken();
        // ACI name: max 63 chars, alphanumeric + hyphens, must start with letter
        var containerName = $"tws-{runId[..Math.Min(8, runId.Length)].ToLowerInvariant()}";

        _logger.LogInformation("[Workspace] provisioning ACI {Name} run={RunId}", containerName, runId);

        var arm = new ArmClient(new DefaultAzureCredential());
        var subscription = await arm.GetDefaultSubscriptionAsync(ct);
        var rg = (await subscription.GetResourceGroupAsync(_resourceGroup, ct)).Value;

        var workspaceContainer = new ContainerInstanceContainer(
            name: "workspace",
            image: _acrImage,
            resources: new ContainerResourceRequirements(
                new ContainerResourceRequestsContent(memoryInGB: 2, cpu: 1)))
        {
            Ports = { new ContainerPort(ExecutorPort) },
        };
        foreach (var e in BuildEnv(board.GitHub, branchName, token, pat))
            workspaceContainer.EnvironmentVariables.Add(e);

        var dnsLabel = containerName; // unique per run, ACI enforces global uniqueness in the region
        var groupData = new ContainerGroupData(
            new Azure.Core.AzureLocation(AciLocation),
            [workspaceContainer],
            ContainerInstanceOperatingSystemType.Linux)
        {
            IPAddress = new ContainerGroupIPAddress(
                [new ContainerGroupPort(ExecutorPort) { Protocol = ContainerGroupNetworkProtocol.Tcp }],
                ContainerGroupIPAddressType.Public)
            {
                DnsNameLabel = dnsLabel,
            },
        };

        // Use the workflows managed identity so ACI can pull from ACR without admin creds.
        if (!string.IsNullOrEmpty(_miResourceId))
        {
            groupData.Identity = new Azure.ResourceManager.Models.ManagedServiceIdentity(
                Azure.ResourceManager.Models.ManagedServiceIdentityType.UserAssigned);
            groupData.Identity.UserAssignedIdentities[new Azure.Core.ResourceIdentifier(_miResourceId)] =
                new Azure.ResourceManager.Models.UserAssignedIdentity();

            // Tell ACI to use the MI for image registry auth.
            groupData.ImageRegistryCredentials.Add(
                new ContainerGroupImageRegistryCredential("tacragentteam.azurecr.io")
                {
                    Identity = _miResourceId,
                });
        }

        var groups = rg.GetContainerGroups();
        var op = await groups.CreateOrUpdateAsync(
            Azure.WaitUntil.Completed, containerName, groupData, ct);

        var fqdn = op.Value.Data.IPAddress?.Fqdn
                   ?? $"{dnsLabel}.{AciLocation}.azurecontainer.io";
        var endpoint = $"http://{fqdn}:{ExecutorPort}";

        _logger.LogInformation("[Workspace] ACI {Name} at {Endpoint} — polling health", containerName, endpoint);
        await WaitForHealthAsync(endpoint, token, ct);
        _logger.LogInformation("[Workspace] ACI {Name} healthy", containerName);

        return new WorkspaceInfo(containerName, endpoint, token);
    }

    public async Task<CommandResult> RunCommandAsync(
        string endpoint, string token, string command,
        int timeoutSeconds = 60, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(timeoutSeconds + 15);

        var body = JsonSerializer.Serialize(new { cmd = command, timeout = timeoutSeconds });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/run")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var result = await resp.Content.ReadFromJsonAsync<ExecutorResponse>(ct)
                     ?? new ExecutorResponse("", "empty response from executor", -1);

        return new CommandResult(result.Stdout, result.Stderr, result.ExitCode);
    }

    public async Task<string> InvokeAsync(
        string endpoint, string token, string route, object body, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}{route}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task DestroyAsync(string containerName, CancellationToken ct = default)
    {
        _logger.LogInformation("[Workspace] destroying ACI {Name}", containerName);
        try
        {
            var arm = new ArmClient(new DefaultAzureCredential());
            var subscription = await arm.GetDefaultSubscriptionAsync(ct);
            var rg = (await subscription.GetResourceGroupAsync(_resourceGroup, ct)).Value;
            var group = (await rg.GetContainerGroupAsync(containerName, ct)).Value;
            await group.DeleteAsync(Azure.WaitUntil.Started, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Workspace] destroy {Name} failed (non-fatal)", containerName);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task WaitForHealthAsync(string endpoint, string token, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(8);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(HealthPollTimeoutSec);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/health");
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                var resp = await http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(HealthPollIntervalMs, ct);
        }
        throw new TimeoutException($"Workspace at {endpoint} did not become healthy within {HealthPollTimeoutSec}s");
    }

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
               .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private sealed record ExecutorResponse(
        [property: JsonPropertyName("stdout")] string Stdout,
        [property: JsonPropertyName("stderr")] string Stderr,
        [property: JsonPropertyName("exit_code")] int ExitCode);
}
