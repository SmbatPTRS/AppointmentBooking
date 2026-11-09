using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.UnitTests;

public sealed class BookingServiceTests
{
    [Theory]
    [InlineData("request ID")]
    [InlineData("UTC start")]
    [InlineData("price")]
    [InlineData("duration")]
    public async Task InvalidCreationInputIsRejectedBeforeReadingStorage(string invalidField)
    {
        // Invalid requests must exit before touching any dependency.
        var service = new BookingService(null!, null!, null!, null!, null!, null!, null!, null!);
        var actor = new Actor(Guid.NewGuid());
        var request = new CreateBookingRequest(Guid.NewGuid(), actor.UserId, Guid.NewGuid(),
            DateTime.UnixEpoch.AddHours(1), 25m, 30);
        request = invalidField switch
        {
            "request ID" => request with { RequestId = Guid.Empty },
            "UTC start" => request with { StartAtUtc = DateTime.SpecifyKind(request.StartAtUtc, DateTimeKind.Unspecified) },
            "price" => request with { ExpectedPrice = -1 },
            "duration" => request with { ExpectedDurationMinutes = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField))
        };

        var error = await Assert.ThrowsAsync<AppException>(() => service.CreateAsync(actor, request, default));
        Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
        Assert.Equal(AppErrorKind.Validation, error.Kind);
    }
}
