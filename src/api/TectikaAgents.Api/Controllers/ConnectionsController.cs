using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

/// <summary>Tenant-level (organization) connection registry — the single source of truth boards and agents
/// reference by id. Credentials are validated before storage and kept only in Key Vault; Cosmos holds the
/// <see cref="Connection"/> metadata + a secret reference. Board bindings (which connections a board enables)
/// live in <see cref="BoardConnectionsController"/>.</summary>
[ApiController]
[Route("api/connections")]
[Authorize]
public class ConnectionsController : ControllerBase
{
    /// <summary>Synthetic id for the always-connected Foundry system connection (never persisted).</summary>
    public const string FoundrySystemId = "conn_foundry_system";

    public sealed record AuthFieldDto(string Name, string Label, string Type, string Hint, bool Secret);
    public sealed record CatalogDto(
        string Id, string DisplayName, string Description, string Category, string IconKey, string? HelpUrl,
        bool SupportsMultiple, IReadOnlyList<AuthFieldDto> AuthFields, int ReadToolCount, int WriteToolCount);

    /// <summary>Create request. <see cref="Secrets"/> is keyed by catalog AuthField name (e.g. {"token": "…"}).
    /// <see cref="Metadata"/> carries non-secret config (e.g. {"defaultFrom": "…"}).</summary>
    public sealed record CreateConnectionRequest(
        string CatalogId, string? DisplayName, string? Scope,
        Dictionary<string, string>? Secrets, Dictionary<string, string>? Metadata);

    private readonly ICosmosDbService _cosmos;
    private readonly IMcpGateway _gateway;
    private readonly ISecretProvider _secrets;
    private readonly TectikaAgents.AgentRuntime.FoundryConnectionsCatalog _foundry;
    private readonly IClaudeModelCatalog _claudeModels;
    private readonly IReadOnlyDictionary<string, IFirstPartyConnector> _connectors;

    public ConnectionsController(ICosmosDbService cosmos, IMcpGateway gateway, ISecretProvider secrets,
        TectikaAgents.AgentRuntime.FoundryConnectionsCatalog foundry, IClaudeModelCatalog claudeModels,
        IEnumerable<IFirstPartyConnector>? connectors = null)
    {
        _cosmos = cosmos;
        _gateway = gateway;
        _secrets = secrets;
        _foundry = foundry;
        _claudeModels = claudeModels;
        _connectors = (connectors ?? Array.Empty<IFirstPartyConnector>())
            .ToDictionary(c => c.CatalogId, StringComparer.Ordinal);
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId   => User.FindFirst("preferred_username")?.Value ?? "unknown";

    // ── Catalog (Available tab) ──────────────────────────────────────────────────
    [HttpGet("catalog")]
    public IActionResult Catalog() =>
        Ok(McpCatalog.Entries.Select(e => new CatalogDto(
            e.Id, e.DisplayName, e.Description, e.Category, e.Icon, e.HelpUrl, e.SupportsMultiple,
            e.EffectiveAuthFields.Select(f => new AuthFieldDto(f.Name, f.Label, f.Type, f.Hint, f.Secret)).ToList(),
            e.Tools.Count(t => !t.IsWrite), e.Tools.Count(t => t.IsWrite))));

    // ── Active connections ───────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var stored = (await _cosmos.GetConnectionsAsync(TenantId, ct)).ToList();
        // Foundry arrives pre-connected — inject it as a read-only system row so the UI can show it.
        stored.Insert(0, FoundrySystemConnection(TenantId));
        // The Foundry project's own connections (Azure OpenAI, Search, …) are live, read-only system rows.
        var projectConns = (await _foundry.ListAsync(ct))
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .Select(c => new Connection
            {
                Id = $"foundry-conn-{c.Name}",
                TenantId = TenantId,
                CatalogId = "foundry",
                Category = ConnectionCategory.Model,
                DisplayName = c.Name!,
                Status = ConnectionStatus.Connected,
                IsSystem = true,
                Scope = ConnectionScope.Organization,
                Metadata = new() { ["foundryType"] = c.Type ?? "Connection" },
            });
        stored.AddRange(projectConns);
        return Ok(stored);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateConnectionRequest req, CancellationToken ct)
    {
        // NB: never log req.Secrets — they are third-party credentials.
        var entry = McpCatalog.Find(req.CatalogId);
        if (entry is null) return BadRequest(new { error = "UnknownIntegration" });

        var fields = entry.EffectiveAuthFields;
        var secrets = req.Secrets ?? new();
        var missing = fields.Where(f => f.Secret && string.IsNullOrWhiteSpace(secrets.GetValueOrDefault(f.Name)))
                            .Select(f => f.Name).ToList();
        if (missing.Count > 0) return BadRequest(new { error = "MissingFields", fields = missing });

        // Primary credential (first auth field) used for validation + single-field secret storage.
        var primary = secrets.GetValueOrDefault(fields[0].Name) ?? "";

        // Only agent-tool integrations have a live endpoint/connector to validate against. Model providers
        // (Anthropic/Foundry) and source-control (GitHub) are resolved elsewhere at use time — just store them.
        if (entry.Category == ConnectionCategory.AgentTool)
        {
            try { await ValidateCredentialAsync(entry, primary, ct); }
            catch (Exception ex) { return BadRequest(new { error = "ValidationFailed", detail = ex.Message }); }
        }

        var conn = new Connection
        {
            TenantId = TenantId,
            CatalogId = entry.Id,
            Category = entry.Category,
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? entry.DisplayName : req.DisplayName!.Trim(),
            Status = ConnectionStatus.Connected,
            LastValidatedAt = DateTimeOffset.UtcNow,
            CreatedBy = UserId,
            Metadata = req.Metadata ?? new(),
            Scope = Enum.TryParse<ConnectionScope>(req.Scope, ignoreCase: true, out var s) ? s : ConnectionScope.Organization,
        };
        // Key Vault secret names allow only [0-9a-zA-Z-]; conn.Id is `conn_<hex>` (underscore), so sanitize.
        conn.SecretName = $"conn-{conn.Id}".Replace('_', '-');
        // Single-field auth stores the raw token (what IFirstPartyConnector/IMcpGateway expect); multi-field
        // stores a JSON object keyed by field name.
        var secretValue = fields.Count == 1
            ? primary
            : JsonSerializer.Serialize(fields.Where(f => f.Secret).ToDictionary(f => f.Name, f => secrets.GetValueOrDefault(f.Name) ?? ""));
        await _secrets.SetSecretAsync(conn.SecretName, secretValue, ct);

        var saved = await _cosmos.UpsertConnectionAsync(conn, ct);
        return Ok(saved);
    }

