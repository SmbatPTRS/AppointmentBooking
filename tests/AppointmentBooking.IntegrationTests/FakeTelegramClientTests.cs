using AppointmentBooking.Api.Telegram;
using AppointmentBooking.IntegrationTests.Fixtures;

namespace AppointmentBooking.IntegrationTests;

// These tests use only the fake sender; no database or real Telegram connection is needed.
public sealed class FakeTelegramClientTests
{
    [Fact]
    public async Task RecordsRepliesWithTheirOriginalDestinationAndText()
    {
        var fake = new FakeTelegramClient();
        ITelegramMessageSender sender = fake;

        // Telegram chat IDs can exceed Int32 and group chat IDs can be negative.
        await sender.SendTextAsync(5_000_000_000, "Welcome! Բարև");
        await sender.SendTextAsync(-1_000_000_000_001, "Choose a service.");

        Assert.Collection(fake.SentMessages,
            message => Assert.Equal(new SentTelegramMessage(5_000_000_000, "Welcome! Բարև"), message),
            message => Assert.Equal(new SentTelegramMessage(-1_000_000_000_001, "Choose a service."), message));
    }

    [Fact]
    public async Task CancelledSendDoesNotRecordAReply()
    {
        var fake = new FakeTelegramClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fake.SendTextAsync(123, "Welcome!", cancellation.Token));

        Assert.Empty(fake.SentMessages);
    }
}
