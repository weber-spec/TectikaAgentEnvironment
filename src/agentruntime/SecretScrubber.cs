using System.Text.RegularExpressions;

namespace TectikaAgents.AgentRuntime;

/// <summary>
/// Redacts credential-shaped substrings from tool output before it re-enters the model context.
///
/// Defense-in-depth for the workspace sandbox: the run's GitHub token lives in the container (e.g.
/// <c>/root/.git-credentials</c>) so a shell command (<c>env</c>, <c>cat ~/.git-credentials</c>,
/// <c>git config --list</c>) could otherwise echo it straight back into the conversation — and from
/// there into logs and any artifact derived from the run. Scrubbing here keeps the raw token out of
/// the model, logs, and persisted content regardless of which tool produced the text.
///
/// This is NOT a substitute for network egress controls or short-lived, narrowly-scoped tokens — a
/// determined agent with arbitrary shell access can still transform a secret before printing it. It
/// raises the bar against accidental and casual leakage.
/// </summary>
public static partial class SecretScrubber
{
    private const string Redacted = "[REDACTED]";

    // GitHub tokens: classic PAT (ghp_), fine-grained (github_pat_), OAuth/app/user/refresh (gho_/ghs_/ghu_/ghr_).
    [GeneratedRegex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bgithub_pat_[A-Za-z0-9_]{22,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubFineGrainedToken();

    // The token embedded in a git remote / credential-store URL: https://x-access-token:TOKEN@github.com
    // (covers tokens that don't match the gh* prefixes, e.g. an installation token).
    [GeneratedRegex(@"(x-access-token|[A-Za-z0-9._%-]+):[^@\s/]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoCredential();

    /// <summary>Return <paramref name="text"/> with credential-shaped substrings replaced by a marker.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = GitHubFineGrainedToken().Replace(text, Redacted);
        s = GitHubToken().Replace(s, Redacted);
        s = UserInfoCredential().Replace(s, $"$1:{Redacted}@");
        return s;
    }
}
