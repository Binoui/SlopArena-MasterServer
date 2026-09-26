// MasterServer/Data/Models/GameServer.cs
namespace MasterServer.Data.Models;

public class GameServer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    /// <summary>Current Steam GameServer identity; decimal string avoids JSON 64-bit precision loss.</summary>
    public string? SteamId { get; set; }
    public int ProtocolVersion { get; set; }
    /// <summary>Trusted process incarnation; rotation cancels pre-restart matches.</summary>
    public Guid? InstanceId { get; set; }
    /// <summary>SHA-256 digest of the immutable admitted roster catalog advertised at registration.</summary>
    public string? CatalogHash { get; set; }
    public string Region { get; set; } = string.Empty;
    public bool IsOfficial { get; set; }
    public int MaxConcurrentMatches { get; set; }
    public int CurrentMatches { get; set; }
    public string? CustomRulesJson { get; set; }
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public string ApiToken { get; set; } = string.Empty; // For auth
}
