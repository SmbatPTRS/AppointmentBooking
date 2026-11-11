using System.Text;
using System.Text.RegularExpressions;

namespace AppointmentBooking.Api.Telegram;

public sealed record TelegramCallback(string Route, Guid Revision, string? ButtonKey = null);

public static class CallbackDataParser
{
    public static bool IsValidRoute(string route) => Regex.IsMatch(route, @"\A[a-z][a-z0-9_.]{0,19}\z");

    public static string Format(string route, Guid revision)
    {
        if (!IsValidRoute(route) || revision == Guid.Empty) throw new ArgumentException("Invalid callback route or revision.");
        return $"1:{revision:N}:{route}";
    }

    public static string FormatItem(string route, Guid revision, string buttonKey)
    {
        _ = Format(route, revision);
        if (!IsValidKey(buttonKey)) throw new ArgumentException("Invalid button key.", nameof(buttonKey));
        return $"2:{revision:N}:{route}:{buttonKey}";
    }

    private static bool IsValidKey(string key) => Regex.IsMatch(key, @"\A[a-zA-Z0-9_-]{1,8}\z");

    public static bool TryParse(string? data, out TelegramCallback? callback)
    {
        callback = null;
        if (data is null || Encoding.UTF8.GetByteCount(data) > 64) return false;
        var parts = data.Split(':');
        if (!((parts.Length == 3 && parts[0] == "1") ||
              (parts.Length == 4 && parts[0] == "2" && IsValidKey(parts[3]))) || !IsValidRoute(parts[2]) ||
            !Guid.TryParseExact(parts[1], "N", out var revision) || revision == Guid.Empty) return false;
        callback = new TelegramCallback(parts[2], revision, parts.Length == 4 ? parts[3] : null);
        return true;
    }
}
