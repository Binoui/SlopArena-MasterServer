// MasterServer/Lobbies/LobbyManager.cs
using System.Collections.Concurrent;

namespace MasterServer.Lobbies;

/// <summary>
/// In-memory lobby state authority (ADR-0004, see docs/adr/). One lobby per game server,
/// keyed by <c>serverId</c>. Lobbies are ephemeral: they live only while the
/// master server process is up, which is acceptable for the demo.
/// </summary>
public sealed class LobbyManager
{
    private readonly ConcurrentDictionary<Guid, Lobby> _lobbiesByServer = new();
    private readonly ConcurrentDictionary<string, Lobby> _lobbyByConnection = new();
    private readonly int _maxPlayersPerLobby;

    /// <param name="options">Lobby capacity options (issue #6); defaults to 4 per lobby.</param>
    public LobbyManager(LobbyOptions? options = null)
    {
        _maxPlayersPerLobby = LobbyOptions.ResolveMax(options);
    }

    /// <summary>
    /// Adds a player to the lobby for <paramref name="serverId"/>. The first
    /// player to join an empty lobby becomes the host (issue #32).
    /// </summary>
    public JoinLobbyResult JoinLobby(Guid serverId, string connectionId, long steamId, string username)
    {
        var lobby = _lobbiesByServer.GetOrAdd(serverId, _ => new Lobby(serverId, _maxPlayersPerLobby));

        // Already in this lobby (duplicate join) — return current state without
        // removing/re-adding to avoid a duplicate player.
        if (_lobbyByConnection.TryGetValue(connectionId, out var previous) && previous == lobby)
            return new JoinLobbyResult(true, null, lobby.GetPlayer(connectionId), lobby.Snapshot(), null);

        // Capacity is enforced atomically under the lobby lock, so concurrent
        // joins cannot oversubscribe. A rejected join leaves the connection
        // wherever it was (issue #6).
        var joined = lobby.AddPlayer(connectionId, steamId, username);
        if (joined is null)
            return new JoinLobbyResult(false,
                $"Lobby is full (max {_maxPlayersPerLobby} players).", null, null, null);

        // The join succeeded — depart any previous lobby and surface the departure
        // so the hub can announce it to the old lobby's survivors and drop the
        // old SignalR group membership.
        LeaveLobbyResult? departure = null;
        if (previous is not null)
        {
            var player = previous.RemovePlayer(connectionId);
            if (previous.IsEmpty)
                _lobbiesByServer.TryRemove(previous.ServerId, out _);
            departure = new LeaveLobbyResult(previous.ServerId, player, previous.Snapshot());
        }

        _lobbyByConnection[connectionId] = lobby;
        return new JoinLobbyResult(true, null, joined, lobby.Snapshot(), departure);
    }

    /// <summary>
    /// Removes a player from whatever lobby they were in (used on
    /// <c>OnDisconnectedAsync</c> and on explicit <c>LeaveLobby</c>).
    /// </summary>
    public LeaveLobbyResult LeaveLobby(string connectionId)
    {
        if (!_lobbyByConnection.TryRemove(connectionId, out var lobby))
            return new LeaveLobbyResult(null, null, null);

        var player = lobby.RemovePlayer(connectionId);
        // Reap empty lobbies so an idle server does not accumulate state, and
        // signal "nothing left to broadcast" via a null snapshot to the caller.
        if (lobby.IsEmpty)
        {
            _lobbiesByServer.TryRemove(lobby.ServerId, out _);
            return new LeaveLobbyResult(lobby.ServerId, player, null);
        }

        return new LeaveLobbyResult(lobby.ServerId, player, lobby.Snapshot());
    }

    /// <summary>
    /// Returns the lobby a connection currently belongs to, or null.
    /// </summary>
    public LobbySnapshot? GetSnapshot(string connectionId)
        => _lobbyByConnection.TryGetValue(connectionId, out var lobby) ? lobby.Snapshot() : null;

    /// <summary>
    /// Attempts a host start for the connection. Only the lobby host may start
    /// (issue #32). On success returns the roster for the match-start broadcast.
    /// </summary>
    public HostStartResult TryHostStart(string connectionId)
    {
        if (!_lobbyByConnection.TryGetValue(connectionId, out var lobby))
            return new HostStartResult(false, "You are not in a lobby.", null);

        if (!lobby.IsHostByConnection(connectionId, out var host))
            return new HostStartResult(false, "Only the host can start the match.", null);

        return new HostStartResult(true, null, new MatchStartingConfig(lobby.ServerId, lobby.Snapshot().Players));
    }

    /// <summary>
    /// Locks in a character selection for the connection's player (issue #34).
    /// The player may call this again to change their pick before the match
    /// starts. Returns the updated player + full snapshot for the
    /// <c>CharacterSelected</c> + <c>LobbyUpdated</c> broadcasts.
    /// </summary>
    public SelectCharacterResult SelectCharacter(string connectionId, string character)
    {
        if (!_lobbyByConnection.TryGetValue(connectionId, out var lobby))
            return new SelectCharacterResult(false, "You are not in a lobby.", null, null);

        var (player, snapshot) = lobby.SelectCharacter(connectionId, character);
        if (player is null)
            return new SelectCharacterResult(false, "You are not in a lobby.", null, null);

        return new SelectCharacterResult(true, null, player, snapshot);
    }

