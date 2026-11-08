using AppointmentBooking.Application.Identity;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.Infrastructure.Time;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class UserIdentityTests(PostgresFixture fixture)
{
    [Fact]
    public async Task FirstVisitsAndCompetingInsertReturnTheSameUser()
    {
        const long telegramUserId = 8_000_000_001;
        await using var firstDb = fixture.CreateContext();
        await using var secondDb = fixture.CreateContext();
        var clock = new SystemClock();
        var first = new UserService(new UserRepository(firstDb), clock);
        var secondRepository = new UserRepository(secondDb);
        var second = new UserService(secondRepository, clock);

        var users = await Task.WhenAll(
            first.GetOrCreateAsync(telegramUserId, "First visit", null),
            second.GetOrCreateAsync(telegramUserId, "Second visit", "second"));

        Assert.Equal(users[0].Id, users[1].Id);

        // Exercise the conflict path even if one visit finishes before the other's lookup.
        var candidate = new User(telegramUserId, "Losing insert", null, clock.UtcNow);
        var stored = await secondRepository.InsertOrGetAsync(candidate);

        Assert.Equal(users[0].Id, stored.Id);
        Assert.Equal(users[0].DisplayName, stored.DisplayName);
        Assert.Equal(1, await firstDb.Users.CountAsync(user => user.TelegramUserId == telegramUserId));
    }
}
