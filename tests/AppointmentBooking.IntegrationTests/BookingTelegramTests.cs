using AppointmentBooking.Api.Telegram.State;

namespace AppointmentBooking.IntegrationTests;

/// <summary>Acceptance coverage for booking values retained by the Telegram conversation.</summary>
public sealed class BookingTelegramTests
{
    [Fact]
    public void ConfirmationRestoresDisplayedSelectionAndRequestIdWhenGoingBack()
    {
        var now = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
        var state = new ConversationState(now);
        var offeringId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 11, 12, 14, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(30);

        state.EnterFlow("book.service", now);
        state.SetBooking(new BookingSelection(
            offeringId, new DateOnly(2026, 11, 12), start, end,
            "Consultation", 25m, 30));
        state.ConfirmationRequestId = requestId;
        state.NavigateTo("book.confirm", now);

        state.SetBooking(new BookingSelection(
            offeringId, new DateOnly(2026, 11, 13), start.AddDays(1), end.AddDays(1),
            "Changed consultation", 30m, 45));
        state.ConfirmationRequestId = Guid.NewGuid();

        Assert.True(state.GoBack(now));
        Assert.Equal("book.service", state.Step);
        Assert.Equal(new BookingSelection(
            offeringId, new DateOnly(2026, 11, 12), start, end,
            "Consultation", 25m, 30), state.Booking);
        Assert.Equal(requestId, state.ConfirmationRequestId);
    }

    [Fact]
    public void NewBookingSelectionGetsNewRequestIdentityAndCancelClearsAllValues()
    {
        var now = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
        var firstStart = new DateTimeOffset(2026, 11, 12, 14, 0, 0, TimeSpan.Zero);
        var secondStart = firstStart.AddDays(1);
        var state = new ConversationState(now);
        state.EnterFlow("book.confirm", now);
        state.SetBooking(new BookingSelection(
            Guid.NewGuid(), new DateOnly(2026, 11, 12), firstStart,
            firstStart.AddMinutes(30), "Consultation", 25m, 30));
        state.ConfirmationRequestId = Guid.NewGuid();
        var firstRequest = state.ConfirmationRequestId;

        state.SetBooking(new BookingSelection(
            Guid.NewGuid(), new DateOnly(2026, 11, 13), secondStart,
            secondStart.AddMinutes(30), "Follow-up", 30m, 30));
        state.ConfirmationRequestId = Guid.NewGuid();

        Assert.NotEqual(firstRequest, state.ConfirmationRequestId);
        state.Reset(now);
        Assert.Null(state.Booking);
        Assert.Null(state.ConfirmationRequestId);
        Assert.Equal("start", state.Step);
    }
}
