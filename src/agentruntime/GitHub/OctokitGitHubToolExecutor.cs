using System.Text.Json;
using Microsoft.Extensions.Logging;
using Octokit;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.GitHub;

public sealed class OctokitGitHubToolExecutor : IGitHubToolExecutor
{
    private static readonly HashSet<string> Handled = new(StringComparer.Ordinal)
    {
        "github_read_file", "github_list_files"
    };

    private readonly ISecretProvider _secrets;
    private readonly ILogger<OctokitGitHubToolExecutor> _logger;

    public OctokitGitHubToolExecutor(ISecretProvider secrets, ILogger<OctokitGitHubToolExecutor> logger)
    {
        _secrets = secrets;
        _logger = logger;
    }

    public bool CanHandle(string toolName) => Handled.Contains(toolName);

    public async Task<string> ExecuteAsync(string toolName, JsonElement args,
        GitHubRepoConnection? boardRepo, AgentRole role, CancellationToken ct)
    {
        if (boardRepo is null)
            return Err("No GitHub repo connected to this board. Ask a board admin to connect one.");

        try
        {
            var client = await GitHubClientFactory.CreateAsync(_secrets, boardRepo, ct);

            _logger.LogInformation("[GitHub] {Tool} on {Owner}/{Repo}", toolName, boardRepo.Owner, boardRepo.Repo);

            return toolName switch
            {
                "github_read_file"  => await ReadFileAsync(client, boardRepo, args, ct),
                "github_list_files" => await ListFilesAsync(client, boardRepo, args, ct),
                _                   => Err($"Unknown GitHub tool '{toolName}'")
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GitHub] {Tool} failed", toolName);
            return Err(ex.Message);
        }
    }

    // ── Tool implementations ─────────────────────────────────────────────────

    private static async Task<string> ReadFileAsync(GitHubClient client,
        GitHubRepoConnection repo, JsonElement args, CancellationToken ct)
    {
        var path   = Str(args, "path");
        var branch = Str(args, "branch");
        var contents = string.IsNullOrEmpty(branch)
            ? await client.Repository.Content.GetAllContents(repo.Owner, repo.Repo, path)
            : await client.Repository.Content.GetAllContentsByRef(repo.Owner, repo.Repo, path, branch);
        var file = contents.FirstOrDefault();
        if (file is null) return Err($"File '{path}' not found.");
        return JsonSerializer.Serialize(new { path = file.Path, content = file.Content, sha = file.Sha });
    }

    private static async Task<string> ListFilesAsync(GitHubClient client,
        GitHubRepoConnection repo, JsonElement args, CancellationToken ct)
    {
        var path   = Str(args, "path");
        var branch = Str(args, "branch");
        IReadOnlyList<Octokit.RepositoryContent> items;
        try
        {
            if (string.IsNullOrEmpty(path))
                items = string.IsNullOrEmpty(branch)
                    ? await client.Repository.Content.GetAllContents(repo.Owner, repo.Repo)
                    : await client.Repository.Content.GetAllContentsByRef(repo.Owner, repo.Repo, branch);
            else
                items = string.IsNullOrEmpty(branch)
                    ? await client.Repository.Content.GetAllContents(repo.Owner, repo.Repo, path)
                    : await client.Repository.Content.GetAllContentsByRef(repo.Owner, repo.Repo, path, branch);
        }
        catch (NotFoundException)
        {
            return JsonSerializer.Serialize(new { files = Array.Empty<object>(), note = "Repository is empty or path not found." });
        }
        return JsonSerializer.Serialize(items.Select(i => new { i.Name, i.Path, type = i.Type.Value.ToString() }));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string Err(string msg) =>
        JsonSerializer.Serialize(new { error = msg });
}
