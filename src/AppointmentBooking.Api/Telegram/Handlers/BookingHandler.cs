using System.Globalization;
using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Api.Telegram.State;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;
using AppointmentBooking.Domain.Enums;

namespace AppointmentBooking.Api.Telegram.Handlers;

/// <summary>Handles customer booking, client bookings, and staff appointment completion.</summary>
public sealed class BookingHandler(
    BookingService bookings,
    CatalogService catalog,
    StaffManagementService staff,
    AvailabilityService availability,
    ITelegramMessageSender sender,
    IClock clock) : ITelegramHandler
{
    private const string DraftKey = "booking";
    private const string AppointmentKey = "appointments";
    public string Route => "book";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        if (context.IsRetryRender)
        {
            await RenderCurrentScreenAsync(context, ct);
            return;
        }
        if (context.Callback?.Route == "bookings" || Command(context.Text) == "bookings" ||
            context.State.Step == "bookings")
        {
            await HandleBookingsAsync(context, ct);
            return;
        }
        if (context.Callback?.Route == "appointments" || Command(context.Text) == "appointments" ||
            context.State.Step.StartsWith("appointments", StringComparison.Ordinal))
        {
            if (context.Callback?.Route == "appointments.staff" && context.ItemId is Guid staffId)
            {
                context.State.SetFeatureData(AppointmentKey, new AppointmentDraft(staffId, null));
                context.State.NavigateTo("appointments.date", clock.UtcNow);
                await RenderAppointmentDateAsync(context, ct);
                return;
            }
            await HandleAppointmentsAsync(context, ct);
            return;
        }
        // The dispatcher routes ordinary text to the current screen; callbacks select
        // only IDs that were bound to this sender and chat when the menu was rendered.
        if (context.Callback?.Route == "book" || Command(context.Text) == "book")
        {
            context.State.EnterFlow("book.staff", clock.UtcNow);
            await RenderStaffAsync(context, ct);
            return;
        }
        
    }

    private async Task RenderCurrentScreenAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        switch (context.State.Step)
        {
            case "book.staff":
                await RenderStaffAsync(context, ct);
                break;
            case "book.service":
                await RenderServicesAsync(context, ct);
                break;
            case "book.date":
                await RenderDateAsync(context, ct);
                break;
            case "book.time":
                await RenderTimesAsync(context, ct);
                break;
            case "book.confirm":
                await RenderConfirmationAsync(context, ct);
                break;
            case "bookings":
                await RenderBookingsAsync(context, ct);
                break;
            case "appointments":
                await RenderAppointmentStaffAsync(context, ct);
                break;
            case "appointments.date":
                await RenderAppointmentDateAsync(context, ct);
                break;
            case "appointments.list":
                await RenderAppointmentListAsync(context, ct);
                break;
        }
    }

    /// <summary>Lists active employees and starts the customer selection flow.</summary>
    private async Task RenderStaffAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var people = await staff.BrowseAsync(context.Caller.Actor, false, ct);
        var buttons = BookingKeyboard.Items(context.State, people, x => x.DisplayName, x => x.Id, "book.staff");
        await sender.SendMenuAsync(context.ChatId, "Choose an employee:", buttons, ct);
    }

    private async Task HandleBookingStaffAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        if (context.IsNavigation) { await RenderStaffAsync(context, ct); return; }
        if (context.ItemId is not Guid staffId) return;
        SetDraft(context, new BookingDraft(staffId, null, null, null));
        context.State.NavigateTo("book.service", clock.UtcNow);
        await RenderServicesAsync(context, ct);
    }

    private async Task RenderServicesAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var draft = GetDraft(context) ?? throw InvalidDraft();
        var offerings = await catalog.ListActiveOfferingsAsync(draft.StaffId, ct);
        var buttons = BookingKeyboard.Items(context.State, offerings,
            x => $"{x.ServiceName} ({x.Price:0.##}, {x.DurationMinutes} min)", x => x.Id, "book.service");
        await sender.SendMenuAsync(context.ChatId, "Choose a service:", buttons, ct);
    }

    private async Task HandleBookingServiceAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        if (context.IsNavigation) { await RenderServicesAsync(context, ct); return; }
        var draft = GetDraft(context) ?? throw InvalidDraft();
        if (context.ItemId is not Guid offeringId) return;
        var offering = await catalog.ListActiveOfferingsAsync(draft.StaffId, ct);
        var selected = offering.SingleOrDefault(x => x.Id == offeringId)
            ?? throw new AppException(ErrorCodes.StaffServiceNotFound, "Service not found.", AppErrorKind.NotFound);
        SetDraft(context, draft with { OfferingId = selected.Id, Date = null, Slot = null, Price = selected.Price, DurationMinutes = selected.DurationMinutes });
        context.State.SetBooking(new BookingSelection(selected.Id, null, null, null,
            selected.ServiceName, selected.Price, selected.DurationMinutes));
        // A changed offering is a new intent and must never reuse an older request key.
        context.State.ConfirmationRequestId = null;
        context.State.NavigateTo("book.date", clock.UtcNow);
        await RenderDateAsync(context, ct);
    }

    private async Task RenderDateAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        await sender.SendMenuAsync(context.ChatId, "Enter a date (YYYY-MM-DD).",
            [.. NavigationKeyboard.Create(context.State)], ct);
    }

    private async Task HandleBookingDateAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        if (context.IsNavigation) { await RenderDateAsync(context, ct); return; }
        if (!DateOnly.TryParseExact(context.Text?.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            await sender.SendTextAsync(context.ChatId, "Please enter the date as YYYY-MM-DD.", ct);
            return;
        }
        var draft = GetDraft(context) ?? throw InvalidDraft();
        SetDraft(context, draft with { Date = date, Slot = null });
        var booking = context.State.Booking ?? throw InvalidDraft();
        context.State.SetBooking(booking with { Date = date, SlotStartUtc = null, SlotEndUtc = null });
        context.State.ConfirmationRequestId = null;
        context.State.NavigateTo("book.time", clock.UtcNow);
        await RenderTimesAsync(context, ct);
    }

    private async Task RenderTimesAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var draft = GetDraft(context) ?? throw InvalidDraft();
        if (draft.OfferingId is not Guid offeringId || draft.Date is not DateOnly date)
            throw InvalidDraft();
        var slots = await availability.ListAsync(draft.StaffId, offeringId, date, ct);
        var slotIds = slots.ToDictionary(_ => Guid.NewGuid(), slot => slot);
        context.State.SetFeatureData("slots", slotIds);
        var buttons = slotIds.Select(pair => context.State.ItemButton(
            pair.Value.StartsAtUtc.ToString("HH:mm", CultureInfo.InvariantCulture), "book.time", pair.Key)).ToList();
        buttons.AddRange(NavigationKeyboard.Create(context.State));
        await sender.SendMenuAsync(context.ChatId,
            slots.Count == 0 ? "No times are available on that date." : "Choose a time:", buttons, ct);
    }

    private async Task HandleBookingTimeAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        if (context.IsNavigation) { await RenderTimesAsync(context, ct); return; }
        var draft = GetDraft(context) ?? throw InvalidDraft();
        var slots = context.State.GetFeatureData<Dictionary<Guid, AvailableSlot>>("slots");
        if (context.ItemId is not Guid slotId || slots is null || !slots.TryGetValue(slotId, out var slot)) return;
        SetDraft(context, draft with { Slot = slot });
        var booking = context.State.Booking ?? throw InvalidDraft();
        context.State.SetBooking(booking with
        {
            SlotStartUtc = slot.StartsAtUtc,
            SlotEndUtc = slot.EndsAtUtc
        });
        context.State.ConfirmationRequestId = null;
        context.State.NavigateTo("book.confirm", clock.UtcNow);
        await RenderConfirmationAsync(context, ct);
    }

    private async Task RenderConfirmationAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var draft = GetDraft(context) ?? throw InvalidDraft();
        if (draft.Slot is null) throw InvalidDraft();
        await sender.SendMenuAsync(context.ChatId,
            $"Review booking:\n{draft.Slot.StartsAtUtc:yyyy-MM-dd HH:mm} UTC\nPrice: {draft.Price:0.##}\nDuration: {draft.DurationMinutes} min",
            BookingKeyboard.Confirmation(context.State), ct);
    }

    private async Task HandleConfirmationAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        if (context.IsNavigation) { await RenderConfirmationAsync(context, ct); return; }
        if (context.Callback?.Route != "book.confirm")
        {
            await RenderConfirmationAsync(context, ct);
            return;
        }
        var draft = GetDraft(context) ?? throw InvalidDraft();
        if (draft.OfferingId is not Guid offeringId || draft.Slot is null) throw InvalidDraft();
        context.State.ConfirmationRequestId ??= Guid.NewGuid();
        var result = await bookings.CreateAsync(context.Caller.Actor,
            new CreateBookingRequest(context.State.ConfirmationRequestId.Value, context.Caller.Actor.UserId,
                offeringId, draft.Slot.StartsAtUtc.UtcDateTime, draft.Price, draft.DurationMinutes), ct);
        await sender.SendTextAsync(context.ChatId,
            $"Booking confirmed: {result.ServiceName} at {result.StartAtUtc:yyyy-MM-dd HH:mm} UTC.", ct);
        context.State.Reset(clock.UtcNow);
    }

    /// <summary>Routes the main-menu “My bookings” action and cancellation buttons.</summary>
    public async Task HandleBookingsAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        if (context.Callback?.Route == "bookings" || Command(context.Text) == "bookings")
            context.State.EnterFlow("bookings", clock.UtcNow);
        if (context.State.Step != "bookings") return;
        if (context.Callback?.Route == "book.cancel" && context.ItemId is Guid id)
        {
            await bookings.CancelAsync(context.Caller.Actor, id, ct);
            context.State.BeginStep("bookings", clock.UtcNow);
        }
        await RenderBookingsAsync(context, ct);
    }

    private async Task RenderBookingsAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var list = await bookings.ListBookingsByClientAsync(context.Caller.Actor, ct);
        var buttons = list.Where(x => x.Status == BookingStatus.Confirmed)
            .Select(x => context.State.ItemButton($"Cancel {x.ServiceName} {x.StartAtUtc:MM-dd HH:mm}",
                "book.cancel", x.Id)).ToList();
        buttons.AddRange(NavigationKeyboard.Create(context.State));
        await sender.SendMenuAsync(context.ChatId,
            list.Count == 0 ? "You have no bookings." : string.Join("\n", list.Select(FormatBooking)), buttons, ct);
    }

    /// <summary>Lists staff appointments and completes the selected appointment.</summary>
    public async Task HandleAppointmentsAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        if (context.Callback?.Route == "appointments" || Command(context.Text) == "appointments")
        {
            context.State.EnterFlow("appointments", clock.UtcNow);
            await RenderAppointmentStaffAsync(context, ct);
            return;
        }
        if (context.State.Step == "appointments")
        {
            if (context.Callback?.Route == "appointment.complete" && context.ItemId is Guid id)
            {
                await bookings.CompleteAsync(context.Caller.Actor, id, ct);
                context.State.BeginStep("appointments", clock.UtcNow);
            }
            await RenderAppointmentStaffAsync(context, ct);
        }
        else if (context.State.Step == "appointments.date")
        {
            await HandleAppointmentDateAsync(context, ct);
        }
        else if (context.State.Step == "appointments.list")
        {
            await HandleAppointmentListAsync(context, ct);
        }
    }

    private async Task RenderAppointmentStaffAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var people = await staff.BrowseForAppointmentsAsync(context.Caller.Actor, ct);
        var buttons = BookingKeyboard.Items(context.State, people, x => x.DisplayName, x => x.Id, "appointments.staff");
        await sender.SendMenuAsync(context.ChatId, "Choose an employee:", buttons, ct);
    }

    private async Task RenderAppointmentDateAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        await sender.SendMenuAsync(context.ChatId, "Enter the appointment date (YYYY-MM-DD).",
            [.. NavigationKeyboard.Create(context.State)], ct);
    }

    private async Task HandleAppointmentDateAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        if (context.IsNavigation) { await RenderAppointmentDateAsync(context, ct); return; }
        if (!DateOnly.TryParseExact(context.Text?.Trim(), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            await sender.SendTextAsync(context.ChatId, "Please enter the date as YYYY-MM-DD.", ct);
            return;
        }
        var draft = context.State.GetFeatureData<AppointmentDraft>(AppointmentKey)
            ?? throw InvalidDraft();
        context.State.SetFeatureData(AppointmentKey, draft with { Date = date });
        context.State.NavigateTo("appointments.list", clock.UtcNow);
        await RenderAppointmentListAsync(context, ct);
    }

    private async Task HandleAppointmentListAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        if (context.IsNavigation) { await RenderAppointmentListAsync(context, ct); return; }
        if (context.Callback?.Route == "appointment.complete" && context.ItemId is Guid bookingId)
        {
            await bookings.CompleteAsync(context.Caller.Actor, bookingId, ct);
            context.State.BeginStep("appointments.list", clock.UtcNow);
        }
        await RenderAppointmentListAsync(context, ct);
    }

    private async Task RenderAppointmentListAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var draft = context.State.GetFeatureData<AppointmentDraft>(AppointmentKey)
            ?? throw InvalidDraft();
        if (draft.Date is not DateOnly date) throw InvalidDraft();
        var list = await bookings.ListByStaffAndRangeAsync(
            context.Caller.Actor, draft.StaffId, date, date, ct);
        var buttons = list.Where(x => x.Status == BookingStatus.Confirmed)
            .Select(x => context.State.ItemButton($"Complete {x.ServiceName} {x.StartAtUtc:HH:mm}",
                "appointment.complete", x.Id)).ToList();
        buttons.AddRange(NavigationKeyboard.Create(context.State));
        await sender.SendMenuAsync(context.ChatId,
            list.Count == 0 ? "No appointments for this date." : string.Join("\n", list.Select(FormatBooking)),
            buttons, ct);
    }

    private static string? Command(string? text) => text?.Split(' ', 2)[0].Split('@', 2)[0].TrimStart('/');
    private static string FormatBooking(BookingDto x) => $"{x.ServiceName}: {x.StartAtUtc:yyyy-MM-dd HH:mm} UTC ({x.Status})";
    private static BookingDraft? GetDraft(TelegramHandlerContext c) => c.State.GetFeatureData<BookingDraft>(DraftKey);
    private static void SetDraft(TelegramHandlerContext c, BookingDraft draft) => c.State.SetFeatureData(DraftKey, draft);
    private static AppException InvalidDraft() => new(ErrorCodes.ValidationFailed, "Booking details are incomplete.", AppErrorKind.Validation);
    private sealed record BookingDraft(Guid StaffId, Guid? OfferingId, DateOnly? Date, AvailableSlot? Slot,
        decimal Price = 0, int DurationMinutes = 0);
    private sealed record AppointmentDraft(Guid StaffId, DateOnly? Date);
}

/// <summary>Exposes one state-machine handler under each callback route it owns.</summary>
public sealed class BookingHandlerRoute(BookingHandler handler, string route) : ITelegramHandler
{
    public string Route => route;
    public Task HandleAsync(TelegramHandlerContext context, CancellationToken ct) =>
        handler.HandleAsync(context, ct);
}
