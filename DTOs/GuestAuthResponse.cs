// MasterServer/DTOs/GuestAuthResponse.cs
namespace MasterServer.DTOs;

public record GuestAuthResponse(string Token, long SteamId);

public record GuestUserInfo(long SteamId, string Username, int Mmr);
