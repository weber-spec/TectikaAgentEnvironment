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
        "github_read_file", "github_list_files",
        "github_create_branch", "github_push_file", "github_create_pr"
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
            var pat = await _secrets.GetSecretAsync(boardRepo.PatSecretName, ct);
            var client = new GitHubClient(new ProductHeaderValue("TectikaAgents"))
            {
                Credentials = new Credentials(pat)
            };

            _logger.LogInformation("[GitHub] {Tool} on {Owner}/{Repo}", toolName, boardRepo.Owner, boardRepo.Repo);

            return toolName switch
            {
                "github_read_file"     => await ReadFileAsync(client, boardRepo, args, ct),
                "github_list_files"    => await ListFilesAsync(client, boardRepo, args, ct),
                "github_create_branch" => await CreateBranchAsync(client, boardRepo, args, ct),
                "github_push_file"     => await PushFileAsync(client, boardRepo, args, ct),
                "github_create_pr"     => await CreatePrAsync(client, boardRepo, args, ct),
                _                      => Err($"Unknown GitHub tool '{toolName}'")
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
        var items  = string.IsNullOrEmpty(branch)
            ? await client.Repository.Content.GetAllContents(repo.Owner, repo.Repo, path)
            : await client.Repository.Content.GetAllContentsByRef(repo.Owner, repo.Repo, path, branch);
        return JsonSerializer.Serialize(items.Select(i => new { i.Name, i.Path, type = i.Type.Value.ToString() }));
    }

    private static async Task<string> CreateBranchAsync(GitHubClient client,
        GitHubRepoConnection repo, JsonElement args, CancellationToken ct)
    {
        var branch   = Str(args, "branch");
        var from     = Str(args, "from");

        // Resolve base SHA
        string sha;
        if (string.IsNullOrEmpty(from))
        {
            var repoData = await client.Repository.Get(repo.Owner, repo.Repo);
            var baseRef  = await client.Git.Reference.Get(repo.Owner, repo.Repo, $"heads/{repoData.DefaultBranch}");
            sha = baseRef.Object.Sha;
        }
        else if (from.Length == 40 && from.All(c => "0123456789abcdefABCDEF".Contains(c)))
        {
            sha = from;
        }
        else
        {
            var baseRef = await client.Git.Reference.Get(repo.Owner, repo.Repo, $"heads/{from}");
            sha = baseRef.Object.Sha;
        }

        await client.Git.Reference.Create(repo.Owner, repo.Repo, new NewReference($"refs/heads/{branch}", sha));
        return JsonSerializer.Serialize(new { created = branch, from_sha = sha });
    }

    private static async Task<string> PushFileAsync(GitHubClient client,
        GitHubRepoConnection repo, JsonElement args, CancellationToken ct)
    {
        var path    = Str(args, "path");
        var content = Str(args, "content");
        var branch  = Str(args, "branch");
        var message = Str(args, "message");

        // Check if file already exists (for update vs create)
        string? existingSha = null;
        try
        {
            var existing = await client.Repository.Content.GetAllContentsByRef(repo.Owner, repo.Repo, path, branch);
            existingSha = existing.FirstOrDefault()?.Sha;
        }
        catch (NotFoundException) { }

        if (existingSha is null)
        {
            var created = await client.Repository.Content.CreateFile(repo.Owner, repo.Repo, path,
                new CreateFileRequest(message, content, branch));
            return JsonSerializer.Serialize(new { action = "created", path, sha = created.Content.Sha });
        }
        else
        {
            var updated = await client.Repository.Content.UpdateFile(repo.Owner, repo.Repo, path,
                new UpdateFileRequest(message, content, existingSha, branch));
            return JsonSerializer.Serialize(new { action = "updated", path, sha = updated.Content.Sha });
        }
    }

    private static async Task<string> CreatePrAsync(GitHubClient client,
        GitHubRepoConnection repo, JsonElement args, CancellationToken ct)
    {
        var title = Str(args, "title");
        var body  = Str(args, "body");
        var head  = Str(args, "head");
        var @base = Str(args, "base");

        var pr = await client.PullRequest.Create(repo.Owner, repo.Repo,
            new NewPullRequest(title, head, @base) { Body = body });
        return JsonSerializer.Serialize(new { pr.Number, pr.Title, url = pr.HtmlUrl });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string Err(string msg) =>
        JsonSerializer.Serialize(new { error = msg });
}
