using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public static class EdgeKindDetector
{
    /// <summary>QaFeedback if adding source→target closes a cycle (target already reaches source
    /// via Dependency edges); otherwise Dependency.</summary>
    public static EdgeKind Detect(IEnumerable<TaskEdge> existing, string sourceTaskId, string targetTaskId)
    {
        var adj = new Dictionary<string, List<string>>();
        foreach (var e in existing.Where(e => e.Kind == EdgeKind.Dependency))
        {
            if (!adj.TryGetValue(e.SourceTaskId, out var l)) adj[e.SourceTaskId] = l = new();
            l.Add(e.TargetTaskId);
        }
        // BFS from target; if we reach source, the new edge closes a loop.
        var seen = new HashSet<string> { targetTaskId };
        var queue = new Queue<string>(); queue.Enqueue(targetTaskId);
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            if (u == sourceTaskId) return EdgeKind.QaFeedback;
            if (adj.TryGetValue(u, out var outs))
                foreach (var v in outs) if (seen.Add(v)) queue.Enqueue(v);
        }
        return EdgeKind.Dependency;
    }
}
