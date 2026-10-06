using System.Globalization;
using MasterServer.Chat;
using MasterServer.Data;
using MasterServer.Lobbies;
using MasterServer.Rooms;
using Microsoft.EntityFrameworkCore;

namespace MasterServer.DTOs;

internal sealed record OwnerObservationSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ObservedPlayer> Players,
    IReadOnlyList<ObservedRoom> Rooms,
    IReadOnlyList<ObservedLobby> Lobbies,
    IReadOnlyList<ObservedMatch> Matches,
    IReadOnlyList<ObservedGlobalMessage> GlobalChat,
    IReadOnlyList<ObservedGameServer> Servers);

internal sealed record ObservedPlayer(
    string SteamId,
    string Name,
    bool MasterConnected,
    string? RoomId,
    string? MatchId,
    string? ServerId,
    string GameServerConnectivity);

internal sealed record ObservedRoom(
    string Id,
    string Name,
    string Phase,
    string LeaderSteamId,
    IReadOnlyList<ObservedRoomMember> Members,
    int MemberCount,
    int Capacity,
    bool Joinable,
    string? Arena,
    string? ActiveMatchId);

internal sealed record ObservedRoomMember(
    string SteamId,
    string Name,
    bool IsLeader,
    string? CharacterSelection,
    bool LockedIn);

internal sealed record ObservedLobby(string ServerId, IReadOnlyList<ObservedRoomMember> Members);

internal sealed record ObservedMatchRosterPlayer(string SteamId, string? Name);

internal sealed record ObservedMatch(
    string Id,
    string? RoomId,
    string? ServerId,
    IReadOnlyList<ObservedMatchRosterPlayer> Roster,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    DateTimeOffset? CanceledAtUtc,
    string? CancelReason,
    string Status,
    double ElapsedSeconds);

internal sealed record ObservedGlobalMessage(
    string Id,
    long Sequence,
    string Channel,
    string SenderId,
    string SenderName,
    string Text,
    DateTimeOffset SentAtUtc);

internal sealed record ObservedGameServer(
    string Id,
    string Name,
    string Region,
    DateTimeOffset LastHeartbeatUtc,
    double HeartbeatAgeSeconds,
    string Status,
    int CurrentMatches,
    int MaxConcurrentMatches,
    int AvailableMatchSlots);

internal sealed record MatchRow(
    Guid Id,
    Guid? RoomId,
    Guid? ServerId,
    long Player1SteamId,
    long Player2SteamId,
    long? Player3SteamId,
    long? Player4SteamId,
    DateTime StartedAt,
    DateTime? EndedAt,
    DateTime? CanceledAt,
    string? CancelReason);


internal static class OwnerObservationSnapshotBuilder
{
    private const int MaxTerminalMatches = 50;
    private static readonly TimeSpan FreshHeartbeatWindow = TimeSpan.FromSeconds(15);

