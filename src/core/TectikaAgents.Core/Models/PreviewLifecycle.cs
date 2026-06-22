namespace TectikaAgents.Core.Models;

/// <summary>Pure lifecycle math + naming for previews (no I/O, fully unit-testable).</summary>
public static class PreviewLifecycle
{
    /// <summary>expiry = min(lastActivity + idle, createdAt + cap).</summary>
    public static DateTimeOffset ComputeExpiry(
        DateTimeOffset createdAt, DateTimeOffset lastActivityAt, int idleMinutes, int capMinutes)
    {
        var idle = lastActivityAt.AddMinutes(idleMinutes);
        var cap = createdAt.AddMinutes(capMinutes);
        return idle < cap ? idle : cap;
    }

    public static bool IsExpired(PreviewSession s, DateTimeOffset now) => now >= s.ExpiresAt;

    /// <summary>Unguessable, DNS-label-safe name (also the ACI group name).</summary>
    public static string NewDnsLabel() => "tpv-" + Guid.NewGuid().ToString("n")[..12];
}
