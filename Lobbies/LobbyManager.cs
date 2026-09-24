// MasterServer/Lobbies/LobbyManager.cs
using System.Collections.Concurrent;

namespace MasterServer.Lobbies;

/// <summary>
/// In-memory lobby and authoritative GameServer-membership state. Waiting-room
/// roster capacity is deliberately separate from server membership: once a
/// match launches, its players leave the roster but remain members of the
/// GameServer channel until they explicitly leave or switch.
/// </summary>
public sealed class LobbyManager
{
    private const int MaxRememberedMemberships = 1024;
    private static readonly TimeSpan RememberedMembershipLifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<Guid, Lobby> _lobbiesByServer = new();
    private readonly ConcurrentDictionary<string, Lobby> _lobbyByConnection = new();
    private readonly ConcurrentDictionary<string, ServerMembership> _serverByConnection = new();
    private readonly Dictionary<long, RememberedMembership> _rememberedBySteamId = new();
    private readonly object _gate = new();
    private readonly int _maxPlayersPerLobby;
    private long _membershipAccess;

    /// <param name="options">Lobby capacity options (issue #6); defaults to 4 per lobby.</param>
    public LobbyManager(LobbyOptions? options = null)
    {
        _maxPlayersPerLobby = LobbyOptions.ResolveMax(options);
    }

    /// <summary>
    /// Admit a connection to a GameServer and, when a waiting slot exists, its
    /// waiting roster. A full waiting roster does not reject GameServer chat:
    /// the result is unsuccessful for lobby UI but <see cref="ServerAdmitted"/>
    /// remains true and the caller has authoritative Server Chat membership.
    /// </summary>
    public JoinLobbyResult JoinLobby(Guid serverId, string connectionId, long steamId, string username)
    {
        lock (_gate)
        {
            PruneRememberedLocked();
            var lobby = _lobbiesByServer.GetOrAdd(serverId, _ => new Lobby(serverId, _maxPlayersPerLobby));

            if (_serverByConnection.TryGetValue(connectionId, out var current)
                && current.ServerId == serverId
                && _lobbyByConnection.TryGetValue(connectionId, out var currentLobby)
                && currentLobby == lobby)
            {
                return new JoinLobbyResult(true, null, currentLobby.GetPlayer(connectionId),
                    currentLobby.Snapshot(), null, ServerAdmitted: true);
            }

            var joined = lobby.AddPlayer(connectionId, steamId, username);
            var departure = RemovePreviousMembershipLocked(connectionId, preserveRecent: false);
            var admittedPlayer = joined ?? new LobbyPlayer(steamId, username, null, false, false);
            _serverByConnection[connectionId] = new ServerMembership(serverId, admittedPlayer);
            RememberLocked(steamId, serverId, admittedPlayer);

            if (joined is null)
            {
                return new JoinLobbyResult(
                    false,
                    $"Lobby is full (max {_maxPlayersPerLobby} players).",
                    null,
                    null,
                    departure,
                    ServerAdmitted: true);
            }

            _lobbyByConnection[connectionId] = lobby;
            _serverByConnection[connectionId] = new ServerMembership(serverId, joined);
            RememberLocked(steamId, serverId, joined);
            return new JoinLobbyResult(true, null, joined, lobby.Snapshot(), departure, ServerAdmitted: true);
        }
    }

    /// <summary>
    /// Reconnect-only admission for an active match connection. It validates a
    /// remembered identity/server pair and restores only Server Chat membership;
    /// it never adds a waiting-roster player. The hub validates GameServer
    /// freshness before calling this method.
    /// </summary>
    public bool ResumeServer(Guid serverId, string connectionId, long steamId, string username, out string? error)
    {
        lock (_gate)
        {
            PruneRememberedLocked();
            if (_serverByConnection.TryGetValue(connectionId, out var current))
            {
                if (current.ServerId == serverId)
                {
                    error = null;
                    return true;
                }
                error = "already_joined";
                return false;
            }

            foreach (var (candidateConnection, membership) in _serverByConnection)
            {
                if (candidateConnection != connectionId && membership.Player.SteamId == steamId)
                {
                    error = "already_joined";
                    return false;
                }
            }

            if (!_rememberedBySteamId.TryGetValue(steamId, out var remembered)
                || remembered.ServerId != serverId)
            {
                error = "not_admitted";
                return false;
            }

            var player = remembered.Player with { Username = username };
            _serverByConnection[connectionId] = new ServerMembership(serverId, player);
            RememberLocked(steamId, serverId, player);
            error = null;
            return true;
        }
    }