    [HttpPost("{connectionId}/validate")]
    public async Task<IActionResult> Validate(string connectionId, CancellationToken ct)
    {
        var conn = await _cosmos.GetConnectionAsync(TenantId, connectionId, ct);
        if (conn is null) return NotFound();
        var entry = McpCatalog.Find(conn.CatalogId);
        if (entry is null) return BadRequest(new { error = "UnknownIntegration" });

        if (entry.Category != ConnectionCategory.AgentTool)
        {
            // Model + source-control have no live endpoint here; treat presence of the secret as validity.
            var secret = await _secrets.GetSecretAsync(conn.SecretName, ct);
            conn.Status = string.IsNullOrEmpty(secret) ? ConnectionStatus.Error : ConnectionStatus.Connected;
        }
        else
        {
            var token = await _secrets.GetSecretAsync(conn.SecretName, ct);
            try
            {
                if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Credential missing.");
                await ValidateCredentialAsync(entry, token, ct);
                conn.Status = ConnectionStatus.Connected;
            }
            catch { conn.Status = ConnectionStatus.Error; }
        }
        conn.LastValidatedAt = DateTimeOffset.UtcNow;
        var saved = await _cosmos.UpsertConnectionAsync(conn, ct);
        return Ok(saved);
    }

    // ── Live model list for a Claude (Anthropic) connection ──────────────────────
    /// <summary>Model ids available to this Anthropic connection, fetched live from Anthropic's /v1/models
    /// (curated fallback for OAuth connections or on failure). Powers the Claude model picker.</summary>
    [HttpGet("{connectionId}/models")]
    public async Task<IActionResult> Models(string connectionId, CancellationToken ct)
    {
        var conn = await _cosmos.GetConnectionAsync(TenantId, connectionId, ct);
        if (conn is null) return NotFound();
        if (!string.Equals(conn.CatalogId, "anthropic", StringComparison.Ordinal))
            return BadRequest(new { error = "NotAClaudeConnection" });
        // The catalog degrades to a curated list internally and never throws; the 502 is a defensive net.
        try { return Ok(await _claudeModels.ListModelsAsync(conn, ct)); }
        catch (Exception) { return StatusCode(StatusCodes.Status502BadGateway, new { error = "Could not load Claude models." }); }
    }

    [HttpDelete("{connectionId}")]
    public async Task<IActionResult> Delete(string connectionId, CancellationToken ct)
    {
        var conn = await _cosmos.GetConnectionAsync(TenantId, connectionId, ct);
        if (conn is null) return NotFound();
        await _cosmos.DeleteConnectionAsync(TenantId, connectionId, ct);
        // Best-effort secret cleanup (empty value removes the key per secret-provider convention).
        try { await _secrets.SetSecretAsync(conn.SecretName, string.Empty, ct); } catch { /* ignore */ }
        return NoContent();
    }

    /// <summary>Validate a credential before storing it. Throws if the token is rejected. Routes by backend:
    /// remote MCP entries connect + list tools; first-party entries validate through their connector.</summary>
    private async Task ValidateCredentialAsync(McpCatalog.CatalogEntry entry, string token, CancellationToken ct)
    {
        if (entry.Backend == McpBackend.FirstParty)
        {
            if (!_connectors.TryGetValue(entry.Id, out var connector))
                throw new InvalidOperationException("No connector is configured for this integration.");
            await connector.ValidateAsync(token, ct);
        }
        else
        {
            await _gateway.ListToolsAsync(
                new McpServerTarget(entry.Endpoint, entry.AuthHeader, entry.AuthScheme, token), ct);
        }
    }

    /// <summary>The Foundry model provider surfaces as an always-connected system connection (config-driven,
    /// no user credential). Synthesized per request; never persisted.</summary>
    private static Connection FoundrySystemConnection(string tenantId) => new()
    {
        Id = FoundrySystemId,
        TenantId = tenantId,
        CatalogId = "foundry",
        Category = ConnectionCategory.Model,
        DisplayName = "Azure AI Foundry",
        Status = ConnectionStatus.Connected,
        IsSystem = true,
        Scope = ConnectionScope.Organization,
    };
}