    internal static async Task<OwnerObservationSnapshot> CaptureAsync(
        AppDbContext db,
        ChatService chat,
        RoomManager rooms,
        LobbyManager lobbies,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var chatSnapshot = chat.GetOwnerObservationSnapshot();
        var roomSnapshots = rooms.GetOwnerObservationSnapshots();
        var lobbySnapshots = lobbies.GetOwnerObservationSnapshots();

        var recentTerminalIds = db.Matches
            .Where(match => match.EndedAt != null || match.CanceledAt != null)
            .OrderByDescending(match => match.CanceledAt ?? match.EndedAt)
            .ThenByDescending(match => match.StartedAt)
            .Take(MaxTerminalMatches)
            .Select(match => match.Id);
        var matchRows = await db.Matches.AsNoTracking()
            .Where(match => match.EndedAt == null && match.CanceledAt == null ||
                recentTerminalIds.Contains(match.Id))
            .OrderByDescending(match => match.EndedAt == null && match.CanceledAt == null)
            .ThenByDescending(match => match.CanceledAt ?? match.EndedAt ?? match.StartedAt)
            .Select(match => new MatchRow(
                match.Id, match.RoomId, match.ServerId,
                match.Player1SteamId, match.Player2SteamId, match.Player3SteamId, match.Player4SteamId,
                match.StartedAt, match.EndedAt, match.CanceledAt, match.CancelReason))
            .ToListAsync(cancellationToken);
        var rosterIdSet = new HashSet<long>();
        foreach (var match in matchRows)
            AddRosterSteamIds(rosterIdSet, match);
        var rosterSteamIds = rosterIdSet.ToArray();
        var rosterNames = rosterSteamIds.Length == 0
            ? new Dictionary<long, string>()
            : await db.Users.AsNoTracking()
                .Where(user => rosterSteamIds.Contains(user.SteamId))
                .Select(user => new { user.SteamId, user.Username })
                .ToDictionaryAsync(user => user.SteamId, user => user.Username, cancellationToken);

        var servers = await db.GameServers.AsNoTracking()
            .OrderBy(server => server.Name)
            .Select(server => new
            {
                server.Id,
                server.Name,
                server.Region,
                server.LastHeartbeat,
                server.CurrentMatches,
                server.MaxConcurrentMatches
            })
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var observedMatches = matchRows.Count == 0
            ? Array.Empty<ObservedMatch>()
            : new ObservedMatch[matchRows.Count];
        for (var index = 0; index < matchRows.Count; index++)
            observedMatches[index] = ToObservedMatch(matchRows[index], rosterNames, now);

        var observedServers = servers.Select(server =>
        {
            var lastHeartbeat = AsUtc(server.LastHeartbeat);
            var ageSeconds = Math.Max(0, (now - lastHeartbeat).TotalSeconds);
            return new ObservedGameServer(
                server.Id.ToString(), server.Name, server.Region, lastHeartbeat, ageSeconds,
                now - lastHeartbeat < FreshHeartbeatWindow ? "fresh" : "stale",
                server.CurrentMatches, server.MaxConcurrentMatches,
                Math.Max(0, server.MaxConcurrentMatches - server.CurrentMatches));
        }).ToArray();

        var roomByPlayer = new Dictionary<long, RoomObservationSnapshot>();
        foreach (var room in roomSnapshots)
            foreach (var member in room.Members)
                roomByPlayer.TryAdd(member.SteamId, room);
        var runningMatchByPlayer = new Dictionary<long, ObservedMatch>();
        for (var index = 0; index < matchRows.Count; index++)
        {
            var row = matchRows[index];
            var match = observedMatches[index];
            if (match.Status != "running")
                continue;
            runningMatchByPlayer.TryAdd(row.Player1SteamId, match);
            runningMatchByPlayer.TryAdd(row.Player2SteamId, match);
            if (row.Player3SteamId is { } player3SteamId)
                runningMatchByPlayer.TryAdd(player3SteamId, match);
            if (row.Player4SteamId is { } player4SteamId)
                runningMatchByPlayer.TryAdd(player4SteamId, match);
        }
        var lobbyByPlayer = new Dictionary<long, Guid>();
        foreach (var lobby in lobbySnapshots)
            foreach (var member in lobby.Players)
                lobbyByPlayer.TryAdd(member.SteamId, lobby.ServerId);

        var observedPlayers = chatSnapshot.Players.Select(player =>
        {
            var steamId = long.Parse(player.PlayerId, NumberStyles.None, CultureInfo.InvariantCulture);
            roomByPlayer.TryGetValue(steamId, out var room);
            runningMatchByPlayer.TryGetValue(steamId, out var match);
            var hasLobby = lobbyByPlayer.TryGetValue(steamId, out var lobbyServerId);
            var matchId = match?.Id ?? room?.ActiveMatchId?.ToString();
            var roomId = room?.Id.ToString();
            var serverId = match?.ServerId;
            if (match is null && room?.ActiveMatchId is null && hasLobby)
                serverId = lobbyServerId.ToString();
            return new ObservedPlayer(
                steamId.ToString(CultureInfo.InvariantCulture), player.DisplayName, true,
                roomId, matchId, serverId, "unknown");
        }).ToArray();

        return new OwnerObservationSnapshot(
            now,
            observedPlayers,
            roomSnapshots.Select(room => new ObservedRoom(
                room.Id.ToString(), room.Name, room.Phase,
                room.LeaderSteamId.ToString(CultureInfo.InvariantCulture),
                room.Members.Select(member => new ObservedRoomMember(
                    member.SteamId.ToString(CultureInfo.InvariantCulture), member.Name,
                    member.IsLeader, member.CharacterSelection, member.LockedIn)).ToArray(),
                room.MemberCount, room.Capacity, room.Joinable, room.ArenaName,
                room.ActiveMatchId?.ToString())).ToArray(),
            lobbySnapshots.Select(lobby => new ObservedLobby(
                lobby.ServerId.ToString(), lobby.Players.Select(member => new ObservedRoomMember(
                    member.SteamId.ToString(CultureInfo.InvariantCulture), member.Username,
                    member.IsHost, member.Character, member.LockedIn)).ToArray())).ToArray(),
            observedMatches,
            chatSnapshot.GlobalMessages.Select(message => new ObservedGlobalMessage(
                message.MessageId.ToString(), message.Sequence, "Global", message.Sender.PlayerId,
                message.Sender.DisplayName, message.Text, message.SentAt)).ToArray(),
            observedServers);
    }

