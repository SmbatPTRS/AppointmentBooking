using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AppointmentBooking.IntegrationTests.Fixtures;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public ApiFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A test database connection string is required.",
                nameof(connectionString));
        }

        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] =
                        _connectionString,

                    ["Telegram:Enabled"] = "false",

                    ["Telegram:BotToken"] = string.Empty,
                    ["Telegram:WebhookUrl"] = string.Empty,
                    ["Telegram:WebhookSecret"] = string.Empty,

                    ["Owner:TelegramUserId"] = "0"
                });
        });
    }
}