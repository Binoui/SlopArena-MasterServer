using MasterServer.Data;
using MasterServer.Steam;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MasterServer.Tests;

public sealed class SteamTicketReplayStoreTests
{
    [Fact]
    public async Task TryConsumeAsync_PersistsOnlyDigestAndRejectsReuseAcrossContexts()
    {
        const string ticket = "AABBCCDDEEFF00112233445566778899";
        var database = $"steam-ticket-replay-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options;

        await using (var db = new AppDbContext(options))
        {
            var store = new SteamTicketReplayStore(db);
            Assert.True(await store.TryConsumeAsync(ticket, CancellationToken.None));
            Assert.False(await store.TryConsumeAsync(ticket.ToLowerInvariant(), CancellationToken.None));

            var saved = await db.UsedSteamAuthTickets.SingleAsync();
            Assert.Equal(64, saved.Hash.Length);
            Assert.NotEqual(ticket, saved.Hash);
        }

        await using var nextContext = new AppDbContext(options);
        Assert.False(await new SteamTicketReplayStore(nextContext)
            .TryConsumeAsync(ticket, CancellationToken.None));
    }
}
