using Microsoft.Azure.Cosmos;
using System.Text.Json.Serialization;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services.MockData;

internal static class EdgeBackfill
{
    public static async Task RunAsync(CosmosClient client, string dbName, ICosmosDbService cosmos, ILogger log, CancellationToken ct = default)
    {
        // Raw projection: downstreamTaskIds no longer exists on the AgentTask model, so read it straight from the stored doc.
        var tasks = client.GetContainer(dbName, CosmosDbService.TasksContainer);
        var q = new QueryDefinition("SELECT c.id, c.boardId, c.tenantId, c.downstreamTaskIds FROM c");
        var iter = tasks.GetItemQueryIterator<RawTask>(q);
        int made = 0;

        var byBoard = new Dictionary<string, List<TaskEdge>>();
        async Task<List<TaskEdge>> EdgesFor(string boardId)
        {
            if (!byBoard.TryGetValue(boardId, out var list))
                byBoard[boardId] = list = (await cosmos.GetEdgesByBoardAsync(boardId, ct)).ToList();
            return list;
        }

        while (iter.HasMoreResults)
            foreach (var t in await iter.ReadNextAsync(ct))
                foreach (var dst in t.DownstreamTaskIds ?? new())
                {
                    var id = TaskEdge.MakeId(t.Id, dst);
                    if (await cosmos.GetEdgeAsync(t.BoardId, id, ct) is not null) continue;
                    var edges = await EdgesFor(t.BoardId);
                    var kind = EdgeKindDetector.Detect(edges, t.Id, dst);
                    var created = await cosmos.CreateEdgeAsync(new TaskEdge {
                        Id = id, TenantId = t.TenantId, BoardId = t.BoardId,
                        SourceTaskId = t.Id, TargetTaskId = dst, Kind = kind }, ct);
                    edges.Add(created);
                    made++;
                }
        log.LogInformation("Edge backfill complete — {Made} edges created.", made);
    }

    private class RawTask
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("boardId")] public string BoardId { get; set; } = "";
        [JsonPropertyName("tenantId")] public string TenantId { get; set; } = "default";
        [JsonPropertyName("downstreamTaskIds")] public List<string>? DownstreamTaskIds { get; set; }
    }
}
