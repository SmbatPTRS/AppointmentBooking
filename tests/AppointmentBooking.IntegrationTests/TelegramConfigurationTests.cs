using AppointmentBooking.Api.Configuration;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace AppointmentBooking.IntegrationTests;

public sealed class TelegramConfigurationTests
{
    [Fact]
    public void DisabledTelegramRequiresNoTokenAndCreatesNoNetworkClientAtStartup()
    {
        var result = new TelegramOptionsValidator().Validate(null, new TelegramOptions());
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("Polling", "", "")]
    [InlineData("Neither", "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345", "")]
    [InlineData("Webhook", "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345", "http://example.com/hook")]
    [InlineData("Webhook", "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345", "https://localhost/hook")]
    public void EnabledTelegramRejectsInvalidConfigurationWithoutLeakingToken(string mode, string token, string url)
    {
        var result = new TelegramOptionsValidator().Validate(null, new TelegramOptions
        { Enabled = true, Mode = mode, BotToken = token, WebhookUrl = url });
        Assert.True(result.Failed);
        if (token.Length > 0) Assert.DoesNotContain(token, string.Join(" ", result.Failures));
    }

    [Fact]
    public void ClientIsReusedAcrossScopesWithoutSendingRequests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:Enabled"] = "true", ["Telegram:Mode"] = "Polling",
            ["Telegram:BotToken"] = "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddTelegramInfrastructure();
        using var provider = services.BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        Assert.Same(first.ServiceProvider.GetRequiredService<ITelegramBotClient>(),
            second.ServiceProvider.GetRequiredService<ITelegramBotClient>());
        Assert.IsType<TelegramMessageSender>(provider.GetRequiredService<ITelegramMessageSender>());
        Assert.Empty(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
    }
}
