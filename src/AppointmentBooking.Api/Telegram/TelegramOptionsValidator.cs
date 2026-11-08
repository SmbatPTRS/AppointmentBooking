using AppointmentBooking.Api.Configuration;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace AppointmentBooking.Api.Telegram;

public sealed class TelegramOptionsValidator : IValidateOptions<TelegramOptions>
{
    public ValidateOptionsResult Validate(string? name, TelegramOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;
        var errors = new List<string>();
        if (!Regex.IsMatch(options.BotToken, @"^[0-9]+:[A-Za-z0-9_-]{20,}$"))
            errors.Add("Telegram:BotToken is required and must have a valid bot-token format.");
        if (options.Mode is not ("Polling" or "Webhook"))
            errors.Add("Telegram:Mode must be Polling or Webhook.");
        if (options.Mode == "Webhook")
        {
            if (!Uri.TryCreate(options.WebhookUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo))
                errors.Add("Telegram:WebhookUrl must be a public HTTPS URL without credentials.");
            if (!Regex.IsMatch(options.WebhookSecret, @"^[A-Za-z0-9_-]{1,256}$"))
                errors.Add("Telegram:WebhookSecret must contain 1–256 letters, digits, underscores or hyphens.");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