    /// <summary>
    /// Explicitly leaves both the waiting roster and authoritative server
    /// membership. This clears the remembered reconnect admission.
    /// </summary>
    public LeaveLobbyResult LeaveLobby(string connectionId, long steamId)
    {
        lock (_gate)
        {
            _rememberedBySteamId.Remove(steamId);
            if (!_serverByConnection.TryGetValue(connectionId, out _))
                return new LeaveLobbyResult(null, null, null);

            return RemovePreviousMembershipLocked(connectionId, preserveRecent: true)
                ?? new LeaveLobbyResult(null, null, null);
        }
    }

    /// <summary>
    /// Disconnect cleanup removes the connection's live membership and waiting
    /// slot but preserves the bounded remembered server admission for
    /// <see cref="ResumeServer"/>.
    /// </summary>
    public LeaveLobbyResult DisconnectConnection(string connectionId)
    {
        lock (_gate)
            return RemovePreviousMembershipLocked(connectionId, preserveRecent: true)
                ?? new LeaveLobbyResult(null, null, null);
    }

    /// <summary>The authoritative joined GameServer, including active matches.</summary>
    public Guid? GetServerId(string connectionId)
        => _serverByConnection.TryGetValue(connectionId, out var membership) ? membership.ServerId : null;

    /// <summary>Removes launched players from the waiting roster but keeps server membership.</summary>
    public IReadOnlyList<string> ReleaseMatchRoster(Guid serverId, IReadOnlyList<long> steamIds)
    {
        lock (_gate)
        {
            if (!_lobbiesByServer.TryGetValue(serverId, out var lobby))
                return Array.Empty<string>();

            var ids = steamIds.ToHashSet();
            var removed = lobby.RemovePlayers(ids);
            foreach (var (connectionId, player) in removed)
            {
                _lobbyByConnection.TryRemove(connectionId, out _);
                if (_serverByConnection.TryGetValue(connectionId, out var membership)
                    && membership.ServerId == serverId)
                {
                    var retained = membership with { Player = player };
                    _serverByConnection[connectionId] = retained;
                    RememberLocked(player.SteamId, serverId, player);
                }
            }

            if (lobby.IsEmpty)
                _lobbiesByServer.TryRemove(serverId, out _);
            return removed.Select(item => item.ConnectionId).ToArray();
        }
    }

    /// <summary>Returns the waiting-roster snapshot for a connection, if any.</summary>
    public LobbySnapshot? GetSnapshot(string connectionId)
    {
        lock (_gate)
            return _lobbyByConnection.TryGetValue(connectionId, out var lobby) ? lobby.Snapshot() : null;
    }

    /// <summary>Attempts a host start for the waiting roster.</summary>
    public HostStartResult TryHostStart(string connectionId)
    {
        lock (_gate)
        {
            if (!_lobbyByConnection.TryGetValue(connectionId, out var lobby))
                return new HostStartResult(false, "You are not in a lobby.", null);
            if (!lobby.IsHostByConnection(connectionId, out _))
                return new HostStartResult(false, "Only the host can start the match.", null);
            return new HostStartResult(true, null, new MatchStartingConfig(lobby.ServerId, lobby.Snapshot().Players));
        }
    }

    /// <summary>Locks in a character selection for the waiting roster.</summary>
    public SelectCharacterResult SelectCharacter(string connectionId, string character)
    {
        lock (_gate)
        {
            if (!_lobbyByConnection.TryGetValue(connectionId, out var lobby))
                return new SelectCharacterResult(false, "You are not in a lobby.", null, null);
            var (player, snapshot) = lobby.SelectCharacter(connectionId, character);
            if (player is null)
                return new SelectCharacterResult(false, "You are not in a lobby.", null, null);
            if (_serverByConnection.TryGetValue(connectionId, out var membership))
            {
                var updated = membership with { Player = player };
                _serverByConnection[connectionId] = updated;
                RememberLocked(player.SteamId, membership.ServerId, player);
            }
            return new SelectCharacterResult(true, null, player, snapshot);
        }
    }

    /// <summary>Host-only match start from stage select.</summary>
    public StartMatchResult TryStartMatch(string connectionId, string arena)
    {
        lock (_gate)
        {
            if (!_lobbyByConnection.TryGetValue(connectionId, out var lobby))
                return new StartMatchResult(false, "You are not in a lobby.", null);
            if (!lobby.IsHostByConnection(connectionId, out _))
                return new StartMatchResult(false, "Only the host can start the match.", null);
            if (!lobby.IsAllLockedIn(out var lockedInError))
                return new StartMatchResult(false, lockedInError, null);
            if (string.IsNullOrWhiteSpace(arena))
                return new StartMatchResult(false, "Arena name missing.", null);

            var players = lobby.Snapshot().Players;
            var withEntityIds = players.Select((p, i) => p with { EntityId = i + 1 }).ToList();
            return new StartMatchResult(true, null,
                new MatchStartedConfig(lobby.ServerId, withEntityIds, ArenaName: arena));
        }
    }

