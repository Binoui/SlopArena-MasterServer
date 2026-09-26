// MasterServer/DTOs/HeartbeatRequest.cs
namespace MasterServer.DTOs;

public record HeartbeatRequest(int CurrentMatches, string? SteamId = null,
    Guid? InstanceId = null, string? CatalogHash = null);
