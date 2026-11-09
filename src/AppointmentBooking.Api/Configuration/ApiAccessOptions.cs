namespace AppointmentBooking.Api.Configuration;

public sealed class ApiAccessOptions
{
    public const string SectionName = "ApiAccess";

    public ApiKeyEntry[] Keys { get; set; } = [];
}

public sealed class ApiKeyEntry
{
    public string Key { get; set; } = "";
    public long TelegramUserId { get; set; }
}
