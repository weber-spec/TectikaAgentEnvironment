using System;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class PreviewLifecycleTests
{
    static readonly DateTimeOffset T0 = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Expiry_is_lastActivity_plus_idle_when_under_cap()
    {
        var exp = PreviewLifecycle.ComputeExpiry(createdAt: T0, lastActivityAt: T0.AddMinutes(5),
            idleMinutes: 15, capMinutes: 45);
        Assert.Equal(T0.AddMinutes(20), exp); // 5 + 15
    }

    [Fact]
    public void Expiry_is_capped_at_createdAt_plus_cap()
    {
        var exp = PreviewLifecycle.ComputeExpiry(createdAt: T0, lastActivityAt: T0.AddMinutes(40),
            idleMinutes: 15, capMinutes: 45);
        Assert.Equal(T0.AddMinutes(45), exp); // 40 + 15 = 55, capped to 45
    }

    [Fact]
    public void IsExpired_true_when_now_past_expiry()
    {
        var s = new PreviewSession { CreatedAt = T0, ExpiresAt = T0.AddMinutes(10) };
        Assert.True(PreviewLifecycle.IsExpired(s, T0.AddMinutes(11)));
        Assert.False(PreviewLifecycle.IsExpired(s, T0.AddMinutes(9)));
    }

    [Fact]
    public void NewDnsLabel_has_prefix_and_is_lowercase_alnum()
    {
        var label = PreviewLifecycle.NewDnsLabel();
        Assert.StartsWith("tpv-", label);
        Assert.Matches("^tpv-[0-9a-f]{12}$", label);
        Assert.NotEqual(PreviewLifecycle.NewDnsLabel(), label); // unguessable / unique
    }
}
