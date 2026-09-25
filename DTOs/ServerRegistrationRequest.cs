// MasterServer/DTOs/ServerRegistrationRequest.cs
namespace MasterServer.DTOs;

// hostId identifies the provisioned VPS host; development registrations may omit it.
public record ServerRegistrationRequest(
    string Name,
    string IpAddress,
    int Port,
    string Region,
    bool IsOfficial,
    int MaxConcurrentMatches,
    string? CustomRulesJson,
    Guid? HostId = null
);
