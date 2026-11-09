using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BookingEndpointsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CreateAndRetryUseAuthenticatedClientAndPersistOneBooking()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        await using var host = seed.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ApiAccess:Keys:0:Key"] = "booking-test-key",
                    ["ApiAccess:Keys:0:TelegramUserId"] = seed.Client.TelegramUserId.ToString()
                }));
            builder.ConfigureTestServices(BookingTestSetup.ConfigureClock);
        });
        using var client = host.CreateClient();
        const string route = "/api/bookings";

        using var anonymous = await client.PostAsJsonAsync(route, seed.Request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, "booking-test-key");
        using var createdResponse = await client.PostAsJsonAsync(route, seed.Request);
        createdResponse.EnsureSuccessStatusCode();
        var created = Assert.IsType<BookingDto>(await createdResponse.Content.ReadFromJsonAsync<BookingDto>());
        Assert.Equal(BookingStatus.Confirmed, created.Status);
        Assert.Equal(seed.Request.StaffServiceId, created.StaffServiceId);
        Assert.Equal(seed.Request.StartAtUtc, created.StartAtUtc);
        Assert.Equal(seed.Request.StartAtUtc.AddMinutes(30), created.EndAtUtc);

        using var retry = await client.PostAsJsonAsync(route, seed.Request);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(created, await retry.Content.ReadFromJsonAsync<BookingDto>());

        using var changed = await client.PostAsJsonAsync(route, seed.Request with { ExpectedPrice = 26m });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var problem = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
        Assert.Equal(ErrorCodes.RequestIdConflict, problem.RootElement.GetProperty("code").GetString());

        await using var db = fixture.CreateContext();
        var stored = await db.Bookings.SingleAsync(x => x.RequestId == seed.Request.RequestId);
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(seed.Client.Id, stored.ClientUserId);
    }
}
