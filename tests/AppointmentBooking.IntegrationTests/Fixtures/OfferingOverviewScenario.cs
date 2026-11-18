using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Domain.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AppointmentBooking.IntegrationTests.Fixtures;

// B's isolated catalogue data and authenticated HTTP clients; no shared fixture state is changed.
public sealed record OfferingOverviewScenario(
    ApiFactory Factory, WebApplicationFactory<Program> Host, User Owner, User Employee,
    User Customer, StaffMember Staff, StaffMember Other, Service ActiveService,
    Service InactiveService, StaffService Active, StaffService Disabled, StaffService Hidden)
    : IAsyncDisposable
{
    public string Url => $"/api/staff/{Staff.Id}/services/management";
    public HttpClient Client(string? key = "employee")
    {
        var client = Host.CreateClient();
        if (key is not null) client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, key);
        return client;
    }

    public static async Task<OfferingOverviewScenario> Create(PostgresFixture fixture)
    {
        await using var db = fixture.CreateContext();
        User User(string name) => new(Random.Shared.NextInt64(10_000_000_000, 80_000_000_000), name, null, DateTime.UtcNow);
        var owner = User("Overview owner");
        var employee = User("Overview employee");
        var customer = User("Overview customer");
        var otherUser = User("Other employee");
        var staff = new StaffMember(employee.Id, "Overview-" + Guid.NewGuid());
        var other = new StaffMember(otherUser.Id, "Other-" + Guid.NewGuid());
        var activeService = new Service("A active " + Guid.NewGuid());
        var disabledService = new Service("B disabled offering " + Guid.NewGuid());
        var inactiveService = new Service("C inactive service " + Guid.NewGuid());
        inactiveService.Deactivate();
        var active = new StaffService(staff.Id, activeService.Id, 12.50m, 30);
        var disabled = new StaffService(staff.Id, disabledService.Id, 25m, 45);
        disabled.Deactivate();
        var hidden = new StaffService(staff.Id, inactiveService.Id, 50m, 60);
        db.AddRange(owner, employee, customer, otherUser, staff, other,
            activeService, disabledService, inactiveService, active, disabled, hidden);
        await db.SaveChangesAsync();
        var factory = new ApiFactory(fixture.ConnectionString);
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Owner:TelegramUserId"] = owner.TelegramUserId.ToString(),
                ["ApiAccess:Keys:0:Key"] = "owner",
                ["ApiAccess:Keys:0:TelegramUserId"] = owner.TelegramUserId.ToString(),
                ["ApiAccess:Keys:1:Key"] = "employee",
                ["ApiAccess:Keys:1:TelegramUserId"] = employee.TelegramUserId.ToString(),
                ["ApiAccess:Keys:2:Key"] = "customer",
                ["ApiAccess:Keys:2:TelegramUserId"] = customer.TelegramUserId.ToString()
            })));
        return new(factory, host, owner, employee, customer, staff, other,
            activeService, inactiveService, active, disabled, hidden);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Factory.DisposeAsync();
    }
}
