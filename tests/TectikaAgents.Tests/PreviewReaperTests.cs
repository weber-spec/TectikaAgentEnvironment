using System;
using System.Collections.Generic;
using System.Linq;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class PreviewReaperTests
{
    static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Selects_expired_sessions_for_teardown()
    {
        var sessions = new[]
        {
            new PreviewSession { Id = "a", ContainerName = "a", ExpiresAt = Now.AddMinutes(-1), Status = PreviewStatus.Running },
            new PreviewSession { Id = "b", ContainerName = "b", ExpiresAt = Now.AddMinutes(5),  Status = PreviewStatus.Running },
        };
        var expired = PreviewReaper.SelectExpired(sessions, Now).Select(s => s.Id).ToList();
        Assert.Equal(new[] { "a" }, expired);
    }

    [Fact]
    public void Selects_orphan_groups_not_in_active_set()
    {
        var active = new[] { new PreviewSession { ContainerName = "keep" } };
        var groups = new[] { new PreviewGroupInfo("keep", "o1"), new PreviewGroupInfo("orphan", "o2") };
        var orphans = PreviewReaper.SelectOrphans(groups, active).Select(g => g.Name).ToList();
        Assert.Equal(new[] { "orphan" }, orphans);
    }
}
