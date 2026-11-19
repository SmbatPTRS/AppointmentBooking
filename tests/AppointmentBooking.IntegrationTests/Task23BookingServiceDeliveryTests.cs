using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class Task23BookingServiceDeliveryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task FailedConfirmationSendAfterCommitRetriesWithoutCreatingAnotherBooking()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var sender = new FakeTelegramClient();
        var committedIds = new List<Guid>();
        await using var host = seed.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            BookingTestSetup.ConfigureClock(services);
            services.AddSingleton<ITelegramMessageSender>(sender);
            services.AddScoped<ITelegramHandler>(provider => new ConfirmationProbe(
                provider.GetRequiredService<BookingService>(), sender, seed.Request, committedIds));
        }));
        var update = TelegramFoundationTests.Message("/delivery.confirm", seed.Client.TelegramUserId);
        update.Id = 23_001;

        async Task DispatchInFreshScope()
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>().DispatchAsync(update);
        }

        sender.FailNextSend(new HttpRequestException("Simulated confirmation delivery failure"));
        await Assert.ThrowsAsync<HttpRequestException>(DispatchInFreshScope);

        // The reply failed, but the actual application service already committed to PostgreSQL.
        var firstId = Assert.Single(committedIds);
        Assert.Empty(sender.SentMessages);
        await using (var db = fixture.CreateContext())
        {
            var saved = await db.Bookings.SingleAsync(booking => booking.ClientUserId == seed.Client.Id);
            Assert.Equal(firstId, saved.Id);
            Assert.Equal(seed.Request.RequestId, saved.RequestId);
        }

        // Redeliver the exact update using a new scoped service/context, as polling does.
        await DispatchInFreshScope();

        Assert.Equal(new[] { firstId, firstId }, committedIds);
        Assert.Equal($"Booking confirmed: {firstId}", Assert.Single(sender.SentMessages).Text);
        await using (var db = fixture.CreateContext())
        {
            var saved = await db.Bookings.SingleAsync(booking => booking.ClientUserId == seed.Client.Id);
            Assert.Equal(firstId, saved.Id);
            Assert.Equal(seed.Request.RequestId, saved.RequestId);
        }
    }

    // Test-only adapter isolating the service/dispatcher contract. It receives the trusted
    // actor from the shared dispatcher and sends a reply only after CreateAsync completes.
    private sealed class ConfirmationProbe(BookingService bookings, ITelegramMessageSender sender,
        CreateBookingRequest originalIntent, List<Guid> committedIds) : ITelegramHandler
    {
        public string Route => "delivery.confirm";

        public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
        {
            context.State.ConfirmationRequestId ??= originalIntent.RequestId;
            var request = originalIntent with { RequestId = context.State.ConfirmationRequestId.Value };
            var saved = await bookings.CreateAsync(context.Caller.Actor, request, ct);
            committedIds.Add(saved.Id);
            await sender.SendTextAsync(context.ChatId, $"Booking confirmed: {saved.Id}", ct);
        }
    }
}
