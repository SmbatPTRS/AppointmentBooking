namespace AppointmentBooking.Domain.Entities;

public sealed class BusinessSettings
{
    public const int SingletonId = 1;

    public int Id { get; private set; }
    public string DisplayName { get; private set; } = null!;
    public string StaffLabel { get; private set; } = null!;
    public string TimeZoneId { get; private set; } = null!;
    public string CurrencyCode { get; private set; } = null!;

    private BusinessSettings()
    {
        // Used by EF Core.
    }

    public BusinessSettings(
        string displayName,
        string staffLabel,
        string timeZoneId,
        string currencyCode)
    {
        Update(displayName, staffLabel, timeZoneId, currencyCode);
        Id = SingletonId;
    }

    public void Update(
        string displayName,
        string staffLabel,
        string timeZoneId,
        string currencyCode)
    {
        string normalizedName = ValidateText(
            displayName, 200, nameof(displayName));

        string normalizedLabel = ValidateText(
            staffLabel, 100, nameof(staffLabel));

        string normalizedTimeZone = ValidateText(
            timeZoneId, 100, nameof(timeZoneId));

        string normalizedCurrency = ValidateText(
            currencyCode, 3, nameof(currencyCode))
            .ToUpperInvariant();

        if (normalizedCurrency.Length != 3 ||
            normalizedCurrency.Any(character =>
                character < 'A' || character > 'Z'))
        {
            throw new ArgumentException(
                "Currency code must contain exactly three English letters.",
                nameof(currencyCode));
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(normalizedTimeZone);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException(
                "The timezone is not supported.",
                nameof(timeZoneId),
                exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException(
                "The timezone configuration is invalid.",
                nameof(timeZoneId),
                exception);
        }

        // Assign only after every value has passed validation.
        DisplayName = normalizedName;
        StaffLabel = normalizedLabel;
        TimeZoneId = normalizedTimeZone;
        CurrencyCode = normalizedCurrency;
    }

    private static string ValidateText(
        string value,
        int maximumLength,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "A value is required.",
                parameterName);
        }

        string normalized = value.Trim();

        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value cannot exceed {maximumLength} characters.",
                parameterName);
        }

        return normalized;
    }
}