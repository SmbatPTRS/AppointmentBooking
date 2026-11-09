namespace AppointmentBooking.Api.Configuration;

public sealed class TelegramOptions
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "Polling";
    public string BotToken { get; set; } = string.Empty;
    public string BotUsername { get; set; } = string.Empty;
    public string WebhookUrl { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
}
