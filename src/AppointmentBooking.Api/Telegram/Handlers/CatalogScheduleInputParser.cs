using System.Globalization;

namespace AppointmentBooking.Api.Telegram.Handlers;

/// <summary>
/// Text parsing for B's catalogue/schedule screens only. Shared callback parsing,
/// conversation state, permissions and business validation remain separate.
/// </summary>
public static class CatalogScheduleInputParser
{
    public static bool TryParsePrice(string? text, out decimal price)
    {
        price = default;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
            return false;
        var parts = value.Split('.');
        if (parts.Length > 2 || parts.Any(p => p.Length == 0 || p.Any(c => c < '0' || c > '9')))
            return false;
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
            return false;

        // Decimal.TryParse can silently round excessive precision. Require an
        // exact value after removing insignificant leading/trailing zeros.
        var integer = parts[0].TrimStart('0');
        if (integer.Length == 0)
            integer = "0";
        var fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : "";
        var normalized = fraction.Length == 0 ? integer : integer + "." + fraction;
        if (parsed.ToString("0.############################", CultureInfo.InvariantCulture) != normalized)
            return false;
        price = parsed;
        return true;
    }

    public static bool TryParseDuration(string? text, out int durationMinutes)
    {
        durationMinutes = default;
        if (!int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            return false;
        durationMinutes = parsed;
        return true;
    }

    public static bool TryParseDate(string? text, out DateOnly date)
    {
        var value = text?.Trim();
        date = default;
        return value is { Length: 10 } && DateOnly.TryParseExact(value, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public static bool TryParseTime(string? text, out TimeOnly time)
    {
        var value = text?.Trim();
        time = default;
        return value is { Length: 5 } && TimeOnly.TryParseExact(value, "HH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
}
