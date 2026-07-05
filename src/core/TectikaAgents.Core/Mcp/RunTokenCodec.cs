using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TectikaAgents.Core.Mcp;

/// <summary>Stateless per-run bearer token for the board-tools MCP endpoint: HMAC-SHA256 over the run
/// identity + expiry, base64url, no storage. Minted by the workflows runtime at job start and validated
/// by the API on every MCP call — so a tool call is bound to one run/board/task and can't be replayed
/// after expiry. The signing key is a shared Key Vault secret held by both surfaces.</summary>
public static class RunTokenCodec
{
    private const string Version = "v1";
    private const char Sep = '|';

    /// <summary>token = base64url(payload) + "." + base64url(hmac(payload)).</summary>
    public static string Mint(RunContext ctx, string signingKey, DateTimeOffset expiresAt)
    {
        var payload = string.Join(Sep,
            Version, ctx.RunId, ctx.TaskId, ctx.BoardId, ctx.TenantId, ctx.RoleId,
            expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        return $"{ToB64Url(payloadBytes)}.{ToB64Url(Sign(payloadBytes, signingKey))}";
    }

    /// <summary>Returns the RunContext when the signature verifies and the token has not expired; else null.</summary>
    public static RunContext? TryValidate(string token, string signingKey, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;

        byte[] payloadBytes, sig;
        try { payloadBytes = FromB64Url(parts[0]); sig = FromB64Url(parts[1]); }
        catch { return null; }

        if (!CryptographicOperations.FixedTimeEquals(sig, Sign(payloadBytes, signingKey))) return null;

        var fields = Encoding.UTF8.GetString(payloadBytes).Split(Sep);
        if (fields.Length != 7 || fields[0] != Version) return null;
        if (!long.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expUnix)) return null;
        if (DateTimeOffset.FromUnixTimeSeconds(expUnix) <= now) return null;

        return new RunContext(RunId: fields[1], TaskId: fields[2], BoardId: fields[3], TenantId: fields[4], RoleId: fields[5]);
    }

    private static byte[] Sign(byte[] payload, string signingKey)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        return h.ComputeHash(payload);
    }

    private static string ToB64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t += (t.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(t);
    }
}
