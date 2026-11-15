using System.Diagnostics;
using System.Runtime.InteropServices;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

public sealed class SetupCommandTests
{
    [Fact]
    public async Task ExplicitSeedCommandPreservesOwnerEditsOnRerunAndDoesNotStartServer()
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        await RunAsync(fixture.ConnectionString, "AMD", true, withEnvFile: true);
        await using (var db = fixture.CreateContext())
        {
            var settings = await db.BusinessSettings.SingleAsync();
            Assert.Equal("Setup business", settings.DisplayName);
            Assert.Equal("UTC", settings.TimeZoneId);
            Assert.Equal("AMD", settings.CurrencyCode);
            settings.Update("Owner edit", "Specialist", "Asia/Yerevan", "USD");
            await db.SaveChangesAsync();
            await db.BookingCoordinationRows.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ChangeVersion, 4L));
        }
        await RunAsync(fixture.ConnectionString, "AMD", true, withEnvFile: true);
        await using var verify = fixture.CreateContext();
        var stored = await verify.BusinessSettings.SingleAsync();
        Assert.Equal("Owner edit", stored.DisplayName);
        Assert.Equal("Specialist", stored.StaffLabel);
        Assert.Equal("Asia/Yerevan", stored.TimeZoneId);
        Assert.Equal("USD", stored.CurrencyCode);
        Assert.Equal(4L, (await verify.BookingCoordinationRows.SingleAsync()).ChangeVersion);
        await using var host = new ApiFactory(fixture.ConnectionString);
        using var http = host.CreateClient();
        using var health = await http.GetAsync("/health");
        using var databaseHealth = await http.GetAsync("/health/db");
        health.EnsureSuccessStatusCode();
        databaseHealth.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task InvalidSeedDefaultsFailWithoutWritingRows()
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        await RunAsync(fixture.ConnectionString, "US", false);
        await using var db = fixture.CreateContext();
        Assert.Empty(await db.BusinessSettings.ToListAsync());
        Assert.Empty(await db.BookingCoordinationRows.ToListAsync());
    }

    private static async Task RunAsync(string connectionString, string currency, bool success, bool withEnvFile = false)
    {
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var contentRoot = Directory.CreateTempSubdirectory("appointment-seed-test-");
        try
        {
            if (withEnvFile)
                await File.WriteAllTextAsync(Path.Combine(contentRoot.FullName, ".env"), "Seed__DisplayName=File business\n");
            var start = new ProcessStartInfo(dotnet)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--seed-business-settings");
            start.ArgumentList.Add("--Seed:DisplayName=Setup business");
            start.ArgumentList.Add($"--contentRoot={contentRoot.FullName}");
            // Test-only config; the setup command must not start HTTP or Telegram receivers.
            start.Environment["ASPNETCORE_ENVIRONMENT"] = withEnvFile ? "Development" : "Testing";
            start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;
            start.Environment["Telegram__Enabled"] = "false";
            start.Environment["Seed__DisplayName"] = "Environment business";
            start.Environment["Seed__StaffLabel"] = "Employee";
            start.Environment["Seed__TimeZoneId"] = "UTC";
            start.Environment["Seed__CurrencyCode"] = currency;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            var output = await stdout;
            var errorOutput = await stderr;
            Assert.Equal(success, process.ExitCode == 0);
            Assert.DoesNotContain("Now listening on", output);
            if (success) Assert.Contains("Business settings seed completed", output);
            else Assert.Contains("Currency code must contain exactly three English letters.", errorOutput);
        }
        finally { contentRoot.Delete(recursive: true); }
    }
}
