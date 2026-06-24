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
/// Manages ACI workspaces — one container per board, one git worktree per run.
///
/// Container lifecycle:
///   - Created on first run that needs a workspace on the board
///   - Shared by all concurrent runs on the same board
///   - Destroyed after 10 minutes idle (no active runs) by IdleWorkspaceCleanupTrigger
///   - Destroyed immediately when the board is deleted
///
/// Config keys (appsettings / env vars):
///   Workspace:ResourceGroup  (default: rg-agentteam-dev-001)
///   Workspace:Image          (default: tacragentteam.azurecr.io/agent-workspace:latest)
///   Workspace:MiResourceId   — full ARM resource id of the workflows user-assigned MI
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

    /// <summary>Deterministic ACI container-group name for a board. ACI names must be ≤63 chars,
    /// alphanumeric + hyphens, and start with a letter.</summary>
    public static string ContainerNameFor(string boardId) =>
        $"tws-{boardId[..Math.Min(8, boardId.Length)].ToLowerInvariant()}";

    /// <summary>Env vars for the workspace container.</summary>
    public static List<ContainerEnvironmentVariable> BuildEnv(
        GitHubRepoConnection? github, string token, string? pat, bool canPush = false)
    {
        var env = new List<ContainerEnvironmentVariable>
        {
            new("EXECUTOR_TOKEN") { SecureValue = token },
        };
        if (github is not null)
        {
            env.Add(new("REPO_URL")     { Value = github.RepoUrl });
            env.Add(new("GIT_PAT")      { SecureValue = pat });
            env.Add(new("GIT_CAN_PUSH") { Value = canPush ? "true" : "false" });
        }
        return env;
    }

    public async Task<WorkspaceInfo?> EnsureBoardContainerAsync(
        Board board, CancellationToken ct = default)
    {
        var pat = board.GitHub is null ? null : await _secrets.GetSecretAsync(board.GitHub.PatSecretName, ct);
        var token = GenerateToken();
        var containerName = ContainerNameFor(board.Id);

        _logger.LogInformation("[Workspace] provisioning ACI {Name} board={BoardId}", containerName, board.Id);

        var arm = new ArmClient(new DefaultAzureCredential());
        var subscription = await arm.GetDefaultSubscriptionAsync(ct);
        var rg = (await subscription.GetResourceGroupAsync(_resourceGroup, ct)).Value;

        // canPush=true here because worktrees for individual runs may control push permission
        // per-run via /worktree/add; the container-level clone uses a read-only push guard only
        // when the board as a whole has no push-capable agents.
        var workspaceContainer = new ContainerInstanceContainer(
            name: "workspace",
            image: _acrImage,
            resources: new ContainerResourceRequirements(
                new ContainerResourceRequestsContent(memoryInGB: 2, cpu: 1)))
        {
            Ports = { new ContainerPort(ExecutorPort) },
        };
        foreach (var e in BuildEnv(board.GitHub, token, pat, canPush: true))
            workspaceContainer.EnvironmentVariables.Add(e);

        var dnsLabel = containerName;
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

        if (!string.IsNullOrEmpty(_miResourceId))
        {
            groupData.Identity = new Azure.ResourceManager.Models.ManagedServiceIdentity(
                Azure.ResourceManager.Models.ManagedServiceIdentityType.UserAssigned);
            groupData.Identity.UserAssignedIdentities[new Azure.Core.ResourceIdentifier(_miResourceId)] =
                new Azure.ResourceManager.Models.UserAssignedIdentity();
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
        try
        {
            await WaitForHealthAsync(endpoint, token, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Workspace] ACI {Name} never became healthy within {Timeout}s — destroying orphan", containerName, HealthPollTimeoutSec);
            await DestroyBoardContainerAsync(containerName, CancellationToken.None);
            throw;
        }
        _logger.LogInformation("[Workspace] ACI {Name} healthy", containerName);

        return new WorkspaceInfo(containerName, endpoint, token);
    }

    public async Task CreateWorktreeAsync(
        string endpoint, string token, string runId, string branch, bool canPush,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[Workspace] creating worktree run={RunId} branch={Branch}", runId, branch);
        await InvokeAsync(endpoint, token, "/worktree/add",
            new { run_id = runId, branch, can_push = canPush }, ct);
    }

    public async Task RemoveWorktreeAsync(
        string endpoint, string token, string runId,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[Workspace] removing worktree run={RunId}", runId);
        try
        {
            await InvokeAsync(endpoint, token, "/worktree/remove", new { run_id = runId }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Workspace] remove worktree run={RunId} failed (non-fatal)", runId);
        }
    }

    public async Task DestroyBoardContainerAsync(string containerName, CancellationToken ct = default)
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
