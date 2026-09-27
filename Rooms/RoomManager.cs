using MasterServer.Chat;
using MasterServer.Lobbies;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;

namespace MasterServer.Rooms;

/// <summary>Public, in-memory Rooms; membership is keyed by verified Steam identity.</summary>
public sealed class RoomManager(TimeProvider clock, IConfiguration configuration)
{
    private const int MaxRooms = 5;
    private const int MaxMembers = 4;
    private static readonly TimeSpan DisconnectedMemberGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EmptyRoomLifetime = TimeSpan.FromSeconds(60);
    private static readonly IReadOnlyList<string> NoConnections = Array.Empty<string>();
    private readonly HashSet<string> _characters = LoadAdmission(configuration, "Room:AdmittedCharacters");
    private readonly HashSet<string> _arenas = LoadAdmission(configuration, "Room:AdmittedArenas");
    public string? CatalogHash => configuration["Room:CatalogHash"];
    // Serializes match launch and terminal snapshots so stale updates cannot overtake a rematch.
    internal SemaphoreSlim MatchLifecycleGate { get; } = new(1, 1);

    private static HashSet<string> LoadAdmission(IConfiguration configuration, string key)
    {
        var values = configuration.GetSection(key).Get<string[]>();
        if (values is not { Length: > 0 } || values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"{key} must pin at least one admitted content ID.");
        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Room> _rooms = new();
    private readonly Dictionary<long, Room> _roomBySteamId = new();
    private readonly Dictionary<string, long> _steamIdByConnection = new();
    private readonly Dictionary<long, HashSet<string>> _connectionsBySteamId = new();

    /// <summary>Registers a connection, reattaching within grace or expiring stale membership atomically.</summary>
    public RoomConnectionResult Connect(long steamId, string connectionId)
    {
        lock (_gate)
        {
            if (_steamIdByConnection.TryGetValue(connectionId, out var currentSteamId))
            {
                if (currentSteamId != steamId)
                    throw new InvalidOperationException("Connection identity changed.");
                return new RoomConnectionResult(GetMyRoomLocked(steamId), null);
            }

            var now = clock.GetTimestamp();
            RoomMutationResult? expiredChange = null;
            if (_roomBySteamId.TryGetValue(steamId, out var currentRoom))
            {
                var member = FindMember(currentRoom, steamId);
                if (currentRoom.Phase is not ("Match Starting" or "In Match") &&
                    member.DisconnectedAtTimestamp is { } disconnectedAt &&
                    clock.GetElapsedTime(disconnectedAt, now) >= DisconnectedMemberGrace)
                    expiredChange = RemoveMembersLocked(currentRoom, [member], now);
            }

            AddConnectionLocked(steamId, connectionId);
            if (expiredChange is not null)
            {
                var connections = expiredChange.ConnectionIds.Append(connectionId).Distinct(StringComparer.Ordinal).ToArray();
                return new RoomConnectionResult(null, expiredChange with { ConnectionIds = connections });
            }

            if (!_roomBySteamId.TryGetValue(steamId, out var room))
                return new RoomConnectionResult(null, null);

            FindMember(room, steamId).DisconnectedAtTimestamp = null;
            var changed = ElectLeaderLocked(room);
            var snapshot = ToSnapshot(room);
            var change = changed
                ? new RoomMutationResult(room.Id, snapshot, null, true, false, NoConnections)
                : null;
            return new RoomConnectionResult(snapshot, change);
        }
    }

    /// <summary>Unregisters a connection, retaining its Room slot for the reconnect grace period.</summary>
    public RoomMutationResult? Disconnect(string connectionId)
    {
        lock (_gate)
        {
            if (!_steamIdByConnection.Remove(connectionId, out var steamId) ||
                !_connectionsBySteamId.TryGetValue(steamId, out var connections))
                return null;

            connections.Remove(connectionId);
            if (connections.Count == 0)
                _connectionsBySteamId.Remove(steamId);

            if (!_roomBySteamId.TryGetValue(steamId, out var room))
                return null;

            var member = FindMember(room, steamId);
            if (_connectionsBySteamId.ContainsKey(steamId))
                return null;

            member.DisconnectedAtTimestamp = clock.GetTimestamp();
            return null;
        }
    }

    public RoomSummary[] GetRooms()
    {
        lock (_gate)
            return _rooms.Values.Select(ToSummary).ToArray();
    }

    public RoomSnapshot? GetMyRoom(long steamId)
    {
        lock (_gate)
            return GetMyRoomLocked(steamId);
    }

    /// <summary>Returns a connection's Room only while its identity is actively attached to it.</summary>
    public Guid? GetRoomId(string connectionId)
    {
        lock (_gate)
        {
            if (!_steamIdByConnection.TryGetValue(connectionId, out var steamId) ||
                !_connectionsBySteamId.TryGetValue(steamId, out var connections) ||
                !connections.Contains(connectionId) ||
                !_roomBySteamId.TryGetValue(steamId, out var room))
                return null;

            return room.Id;
        }
    }

    public RoomMutationResult? RenameMember(long steamId, string name)
    {
        lock (_gate)
        {
            if (!_roomBySteamId.TryGetValue(steamId, out var room))
                return null;

            var member = FindMember(room, steamId);
            if (member.Name == name)
                return null;

            member.Name = name;
            return new RoomMutationResult(room.Id, ToSnapshot(room), null, true, false, NoConnections);
        }
    }

    public RoomMutationResult CreateRoom(long steamId, string name, string playerName)
    {
        var validName = ValidateName(name);
        lock (_gate)
        {
            if (_roomBySteamId.ContainsKey(steamId))
                return Reject("already_in_room: Leave your current Room before creating another.");
            if (_rooms.Count >= MaxRooms)
                return Reject("room_limit: All five Room slots are occupied, including recently " +
                    "emptied Rooms. Join one or try again later.");

            var room = new Room(Guid.NewGuid(), validName);
            room.Members.Add(new Member(steamId, playerName));
            room.LeaderSteamId = steamId;
            _rooms.Add(room.Id, room);
            _roomBySteamId.Add(steamId, room);
            return Changed(room, steamId);
        }
    }

    public RoomMutationResult JoinRoom(Guid roomId, long steamId, string name)
    {
        lock (_gate)
        {
            var now = clock.GetTimestamp();
            if (_roomBySteamId.TryGetValue(steamId, out var currentRoom))
            {
                if (currentRoom.Id == roomId)
                    return new RoomMutationResult(roomId, ToSnapshot(currentRoom), null, false, false,
                        GetConnectionsLocked(steamId));
                return Reject("already_in_room: Leave your current Room before joining another.");
            }

            if (!_rooms.TryGetValue(roomId, out var room))
                return Reject("room_not_found: That Room no longer exists. Refresh the list and choose another.");
            if (room.EmptySinceTimestamp is { } emptySince &&
                clock.GetElapsedTime(emptySince, now) >= EmptyRoomLifetime)
                return Reject("room_not_found: That Room no longer exists. Refresh the list and choose another.");
            if (room.Phase != "Lobby")
                return Reject("room_selecting: This Room is preparing a Match. Join after it returns to Lobby.");
            if (room.Members.Count >= MaxMembers)
                return Reject("room_full: That Room has four members. Choose another Room.");

            room.EmptySinceTimestamp = null;
            room.Members.Add(new Member(steamId, name));
            _roomBySteamId.Add(steamId, room);
            ElectLeaderLocked(room);
            return Changed(room, steamId);
        }
    }
    public RoomMutationResult StartCharacterSelect(long steamId)
    {
        lock (_gate)
        {
            var (room, error) = PrepareLocked(steamId, "Lobby", leaderOnly: true);
            if (error is not null) return error;
            if (room!.Members.Count < 2)
                return Reject("room_not_ready: At least two members must join before Character Select.");
            room.Phase = "Character Select";
            return Changed(room, steamId);
        }
    }

    public RoomMutationResult SelectCharacter(long steamId, string character)
    {
        lock (_gate)
        {
            var (room, error) = PrepareLocked(steamId, "Character Select", leaderOnly: false);
            if (error is not null) return error;
            if (character is null || !_characters.Contains(character))
                return Reject("character_not_admitted: Choose an admitted Character.");
            var member = FindMember(room!, steamId);
            member.CharacterSelection = character;
            member.LockedIn = true;
            return Changed(room!, steamId);
        }
    }

    public RoomMutationResult StartStageSelect(long steamId)
    {
        lock (_gate)
        {
            var (room, error) = PrepareLocked(steamId, "Character Select", leaderOnly: true);
            if (error is not null) return error;
            if (room!.Members.Count < 2 || room.Members.Any(member => !member.LockedIn))
                return Reject("room_not_ready: Two to four members must lock in admitted Characters first.");
            room.Phase = "Stage Select";
            return Changed(room, steamId);
        }
    }

    public RoomMutationResult ChooseArena(long steamId, string arena)
    {
        lock (_gate)
        {
            var (room, error) = PrepareLocked(steamId, "Stage Select", leaderOnly: true);
            if (error is not null) return error;
            if (arena is null || !_arenas.Contains(arena))
                return Reject("arena_not_admitted: Choose an admitted Arena.");
            if (room!.Members.Count < 2 || room.Members.Any(member => !member.LockedIn))
                return Reject("room_not_ready: Two to four members must remain locked in.");
            room.ArenaName = arena;
            return Changed(room, steamId);
        }
    }

    /// <summary>Freeze the current Room roster before host selection or network I/O.</summary>
    public RoomLaunchPreparation? BeginMatch(long steamId, out string? error)
    {
        lock (_gate)
        {
            var (room, rejected) = PrepareLocked(steamId, "Stage Select", leaderOnly: true);
            if (rejected is not null)
            {
                error = rejected.Error;
                return null;
            }
            if (room!.Members.Count is < 2 or > 4 ||
                room.Members.Any(member => !member.LockedIn || member.CharacterSelection is null) ||
                room.ArenaName is null)
            {
                error = "room_not_ready: Two to four members must lock in Characters and choose an Arena.";
                return null;
            }
            var matchId = Guid.NewGuid();
            room.ActiveMatchId = matchId;
            room.Phase = "Match Starting";
            error = null;
            var players = room.Members.Select((member, index) =>
                new LobbyPlayer(member.SteamId, member.Name, member.CharacterSelection,
                    true, member.SteamId == room.LeaderSteamId, index + 1)).ToArray();
            return new RoomLaunchPreparation(room.Id, matchId, room.ArenaName, players);
        }
    }

    public RoomSnapshot? CompleteLaunch(Guid roomId, Guid matchId)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room) ||
                room.Phase != "Match Starting" || room.ActiveMatchId != matchId)
                return null;
            room.Phase = "In Match";
            return ToSnapshot(room);
        }
    }

    public RoomSnapshot? FailLaunch(Guid roomId, Guid matchId)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room) ||
                room.Phase != "Match Starting" || room.ActiveMatchId != matchId)
                return null;
            room.ActiveMatchId = null;
            room.Phase = "Stage Select";
            return ToSnapshot(room);
        }
    }

    /// <summary>Returns the matching Room to Lobby once; late terminals cannot reset a rematch.</summary>
    public RoomSnapshot? CompleteMatch(Guid roomId, Guid matchId)
    {
        lock (_gate)
        {
            if (matchId == Guid.Empty || !_rooms.TryGetValue(roomId, out var room) ||
                room.ActiveMatchId != matchId || room.Phase is not ("Match Starting" or "In Match"))
                return null;

            ResetPreparationLocked(room);
            room.EmptySinceTimestamp = room.Members.Count == 0 ? clock.GetTimestamp() : null;
            return ToSnapshot(room);
        }
    }

    private (Room? Room, RoomMutationResult? Error) PrepareLocked(long steamId, string phase, bool leaderOnly)
    {
        if (!_roomBySteamId.TryGetValue(steamId, out var room))
            return (null, Reject("not_in_room: Join a Room first."));
        if (leaderOnly && room.LeaderSteamId != steamId)
            return (null, Reject("not_leader: Only the Room leader can advance preparation."));
        if (room.Phase != phase)
            return (null, Reject($"invalid_phase: Room must be in {phase} first."));
        return (room, null);
    }

    public RoomMutationResult LeaveRoom(long steamId)
    {
        lock (_gate)
        {
            if (!_roomBySteamId.TryGetValue(steamId, out var room))
                return Reject("not_in_room: You are not currently in a Room.");
            if (room.Phase == "Match Starting")
                return Reject("room_match_starting: Wait for Match launch before leaving.");
            _roomBySteamId.Remove(steamId);

            var index = room.Members.FindIndex(member => member.SteamId == steamId);
            if (index < 0)
                throw new InvalidOperationException("Room membership index is inconsistent.");
            var connections = GetConnectionsLocked(steamId);
            room.Members.RemoveAt(index);
            if (room.Members.Count == 0)
            {
                room.LeaderSteamId = 0;
                room.EmptySinceTimestamp = room.Phase == "In Match" ? null : clock.GetTimestamp();
            }
            else
            {
                ElectLeaderLocked(room);
            }
            if (room.Phase != "In Match")
            {
                if (room.Members.Count < 2)
                    ResetPreparationLocked(room);
                else
                    room.ArenaName = null;
            }

            return new RoomMutationResult(room.Id, ToSnapshot(room), null, true, false, connections);
        }
    }

    /// <summary>Expires disconnected members and empty Rooms using the injected clock.</summary>
    public RoomMutationResult[] SweepExpired()
    {
        lock (_gate)
        {
            var now = clock.GetTimestamp();
            var changes = new List<RoomMutationResult>();
            foreach (var room in _rooms.Values.ToArray())
            {
                var expiredMembers = room.Phase is "Match Starting" or "In Match"
                    ? Array.Empty<Member>()
                    : room.Members.Where(member => member.DisconnectedAtTimestamp is { } disconnectedAt &&
                        clock.GetElapsedTime(disconnectedAt, now) >= DisconnectedMemberGrace).ToArray();
                if (expiredMembers.Length > 0)
                    changes.Add(RemoveMembersLocked(room, expiredMembers, now));

                if (room.Phase is not ("Match Starting" or "In Match") &&
                    room.Members.Count == 0 && room.EmptySinceTimestamp is { } emptySince &&
                    clock.GetElapsedTime(emptySince, now) >= EmptyRoomLifetime)
                {
                    _rooms.Remove(room.Id);
                    changes.Add(new RoomMutationResult(room.Id, null, null, true, true, NoConnections));
                }
            }
            return changes.ToArray();
        }
    }

    private void AddConnectionLocked(long steamId, string connectionId)
    {
        _steamIdByConnection.Add(connectionId, steamId);
        if (!_connectionsBySteamId.TryGetValue(steamId, out var connections))
        {
            connections = new HashSet<string>(StringComparer.Ordinal);
            _connectionsBySteamId.Add(steamId, connections);
        }
        connections.Add(connectionId);
    }

    private RoomMutationResult RemoveMembersLocked(Room room, IReadOnlyList<Member> members, long now)
    {
        var revokedConnections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            _roomBySteamId.Remove(member.SteamId);
            revokedConnections.UnionWith(GetConnectionsLocked(member.SteamId));
            room.Members.Remove(member);
        }

        if (room.Members.Count == 0)
        {
            room.LeaderSteamId = 0;
            room.EmptySinceTimestamp ??= now;
        }
        else
        {
            room.EmptySinceTimestamp = null;
            ElectLeaderLocked(room);
        }
        if (room.Members.Count < 2)
            ResetPreparationLocked(room);
        else
            room.ArenaName = null;

        return new RoomMutationResult(room.Id, ToSnapshot(room), null, true, false,
            revokedConnections.Count == 0 ? NoConnections : revokedConnections.ToArray());
    }

    private static void ResetPreparationLocked(Room room)
    {
        room.Phase = "Lobby";
        room.ActiveMatchId = null;
        room.ArenaName = null;
        foreach (var member in room.Members)
        {
            member.CharacterSelection = null;
            member.LockedIn = false;
        }
    }

    private RoomSnapshot? GetMyRoomLocked(long steamId)
        => _roomBySteamId.TryGetValue(steamId, out var room) ? ToSnapshot(room) : null;

    private RoomMutationResult Changed(Room room, long steamId)
        => new(room.Id, ToSnapshot(room), null, true, false, GetConnectionsLocked(steamId));

    private IReadOnlyList<string> GetConnectionsLocked(long steamId)
        => _connectionsBySteamId.TryGetValue(steamId, out var connections)
            ? connections.ToArray()
            : NoConnections;

    private bool IsConnectedLocked(long steamId)
        => _connectionsBySteamId.TryGetValue(steamId, out var connections) && connections.Count > 0;

    private bool ElectLeaderLocked(Room room)
    {
        var previousLeader = room.LeaderSteamId;
        if (room.Members.Any(member => member.SteamId == previousLeader))
            return false;

        var leader = room.Members.FirstOrDefault(member => IsConnectedLocked(member.SteamId))
            ?? room.Members.FirstOrDefault(member => member.SteamId == previousLeader)
            ?? room.Members.FirstOrDefault();
        room.LeaderSteamId = leader?.SteamId ?? 0;
        return room.LeaderSteamId != previousLeader;
    }

    private static Member FindMember(Room room, long steamId)
        => room.Members.First(member => member.SteamId == steamId);

    private static RoomMutationResult Reject(string error)
        => new(Guid.Empty, null, error, false, false, NoConnections);

    private static string ValidateName(string name)
    {
        try
        {
            return ChatText.DisplayName(name);
        }
        catch (HubException)
        {
            throw new HubException("invalid_room_name: Enter a name with 1–24 valid characters.");
        }
    }

    private RoomSnapshot ToSnapshot(Room room)
    {
        var members = room.Members
            .Select(member => new RoomMember(member.SteamId, member.Name,
                member.SteamId == room.LeaderSteamId, member.CharacterSelection, member.LockedIn))
            .ToArray();
        return new RoomSnapshot(room.Id, room.Name, room.Phase, room.LeaderSteamId, members,
            members.Length, MaxMembers, room.Phase == "Lobby" && members.Length < MaxMembers,
            room.ArenaName, _characters.ToArray(), _arenas.ToArray(), room.ActiveMatchId);
    }

    private static RoomSummary ToSummary(Room room)
        => new(room.Id, room.Name, room.Phase, room.LeaderSteamId, room.Members.Count,
            MaxMembers, room.Phase == "Lobby" && room.Members.Count < MaxMembers);

    private sealed class Room(Guid id, string name)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public List<Member> Members { get; } = new(MaxMembers);
        public long LeaderSteamId { get; set; }
        public long? EmptySinceTimestamp { get; set; }
        public string Phase { get; set; } = "Lobby";
        public string? ArenaName { get; set; }
        public Guid? ActiveMatchId { get; set; }
    }

    private sealed class Member(long steamId, string name)
    {
        public long SteamId { get; } = steamId;
        public string Name { get; set; } = name;
        public long? DisconnectedAtTimestamp { get; set; }
        public string? CharacterSelection { get; set; }
        public bool LockedIn { get; set; }
    }
}
