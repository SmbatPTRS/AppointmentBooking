using AppointmentBooking.Api.Configuration;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Identity;
using AppointmentBooking.Application.Common;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AppointmentBooking.IntegrationTests;

public sealed class SharedHostTests
{
    [Fact]
    public async Task HostUsesTestConfigurationAndReplaceableClockWithoutDatabaseAccess()
    {
        // No connection is opened; /health deliberately does not access the database.
        const string testConnection = "Host=localhost;Database=not_opened;Username=test;Password=test";
        await using var factory = new ApiFactory(testConnection);
        var clock = new FixedClock();
        await using var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IClock>(clock)));
        using var client = configured.CreateClient();
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        Assert.Same(clock, configured.Services.GetRequiredService<IClock>());
        var configuration = configured.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(testConnection, configuration.GetConnectionString("DefaultConnection"));
        Assert.Equal("false", configuration["Telegram:Enabled"]);
    }

    [Fact]
    public async Task HostRegistersScopedAccessServiceWithOwnerOptions()
    {
        const string testConnection = "Host=localhost;Database=not_opened;Username=test;Password=test";
        await using var factory = new ApiFactory(testConnection);
        await using var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Owner:TelegramUserId"] = "12345678901" })));
        using var firstScope = configured.Services.CreateScope();
        using var secondScope = configured.Services.CreateScope();

        var options = configured.Services.GetRequiredService<IOptions<OwnerOptions>>().Value;
        Assert.Equal(12345678901L, options.TelegramUserId);

        var access = firstScope.ServiceProvider.GetRequiredService<IAccessService>();
        Assert.IsType<AccessService>(access);
        Assert.Same(access, firstScope.ServiceProvider.GetRequiredService<IAccessService>());
        Assert.NotSame(access, secondScope.ServiceProvider.GetRequiredService<IAccessService>());
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);
    }
}