    /// <summary>Checks whether every waiting-roster player has locked in.</summary>
    public bool IsAllLockedIn(string connectionId, out string? error)
    {
        lock (_gate)
        {
            if (!_lobbyByConnection.TryGetValue(connectionId, out var lobby))
            {
                error = "You are not in a lobby.";
                return false;
            }
            return lobby.IsAllLockedIn(out error);
        }
    }

    private LeaveLobbyResult? RemovePreviousMembershipLocked(string connectionId, bool preserveRecent)
    {
        _serverByConnection.TryRemove(connectionId, out var membership);
        _lobbyByConnection.TryRemove(connectionId, out var lobby);
        if (membership is null)
            return null;

        if (!preserveRecent)
            _rememberedBySteamId.Remove(membership.Player.SteamId);

        LobbyPlayer? player = membership.Player;
        LobbySnapshot? snapshot = null;
        if (lobby is not null)
        {
            player = lobby.RemovePlayer(connectionId) ?? player;
            if (lobby.IsEmpty)
                _lobbiesByServer.TryRemove(lobby.ServerId, out _);
            else
                snapshot = lobby.Snapshot();
        }
        return new LeaveLobbyResult(membership.ServerId, player, snapshot);
    }

    private void RememberLocked(long steamId, Guid serverId, LobbyPlayer player)
    {
        _membershipAccess++;
        _rememberedBySteamId[steamId] = new RememberedMembership(serverId, player, DateTime.UtcNow, _membershipAccess);
        if (_rememberedBySteamId.Count <= MaxRememberedMemberships)
            return;

        var oldest = _rememberedBySteamId.MinBy(pair => pair.Value.Access);
        _rememberedBySteamId.Remove(oldest.Key);
    }

    private void PruneRememberedLocked()
    {
        var cutoff = DateTime.UtcNow - RememberedMembershipLifetime;
        foreach (var key in _rememberedBySteamId
                     .Where(pair => pair.Value.LastSeenUtc < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
            _rememberedBySteamId.Remove(key);
    }

    private sealed record ServerMembership(Guid ServerId, LobbyPlayer Player);
    private sealed record RememberedMembership(Guid ServerId, LobbyPlayer Player, DateTime LastSeenUtc, long Access);

    /// <summary>A single waiting roster. Active-match players are not retained here.</summary>
    private sealed class Lobby(Guid serverId, int maxPlayers)
    {
        private readonly object _gate = new();
        private readonly List<PlayerState> _players = new();

        public Guid ServerId => serverId;

        public LobbyPlayer? AddPlayer(string connectionId, long steamId, string username)
        {
            lock (_gate)
            {
                if (_players.Count >= maxPlayers)
                    return null;
                var player = new PlayerState(connectionId, steamId, username, null, false, _players.Count == 0);
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
                if (removed.IsHost && _players.Count > 0)
                    _players[0] = _players[0] with { IsHost = true };
                return removed.ToPlayer();
            }
        }

        public IReadOnlyList<(string ConnectionId, LobbyPlayer Player)> RemovePlayers(ISet<long> steamIds)
        {
            lock (_gate)
            {
                var removed = _players.Where(player => steamIds.Contains(player.SteamId)).ToArray();
                _players.RemoveAll(player => steamIds.Contains(player.SteamId));
                if (_players.Count > 0 && !_players.Any(player => player.IsHost))
                    _players[0] = _players[0] with { IsHost = true };
                return removed.Select(player => (player.ConnectionId, player.ToPlayer())).ToArray();
            }
        }

        public bool IsEmpty
        {
            get { lock (_gate) return _players.Count == 0; }
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
                return new LobbySnapshot(serverId, _players.Select(m => m.ToPlayer()).ToList());
        }

        public (LobbyPlayer? Player, LobbySnapshot Snapshot) SelectCharacter(string connectionId, string character)
        {
            lock (_gate)
            {
                var idx = _players.FindIndex(m => m.ConnectionId == connectionId);
                if (idx < 0)
                    return (null, Snapshot());
                _players[idx] = _players[idx] with { Character = character, LockedIn = true };
                return (_players[idx].ToPlayer(), Snapshot());
            }
        }

        public bool IsAllLockedIn(out string? error)
        {
            lock (_gate)
            {
                if (_players.Count < LobbyLimits.MinPlayers)
                {
                    error = $"At least {LobbyLimits.MinPlayers} players are required to start.";
                    return false;
                }
                if (_players.Any(player => !player.LockedIn))
                {
                    error = "All players must lock in a character before starting.";
                    return false;
                }
                error = null;
                return true;
            }
        }

        private sealed record PlayerState(
            string ConnectionId,
            long SteamId,
            string Username,
            string? Character,
            bool LockedIn,
            bool IsHost)
        {
            public LobbyPlayer ToPlayer() => new(SteamId, Username, Character, LockedIn, IsHost);
        }
    }
}