    private static ObservedMatch ToObservedMatch(
        MatchRow match,
        IReadOnlyDictionary<long, string> rosterNames,
        DateTimeOffset now)
    {
        var status = match.CanceledAt is not null ? "canceled"
            : match.EndedAt is not null ? "completed" : "running";
        var startedAt = AsUtc(match.StartedAt);
        DateTimeOffset? endedAt = match.EndedAt is null ? null : AsUtc(match.EndedAt.Value);
        DateTimeOffset? canceledAt = match.CanceledAt is null ? null : AsUtc(match.CanceledAt.Value);
        var terminalAt = canceledAt ?? endedAt ?? now;
        var rosterLength = 2 + (match.Player3SteamId.HasValue ? 1 : 0) +
            (match.Player4SteamId.HasValue ? 1 : 0);
        var roster = new ObservedMatchRosterPlayer[rosterLength];
        var rosterIndex = 0;
        roster[rosterIndex++] = ToRosterPlayer(match.Player1SteamId, rosterNames);
        roster[rosterIndex++] = ToRosterPlayer(match.Player2SteamId, rosterNames);
        if (match.Player3SteamId is { } player3SteamId)
            roster[rosterIndex++] = ToRosterPlayer(player3SteamId, rosterNames);
        if (match.Player4SteamId is { } player4SteamId)
            roster[rosterIndex] = ToRosterPlayer(player4SteamId, rosterNames);
        return new ObservedMatch(
            match.Id.ToString(), match.RoomId?.ToString(), match.ServerId?.ToString(),
            roster, startedAt, endedAt, canceledAt, match.CancelReason, status,
            Math.Max(0, (terminalAt - startedAt).TotalSeconds));
    }

    private static void AddRosterSteamIds(HashSet<long> steamIds, MatchRow match)
    {
        steamIds.Add(match.Player1SteamId);
        steamIds.Add(match.Player2SteamId);
        if (match.Player3SteamId is { } player3SteamId)
            steamIds.Add(player3SteamId);
        if (match.Player4SteamId is { } player4SteamId)
            steamIds.Add(player4SteamId);
    }

    private static ObservedMatchRosterPlayer ToRosterPlayer(
        long steamId,
        IReadOnlyDictionary<long, string> rosterNames)
        => new(steamId.ToString(CultureInfo.InvariantCulture),
            rosterNames.TryGetValue(steamId, out var name) ? name : null);

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

