// MasterServer/Lobbies/LobbyLimits.cs
namespace MasterServer.Lobbies;

/// <summary>
/// The single home for the player-count contract (issue #6): a match holds
/// 2–4 players. Every layer (lobby join, match start, match launch) derives
/// its bounds from here instead of re-hard-coding them.
/// </summary>
public static class LobbyLimits
{
    /// <summary>Minimum players to start a match — the game rule.</summary>
    public const int MinPlayers = 2;

    /// <summary>
    /// The player-count contract maximum — matches the persisted Match capacity
    /// (Player1–Player4 columns). Configuration may lower, not raise, it.
    /// </summary>
    public const int MaxPlayers = 4;
}

/// <summary>
/// Lobby capacity options, bound from the <c>Lobby</c> configuration section.
/// Registered once and shared by the lobby manager and the match launcher so
/// both enforce the same maximum.
/// </summary>
public sealed record LobbyOptions(int MaxPlayersPerLobby = LobbyLimits.MaxPlayers)
{
    /// <summary>Resolves the effective maximum, defaulting when no options are supplied.</summary>
    public static int ResolveMax(LobbyOptions? options)
        => (options ?? new LobbyOptions()).MaxPlayersPerLobby;
}
