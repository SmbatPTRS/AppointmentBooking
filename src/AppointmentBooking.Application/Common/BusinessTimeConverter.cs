namespace AppointmentBooking.Application.Common;

public static class BusinessTimeConverter
{
    /// Resolves the stored IANA id. Throws a clear AppException if invalid (BR-13).
    public static TimeZoneInfo GetTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException
                                       or InvalidTimeZoneException)
        {
            throw new AppException(
                ErrorCodes.InvalidTimeZone, // use your team's agreed code
                $"Business timezone '{timeZoneId}' is not valid.",
                AppErrorKind.Validation);   // use your team's agreed kind
        }
    }

    /// Local date -> UTC range [fromUtc, toUtc). End is exclusive.
    public static (DateTime FromUtc, DateTime ToUtc) GetUtcRange(
        DateOnly localDateFrom, DateOnly localDateTo, string timeZoneId)
    {
        if (localDateTo < localDateFrom)
            throw new ArgumentException("End date is before start date.");

        var tz = GetTimeZone(timeZoneId);

        var fromUtc = LocalMidnightToUtc(localDateFrom, tz);
        var toUtc   = LocalMidnightToUtc(localDateTo.AddDays(1), tz);
        return (fromUtc, toUtc);
    }

    /// Single-day convenience overload.
    public static (DateTime FromUtc, DateTime ToUtc) GetUtcRange(
        DateOnly localDate, string timeZoneId)
        => GetUtcRange(localDate, localDate, timeZoneId);

    /// Local wall-clock (date + time) -> UTC instant. Used for slot starts.
    /// Invalid (skipped) and ambiguous (repeated) times are rejected, matching BR-13.
    public static DateTime LocalToUtc(DateOnly date, TimeOnly time, string timeZoneId)
    {
        var tz = GetTimeZone(timeZoneId);
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);

        if (tz.IsInvalidTime(local) || tz.IsAmbiguousTime(local))
            throw new AppException(
                ErrorCodes.InvalidLocalTime,
                "This local time does not exist or is ambiguous in the business timezone.",
                AppErrorKind.Validation);

        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    /// UTC instant -> business-local DateTime, for display.
    public static DateTime UtcToLocal(DateTime utc, string timeZoneId)
    {
        if (utc.Kind == DateTimeKind.Local)
            throw new ArgumentException("Expected a UTC value, not Local.", nameof(utc));

        var asUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(asUtc, GetTimeZone(timeZoneId));
    }

    /// UTC instant -> business-local calendar date.
    public static DateOnly UtcToLocalDate(DateTime utc, string timeZoneId)
        => DateOnly.FromDateTime(UtcToLocal(utc, timeZoneId));

    // Day boundaries: midnight is the start of a day. In zones that skip
    // midnight on a DST day, the first valid instant of that day is used.
    private static DateTime LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue),
                                         DateTimeKind.Unspecified);

        while (tz.IsInvalidTime(local))
            local = local.AddMinutes(1);   // skip the gap to the first valid minute

        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }
}