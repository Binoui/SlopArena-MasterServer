namespace MasterServer.Data.Models;

/// <summary>Durably consumed web-ticket digest; never persist the ticket itself.</summary>
public sealed class UsedSteamAuthTicket
{
    public string Hash { get; set; } = string.Empty;
    public DateTime ConsumedAt { get; set; }
}
