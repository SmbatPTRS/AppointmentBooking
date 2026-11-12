using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogEndpointsTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task AllRoutesRequireValidApiKey(string? key)
    {
        await using var setup = await Seed();
        using var client = setup.Client(key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/services")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(setup.Offerings)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/services", new { name = "Cut" })).StatusCode);
    }

    [Fact]
    public async Task OwnerCreatesTrimmedServiceAndLocationReturnsVisibleCatalog()
    {
        await using var setup = await Seed();
        using var owner = setup.Client("owner");
        var name = "New-" + Guid.NewGuid();
        using var response = await owner.PostAsJsonAsync("/api/services", new { name = "  " + name + "  " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ServiceDto>())!;
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(name, created.Name);
        Assert.True(created.IsActive);
        Assert.NotNull(response.Headers.Location);
        using var customer = setup.Client("customer");
        var listed = await customer.GetFromJsonAsync<ServiceDto[]>(response.Headers.Location);
        Assert.Contains(created, listed!);
        await using var db = fixture.CreateContext();
        Assert.Equal(name, (await db.Services.SingleAsync(s => s.Id == created.Id)).Name);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("employee")]
    public async Task CallerCannotSpoofOwnerInPayloadOrHeader(string key)
    {
        await using var setup = await Seed();
        using var client = setup.Client(key);
        client.DefaultRequestHeaders.Add("X-User-Id", setup.Owner.Id.ToString());
        var name = "Forbidden-" + Guid.NewGuid();
        using var response = await client.PostAsJsonAsync("/api/services", new { name, userId = setup.Owner.Id, isOwner = true });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("access.forbidden", json.RootElement.GetProperty("code").GetString());
        await using var db = fixture.CreateContext();
        Assert.False(await db.Services.AnyAsync(s => s.Name == name));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":\"\"}")]
    [InlineData("{\"name\":\"   \"}")]
    [InlineData("{\"name\":42}")]
    [InlineData("{")]
    public async Task InvalidRequestReturns400WithoutPersisting(string body)
    {
        await using var setup = await Seed();
        using var client = setup.Client("owner");
        await using var db = fixture.CreateContext();
        var before = await db.Services.CountAsync();
        using var response = await client.PostAsync("/api/services", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await db.Services.CountAsync());
    }

    [Theory]
    [InlineData(200, HttpStatusCode.Created)]
    [InlineData(201, HttpStatusCode.BadRequest)]
    public async Task NameLimitIsAppliedAfterTrimming(int length, HttpStatusCode expected)
    {
        await using var setup = await Seed();
        using var client = setup.Client("owner");
        var name = Guid.NewGuid().ToString("N") + new string('x', length - 32);
        using var response = await client.PostAsJsonAsync("/api/services", new { name = "  " + name + "  " });
        Assert.Equal(expected, response.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Equal(expected == HttpStatusCode.Created, await db.Services.AnyAsync(s => s.Name == name));
    }

    [Fact]
    public async Task CustomerListsOnlyActiveServiceTypes()
    {
        await using var setup = await Seed();
        using var client = setup.Client("customer");
        var result = (await client.GetFromJsonAsync<ServiceDto[]>("/api/services"))!;
        Assert.Contains(result, s => s.Id == setup.Service.Id && s.Name == setup.Service.Name);
        Assert.DoesNotContain(result, s => s.Id == setup.Hidden.Id);
        Assert.All(result, s => Assert.True(s.IsActive));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task OfferingsRequireActiveStaffServiceAndOffering(bool activeStaff, bool activeService, bool activeOffering)
    {
        await using var setup = await Seed(activeStaff, activeService, activeOffering);
        using var client = setup.Client("customer");
        var result = (await client.GetFromJsonAsync<StaffServiceDto[]>(setup.Offerings))!;
        if (activeStaff && activeService && activeOffering)
        {
            var item = Assert.Single(result);
            Assert.Equal(setup.Offering.Id, item.Id);
            Assert.Equal(setup.Staff.Id, item.StaffMemberId);
            Assert.Equal(setup.Service.Id, item.ServiceId);
            Assert.Equal(setup.Service.Name, item.ServiceName);
            Assert.Equal(1250.25m, item.Price);
            Assert.Equal(45, item.DurationMinutes);
            Assert.True(item.IsActive);
        }
        else Assert.Empty(result);
    }

    [Fact]
    public async Task UnknownStaffReturnsEmptyArrayAndEmptyIdentifierIsRejected()
    {
        await using var setup = await Seed();
        using var client = setup.Client("customer");
        Assert.Empty((await client.GetFromJsonAsync<StaffServiceDto[]>($"/api/staff/{Guid.NewGuid()}/services"))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/staff/{Guid.Empty}/services")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/staff/not-a-guid/services")).StatusCode);
    }

    [Fact]
    public async Task OfferingChangesAreVisibleOnNextRequestWithoutRestart()
    {
        await using var setup = await Seed();
        using var client = setup.Client("customer");
        Assert.Single((await client.GetFromJsonAsync<StaffServiceDto[]>(setup.Offerings))!);
        await using var db = fixture.CreateContext();
        var offering = await db.StaffServices.SingleAsync(s => s.Id == setup.Offering.Id);
        offering.Deactivate();
        await db.SaveChangesAsync();
        Assert.Empty((await client.GetFromJsonAsync<StaffServiceDto[]>(setup.Offerings))!);
    }

    private async Task<Setup> Seed(bool activeStaff = true, bool activeService = true, bool activeOffering = true)
    {
        await using var db = fixture.CreateContext();
        User User(string name) => new(Random.Shared.NextInt64(10000000000, 90000000000), name, null, DateTime.UtcNow);
        var owner = User("Owner");
        var employee = User("Employee");
        var customer = User("Customer");
        var otherUser = User("Other");
        var staff = new StaffMember(employee.Id, "Employee");
        var other = new StaffMember(otherUser.Id, "Other");
        var service = new Service("Visible-" + Guid.NewGuid());
        var hidden = new Service("Hidden-" + Guid.NewGuid());
        hidden.Deactivate();
        var offering = new StaffService(staff.Id, service.Id, 1250.25m, 45);
        if (!activeStaff) staff.Deactivate();
        if (!activeService) service.Deactivate();
        if (!activeOffering) offering.Deactivate();
        db.AddRange(owner, employee, customer, otherUser, staff, other, service, hidden, offering,
            new StaffService(other.Id, service.Id, 9000m, 60), new StaffService(staff.Id, hidden.Id, 100m, 15));
        await db.SaveChangesAsync();
        var factory = new ApiFactory(fixture.ConnectionString);
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Owner:TelegramUserId"] = owner.TelegramUserId.ToString(),
                ["ApiAccess:Keys:0:Key"] = "owner", ["ApiAccess:Keys:0:TelegramUserId"] = owner.TelegramUserId.ToString(),
                ["ApiAccess:Keys:1:Key"] = "employee", ["ApiAccess:Keys:1:TelegramUserId"] = employee.TelegramUserId.ToString(),
                ["ApiAccess:Keys:2:Key"] = "customer", ["ApiAccess:Keys:2:TelegramUserId"] = customer.TelegramUserId.ToString()
            })));
        return new Setup(factory, host, owner, staff, service, hidden, offering);
    }

    private sealed record Setup(ApiFactory Factory, WebApplicationFactory<Program> Host, User Owner,
        StaffMember Staff, Service Service, Service Hidden, StaffService Offering) : IAsyncDisposable
    {
        public string Offerings => $"/api/staff/{Staff.Id}/services";
        public HttpClient Client(string? key)
        {
            var client = Host.CreateClient();
            if (key is not null) client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, key);
            return client;
        }
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); await Factory.DisposeAsync(); }
    }
}