    /// <summary>
    /// Host-only: starts the actual match from char select (issue #34).
    /// Requires all players locked in and a minimum of 2 players. On success
    /// returns the final roster with character classes for the game server
    /// launch + <c>MatchStarted</c> broadcast.
    /// </summary>
    public StartMatchResult TryStartMatch(string connectionId)
    {
        if (!_lobbyByConnection.TryGetValue(connectionId, out var lobby))
            return new StartMatchResult(false, "You are not in a lobby.", null);

        if (!lobby.IsHostByConnection(connectionId, out _))
            return new StartMatchResult(false, "Only the host can start the match.", null);

        if (!lobby.IsAllLockedIn(out var lockedInError))
            return new StartMatchResult(false, lockedInError, null);

        var players = lobby.Snapshot().Players;
        var withEntityIds = players.Select((p, i) => p with { EntityId = i + 1 }).ToList();
        return new StartMatchResult(true, null,
            new MatchStartedConfig(lobby.ServerId, withEntityIds));
    }

    /// <summary>
    /// A single lobby: an ordered, locked player list. The first player is the
    /// host; on host departure the next-joined player is promoted.
    /// </summary>
    private sealed class Lobby(Guid serverId, int maxPlayers)
    {
        private readonly object _gate = new();
        private readonly List<PlayerState> _players = new();

        public Guid ServerId => serverId;

        /// <summary>
        /// Adds a player as the newest player. Returns null when the lobby is
        /// at capacity (issue #6) — the caller must treat that as a rejection.
        /// </summary>
        public LobbyPlayer? AddPlayer(string connectionId, long steamId, string username)
        {
            lock (_gate)
            {
                if (_players.Count >= maxPlayers)
                    return null;

                var isHost = _players.Count == 0;
                var player = new PlayerState(connectionId, steamId, username, null, false, isHost);
                _players.Add(player);
                return player.ToPlayer();
            }
        }

        public LobbyPlayer? GetPlayer(string connectionId)
        {
            lock (_gate)
            {
                var idx = _players.FindIndex(m => m.ConnectionId == connectionId);
                return idx >= 0 ? _players[idx].ToPlayer() : null;
            }
        }

        public LobbyPlayer? RemovePlayer(string connectionId)
        {
            lock (_gate)
            {
                var idx = _players.FindIndex(m => m.ConnectionId == connectionId);
                if (idx < 0) return null;

                var removed = _players[idx];
                _players.RemoveAt(idx);

                // Promote the now-first player to host when the host left.
                if (removed.IsHost && _players.Count > 0)
                    _players[0] = _players[0] with { IsHost = true };

                return removed.ToPlayer();
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_gate) { return _players.Count == 0; }
            }
        }

        public bool IsHostByConnection(string connectionId, out LobbyPlayer? host)
        {
            lock (_gate)
            {
                var idx = _players.FindIndex(m => m.ConnectionId == connectionId);
                if (idx < 0 || !_players[idx].IsHost)
                {
                    host = null;
                    return false;
                }
                host = _players[idx].ToPlayer();
                return true;
            }
        }

        public LobbySnapshot Snapshot()
        {
            lock (_gate)
            {
                return new LobbySnapshot(serverId, _players.Select(m => m.ToPlayer()).ToList());
            }
        }

        /// <summary>
        /// Locks in a character selection for the player on this connection
        /// (issue #34). Returns the updated player + snapshot, or null player
        /// if the connection is not in this lobby.
        /// </summary>
        public (LobbyPlayer? Player, LobbySnapshot Snapshot) SelectCharacter(
            string connectionId, string character)
        {
            lock (_gate)
            {
                var idx = _players.FindIndex(m => m.ConnectionId == connectionId);
                if (idx < 0)
                    return (null, Snapshot());

                _players[idx] = _players[idx] with
                {
                    Character = character,
                    LockedIn = true
                };
                return (_players[idx].ToPlayer(), Snapshot());
            }
        }

        /// <summary>
        /// Checks whether all players have locked in a character. Returns false
        /// with a descriptive error if not. Minimum 2 players required (issue #6).
        /// </summary>
        public bool IsAllLockedIn(out string? error)
        {
            lock (_gate)
            {
                if (_players.Count < LobbyLimits.MinPlayers)
                {
                    error = $"Need at least {LobbyLimits.MinPlayers} players to start.";
                    return false;
                }

                var unlocked = _players.FirstOrDefault(m => !m.LockedIn);
                if (unlocked != default)
                {
                    error = $"Waiting for {unlocked.Username} to lock in.";
                    return false;
                }

                error = null;
                return true;
            }
        }

        private readonly record struct PlayerState(
            string ConnectionId,
            long SteamId,
            string Username,
            string? Character,
            bool LockedIn,
            bool IsHost,
            int EntityId = 0)
        {
            public LobbyPlayer ToPlayer() => new(SteamId, Username, Character, LockedIn, IsHost, EntityId);
        }
    }
}
