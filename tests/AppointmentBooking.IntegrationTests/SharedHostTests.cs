using AppointmentBooking.Application.Common;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);
    }
}
