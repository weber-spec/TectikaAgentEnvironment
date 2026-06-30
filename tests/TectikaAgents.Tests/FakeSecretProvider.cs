using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.Core.Interfaces;

public sealed class FakeSecretProvider : ISecretProvider
{
    public readonly Dictionary<string, string> Store = new();

    public Task<string> GetSecretAsync(string name, CancellationToken ct)
        => Task.FromResult(Store.TryGetValue(name, out var v) ? v : string.Empty);

    public Task SetSecretAsync(string name, string value, CancellationToken ct)
    {
        if (value == "") Store.Remove(name); else Store[name] = value;
        return Task.CompletedTask;
    }
}
