using AppointmentBooking.Application.Abstractions;
using Microsoft.EntityFrameworkCore.Storage;

namespace AppointmentBooking.Infrastructure.Persistence;

public class TransactionScope(IDbContextTransaction transaction) 
    : ITransactionScope
{
    public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);

    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}