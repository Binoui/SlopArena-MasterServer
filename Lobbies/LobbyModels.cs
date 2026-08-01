// MasterServer/Lobbies/LobbyModels.cs
namespace MasterServer.Lobbies;

/// <summary>
/// A player present in a lobby. Lobby state is in-memory and ephemeral
/// (see ADR-0004): the master server is the lobby authority only while up.
/// </summary>
public sealed record LobbyPlayer(
    long SteamId,
    string Name,
    string? CharacterSelection,
    bool LockedIn,
    bool IsHost);

/// <summary>
/// Full lobby membership snapshot pushed on any change via <c>LobbyUpdated</c>.
/// </summary>
public sealed record LobbySnapshot(Guid ServerId, IReadOnlyList<LobbyPlayer> Players);

/// <summary>
/// Payload of the <c>MatchStarting</c> push broadcast when the host starts
/// (transitions lobby → char select). Character selections are null at this
/// point; players pick on the char-select screen.
/// </summary>
public sealed record MatchStartingConfig(Guid ServerId, IReadOnlyList<LobbyPlayer> Players);

/// <summary>
/// Payload of the <c>MatchStarted</c> push broadcast when the host starts the
/// actual match from char select (all players locked in). Carries the final
/// roster with character classes the game server will spawn.
/// </summary>
public sealed record MatchStartedConfig(Guid ServerId, IReadOnlyList<LobbyPlayer> Players);

/// <summary>
/// Result of a player joining a lobby. The hub uses this to perform the
/// SignalR group join and the <c>PlayerJoined</c>/<c>LobbyUpdated</c> broadcasts.
/// <c>Departure</c> is non-null when the connection was previously in a
/// different lobby — the hub must announce the departure to the old lobby.
/// </summary>
public sealed record JoinLobbyResult(
    LobbyPlayer Player,
    LobbySnapshot Snapshot,
    LeaveLobbyResult? Departure);

/// <summary>
/// Result of a player leaving a lobby (or disconnecting). <c>ServerId</c> is null
/// when the connection was not in any lobby.
/// </summary>
public sealed record LeaveLobbyResult(
    Guid? ServerId,
    LobbyPlayer? Player,
    LobbySnapshot? Snapshot);

/// <summary>
/// Result of a host calling <c>HostStart</c>. Non-host callers get <c>Success=false</c>.
/// </summary>
public sealed record HostStartResult(bool Success, string? Error, MatchStartingConfig? Config);

/// <summary>
/// Result of a player calling <c>SelectCharacter</c> (lock-in). Carries the
/// updated player and full snapshot for the <c>CharacterSelected</c> +
/// <c>LobbyUpdated</c> broadcasts.
/// </summary>
public sealed record SelectCharacterResult(
    bool Success,
    string? Error,
    LobbyPlayer? Player,
    LobbySnapshot? Snapshot);

/// <summary>
/// Result of a host calling <c>StartMatch</c> from char select. Requires all
/// players locked in (minimum 2). Non-host or unlocked players get
/// <c>Success=false</c>.
/// </summary>
public sealed record StartMatchResult(bool Success, string? Error, MatchStartedConfig? Config);
