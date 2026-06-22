using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class PreviewServiceTests
{
    sealed class FakeProvisioner : IPreviewProvisioner
    {
        public bool Fail; public List<string> Destroyed = new();
        public Task<PreviewProvisionResult> ProvisionAsync(GitHubRepoConnection r, string b, string? p, string dns, CancellationToken ct)
            => Fail ? throw new InvalidOperationException("boom")
                    : Task.FromResult(new PreviewProvisionResult($"{dns}.westeurope.azurecontainer.io", dns));
        public Task DestroyAsync(string name, CancellationToken ct) { Destroyed.Add(name); return Task.CompletedTask; }
        public Task<IReadOnlyList<PreviewGroupInfo>> ListPreviewGroupsAsync(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<PreviewGroupInfo>)new List<PreviewGroupInfo>());
    }

    sealed class NullSecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(string name, CancellationToken ct) => Task.FromResult("");
        public Task SetSecretAsync(string name, string value, CancellationToken ct) => Task.CompletedTask;
    }

    static async Task<(PreviewService svc, InMemoryCosmosDbService cosmos, FakeProvisioner prov)> MakeAsync(DateTimeOffset now)
    {
        var cosmos = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);
        await cosmos.CreateBoardAsync(new Board { Id = "b1", TenantId = "t1",
            GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", RepoUrl = "https://github.com/o/r", PatSecretName = "" } });
        var prov = new FakeProvisioner();
        var svc = new PreviewService(cosmos, prov, new NullSecretProvider(),
            new PreviewSettings { IdleMinutes = 15, CapMinutes = 45 }, () => now);
        return (svc, cosmos, prov);
    }

    [Fact]
    public async Task Start_provisions_and_marks_running()
    {
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var (svc, _, _) = await MakeAsync(now);
        var s = await svc.StartAsync("t1", "b1", "main", default);
        Assert.Equal(PreviewStatus.Running, s.Status);
        Assert.StartsWith("https://tpv-", s.Url);
        Assert.EndsWith(":8080", s.Url);
        Assert.Equal(now.AddMinutes(15), s.ExpiresAt);
    }

    [Fact]
    public async Task Start_replaces_existing_preview()
    {
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var (svc, _, prov) = await MakeAsync(now);
        var first = await svc.StartAsync("t1", "b1", "main", default);
        var second = await svc.StartAsync("t1", "b1", "dev", default);
        Assert.Contains(first.ContainerName!, prov.Destroyed);
        Assert.Equal("dev", second.Branch);
    }

    [Fact]
    public async Task Start_failure_marks_failed_with_error()
    {
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var (svc, _, prov) = await MakeAsync(now); prov.Fail = true;
        var s = await svc.StartAsync("t1", "b1", "main", default);
        Assert.Equal(PreviewStatus.Failed, s.Status);
        Assert.False(string.IsNullOrEmpty(s.Error));
    }

    [Fact]
    public async Task Start_without_github_throws()
    {
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var cosmos = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);
        await cosmos.CreateBoardAsync(new Board { Id = "b2", TenantId = "t1" });
        var svc = new PreviewService(cosmos, new FakeProvisioner(), new NullSecretProvider(),
            new PreviewSettings { IdleMinutes = 15, CapMinutes = 45 }, () => now);
        await Assert.ThrowsAsync<PreviewNotConnectedException>(() => svc.StartAsync("t1", "b2", "main", default));
    }

    [Fact]
    public async Task Heartbeat_extends_expiry()
    {
        var t0 = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var now = t0;
        var cosmos = new InMemoryCosmosDbService(NullLogger<InMemoryCosmosDbService>.Instance);
        await cosmos.CreateBoardAsync(new Board { Id = "b1", TenantId = "t1",
            GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", RepoUrl = "https://github.com/o/r", PatSecretName = "" } });
        var svc = new PreviewService(cosmos, new FakeProvisioner(), new NullSecretProvider(),
            new PreviewSettings { IdleMinutes = 15, CapMinutes = 45 }, () => now);
        await svc.StartAsync("t1", "b1", "main", default);
        now = t0.AddMinutes(10);
        var s = await svc.HeartbeatAsync("t1", "b1", default);
        Assert.Equal(t0.AddMinutes(25), s!.ExpiresAt);
    }
}
