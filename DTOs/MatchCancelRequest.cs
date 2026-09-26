namespace MasterServer.DTOs;

public sealed record MatchCancelRequest(Guid MatchId, string Reason);
