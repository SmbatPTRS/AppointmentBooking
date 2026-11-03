using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduleConcurrencyTests(PostgresFixture fixture)
{
    private static long nextTelegramUserId = 3000000000;

    [Fact]
    public async Task TransactionCommitPersistsChanges()
    {
        await using var db = fixture.CreateContext();
        var user = new User(
            Interlocked.Increment(ref nextTelegramUserId),
            "Committed user",
            null,
            DateTime.UtcNow);

        await using (var transaction = await db.BeginTransactionAsync(CancellationToken.None))
        {
            db.Users.Add(user);
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await using var verification = fixture.CreateContext();
        Assert.NotNull(await verification.Users.SingleOrDefaultAsync(item => item.Id == user.Id));
    }

    [Fact]
    public async Task DisposingAnUncommittedTransactionRollsBackChanges()
    {
        var userId = Guid.NewGuid();
        await using (var db = fixture.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync(CancellationToken.None);
            db.Database.ExecuteSqlInterpolated(
                $"INSERT INTO users (id, telegram_user_id, display_name, created_at_utc) VALUES ({userId}, {Interlocked.Increment(ref nextTelegramUserId)}, {"Rolled back"}, {DateTime.UtcNow})");
            await transaction.DisposeAsync();
        }

        await using var verification = fixture.CreateContext();
        Assert.Null(await verification.Users.SingleOrDefaultAsync(item => item.Id == userId));
    }

    [Fact]
    public async Task AContextCannotBeginTwoTransactions()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.BeginTransactionAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.BeginTransactionAsync(CancellationToken.None));
    }
}
