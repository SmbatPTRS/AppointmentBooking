using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Business;

public sealed class BusinessSettingsService(
    IAccessService access,
    IBusinessSettingsRepository settings,
    IBookingRepository bookings,
    IBookingCoordinationRepository coordination,
    IUnitOfWork unitOfWork)
{
    public async Task<BusinessSettingsView> GetAsync(CancellationToken ct = default) =>
        ToView(await settings.GetAsync(ct) ?? throw MissingSettings());

    public async Task<BusinessSettingsView> UpdateLabelsAsync(
        Actor actor, string displayName, string staffLabel, CancellationToken ct = default)
    {
        await access.RequireOwnerAsync(actor, ct);

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await coordination.LockBusinessAsync(ct);
        var current = await settings.GetForUpdateAsync(ct) ?? throw MissingSettings();
        Update(current, displayName, staffLabel, current.TimeZoneId, current.CurrencyCode);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToView(current);
    }

    public async Task<BusinessSettingsView> UpdateTimeZoneAndCurrencyAsync(
        Actor actor, string timeZoneId, string currencyCode, CancellationToken ct = default)
    {
        await access.RequireOwnerAsync(actor, ct);
        await coordination.IncrementBusinessVersionAsync(ct);

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await coordination.LockBusinessAsync(ct);
        var current = await settings.GetForUpdateAsync(ct) ?? throw MissingSettings();

        if (await bookings.AnyBookingExistsAsync(ct))
        {
            throw new AppException(ErrorCodes.BusinessSettingsLocked,
                "Timezone and currency cannot be changed after a booking exists.", AppErrorKind.Conflict);
        }

        Update(current, current.DisplayName, current.StaffLabel, timeZoneId, currencyCode);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToView(current);
    }

    private static void Update(BusinessSettings current,
        string displayName, string staffLabel, string timeZoneId, string currencyCode)
    {
        try
        {
            current.Update(displayName, staffLabel, timeZoneId, currencyCode);
        }
        catch (ArgumentException exception)
        {
            var code = exception.ParamName switch
            {
                nameof(timeZoneId) => ErrorCodes.InvalidTimeZone,
                nameof(currencyCode) => ErrorCodes.InvalidCurrency,
                _ => ErrorCodes.ValidationFailed
            };
            throw new AppException(code, exception.Message, AppErrorKind.Validation);
        }
    }

    private static AppException MissingSettings() =>
        new(ErrorCodes.BusinessSettingsNotFound, "Business settings have not been seeded.", AppErrorKind.NotFound);

    private static BusinessSettingsView ToView(BusinessSettings current) =>
        new(current.DisplayName, current.StaffLabel, current.TimeZoneId, current.CurrencyCode);
}
