using AppointmentBooking.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IUnitOfWork
{
    // Feature owners add their DbSets and configuration classes here.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    // DbContext already implements SaveChangesAsync.
    // This helper adds the explicit operation boundary, not another change tracker.
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Nested transaction boundaries are not supported.");

        await using var transaction =
            await Database.BeginTransactionAsync(cancellationToken);

        var result = await operation(cancellationToken);
        await SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
