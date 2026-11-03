namespace AppointmentBooking.Application.Abstractions;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct);
    Task<ITransactionScope> BeginTransactionAsync(CancellationToken ct);
}

public interface ITransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
    // DisposeAsync without Commit = rollback.
}