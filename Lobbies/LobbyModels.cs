// MasterServer/Lobbies/LobbyModels.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MasterServer.Lobbies;

/// <summary>
/// A player present in a lobby. Lobby state is in-memory and ephemeral
/// (see ADR-0004, docs/adr/): the master server is the lobby authority only while up.
/// </summary>
/// <remarks>
/// Glossary alignment (issue #7): the C# properties are <c>Username</c>/<c>Character</c>,
/// but the SignalR wire keys stay <c>name</c>/<c>characterSelection</c> (pinned via
/// <see cref="JsonPropertyNameAttribute"/>). The client (SlopArena repo) parses those
/// exact keys in <c>LobbyPayloadCodec</c>, so the wire cannot change until both repos
/// ship in lockstep.
/// </remarks>
public sealed record LobbyPlayer(
    long SteamId,
    [property: JsonPropertyName("name")] string Username,
    [property: JsonPropertyName("characterSelection")] string? Character,
    bool LockedIn,
    bool IsHost,
    int EntityId = 0);

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

/// <summary>Master-issued Steam routing coordinates; all identities serialize losslessly.</summary>
public sealed record SteamMatchDescriptor(
    string Transport,
    string ServerSteamId,
    Guid MatchId,
    int VirtualPort,
    int ProtocolVersion,
    string ContentHash,
    DateTimeOffset AdmissionExpiresAtUtc);

/// <summary>Match push sent only to its locked-in roster.</summary>
public sealed record MatchStartedConfig(
    Guid ServerId,
    IReadOnlyList<LobbyPlayer> Players,
    int MatchPort = 0,
    string ArenaName = "",
    JsonElement? Content = null,
    SteamMatchDescriptor? Descriptor = null);

/// <summary>
/// Result of a player entering a GameServer. <c>Success</c> means a waiting
/// roster slot was admitted. <c>ServerAdmitted</c> means authoritative Server
/// Chat membership was admitted; it remains true when the waiting roster is
/// full and <c>Error</c> is <c>lobby_full</c>-equivalent.
/// </summary>
public sealed record JoinLobbyResult(
    bool Success,
    string? Error,
    LobbyPlayer? Player,
    LobbySnapshot? Snapshot,
    LeaveLobbyResult? Departure,
    bool ServerAdmitted = false);

/// <summary>GameHost match-start response. Master forwards authoritative content unchanged.</summary>
public sealed record MatchLaunchResult(int MatchPort, JsonElement Content,
    SteamMatchDescriptor? Descriptor = null);

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
