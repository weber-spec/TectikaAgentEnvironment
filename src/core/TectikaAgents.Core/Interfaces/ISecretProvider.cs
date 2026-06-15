namespace TectikaAgents.Core.Interfaces;

public interface ISecretProvider
{
    Task<string> GetSecretAsync(string name, CancellationToken ct);
    Task SetSecretAsync(string name, string value, CancellationToken ct);
}
