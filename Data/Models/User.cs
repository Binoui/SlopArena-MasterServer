// MasterServer/Data/Models/User.cs
namespace MasterServer.Data.Models;

/// <summary>
/// A player persisted in the database (glossary: <b>Player</b>). The entity and
/// table keep the historical <c>User</c> name: renaming would churn EF migrations
/// and the auth surface for no behavioral gain — the glossary treats this class
/// as the Player persistence artifact (issue #7).
/// </summary>
public class User
{
    public long SteamId { get; set; } // Primary key
    public string Username { get; set; } = string.Empty;
    public int Mmr { get; set; } = 1000;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLogin { get; set; }
}
