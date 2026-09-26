using System.Data.Common;
using System.Security.Cryptography;
using MasterServer.Data;
using MasterServer.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MasterServer.Steam;

public sealed class SteamTicketReplayStore(AppDbContext db)
{
    public async Task<bool> TryConsumeAsync(string ticket, CancellationToken cancellationToken)
    {
        if (!SteamTicketVerifier.IsValidTicket(ticket))
            return false;

        var hash = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(ticket)));
        try
        {
            if (await db.UsedSteamAuthTickets.AsNoTracking().AnyAsync(entry => entry.Hash == hash, cancellationToken))
                return false;

            var entry = new UsedSteamAuthTicket { Hash = hash, ConsumedAt = DateTime.UtcNow };
            db.UsedSteamAuthTickets.Add(entry);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                db.Entry(entry).State = EntityState.Detached;
                if (await db.UsedSteamAuthTickets.AsNoTracking().AnyAsync(item => item.Hash == hash, cancellationToken))
                    return false;
                throw new SteamReplayStoreUnavailableException();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbException)
        {
            throw new SteamReplayStoreUnavailableException();
        }
    }
}

public sealed class SteamReplayStoreUnavailableException : Exception
{
}
