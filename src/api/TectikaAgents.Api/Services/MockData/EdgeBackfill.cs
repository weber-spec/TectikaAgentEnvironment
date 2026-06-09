using Microsoft.Azure.Cosmos;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services.MockData;

internal static class EdgeBackfill
{
    public static async Task RunAsync(CosmosClient client, string dbName, ICosmosDbService cosmos, ILogger log, CancellationToken ct = default)
    {
        var tasks = client.GetContainer(dbName, CosmosDbService.TasksContainer);
        var q = new QueryDefinition("SELECT c.id, c.boardId, c.tenantId, c.downstreamTaskIds FROM c");
        var iter = tasks.GetItemQueryIterator<RawTask>(q);
        int made = 0;
        while (iter.HasMoreResults)
            foreach (var t in await iter.ReadNextAsync(ct))
                foreach (var dst in t.DownstreamTaskIds ?? new())
                {
                    var id = TaskEdge.MakeId(t.Id, dst);
                    if (await cosmos.GetEdgeAsync(t.BoardId, id, ct) is not null) continue;
                    var existing = await cosmos.GetEdgesByBoardAsync(t.BoardId, ct);
                    var kind = EdgeKindDetector.Detect(existing, t.Id, dst);
                    await cosmos.CreateEdgeAsync(new TaskEdge {
                        Id = id, TenantId = t.TenantId, BoardId = t.BoardId,
                        SourceTaskId = t.Id, TargetTaskId = dst, Kind = kind }, ct);
                    made++;
                }
        log.LogInformation("Edge backfill complete — {Made} edges created.", made);
    }

    private class RawTask
    {
        public string Id { get; set; } = "";
        public string BoardId { get; set; } = "";
        public string TenantId { get; set; } = "default";
        public List<string>? DownstreamTaskIds { get; set; }
    }
}
