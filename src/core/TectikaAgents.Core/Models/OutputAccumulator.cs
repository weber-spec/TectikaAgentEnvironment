namespace TectikaAgents.Core.Models;

/// <summary>Pure reducer that applies a round's <see cref="OutputOp"/>s to the
/// task's accumulated declared-output set. Never mutates the input list.</summary>
public static class OutputAccumulator
{
    public static List<Output> Apply(IReadOnlyList<Output> current, IReadOnlyList<OutputOp> ops)
    {
        var list = current.ToList();
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case OutputOpKind.Declare:
                    if (op.Declared is not null) list.Add(op.Declared);
                    break;
                case OutputOpKind.Update:
                    var existing = list.FirstOrDefault(o => o.Id == op.Id);
                    if (existing is not null)
                    {
                        if (op.Label is not null) existing.Label = op.Label;
                        if (op.Inline is not null) existing.Inline = op.Inline;
                    }
                    break;
                case OutputOpKind.Remove:
                    list.RemoveAll(o => o.Id == op.Id);
                    break;
            }
        }
        return list;
    }
}
