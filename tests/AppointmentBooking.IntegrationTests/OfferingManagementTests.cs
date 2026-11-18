using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class OfferingManagementTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("owner")]
    [InlineData("employee")]
    public async Task AuthorizedReadIncludesInactiveOfferingsWithIndependentStatuses(string key)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var client = setup.Client(key);
        var rows = (await client.GetFromJsonAsync<ManagedOfferingDto[]>(setup.Url))!;
        Assert.Equal(3, rows.Length);
        Assert.Equal(new[] { setup.Active.Id, setup.Disabled.Id, setup.Hidden.Id }, rows.Select(r => r.Id));
        Assert.All(rows, r => Assert.Equal(setup.Staff.Id, r.StaffMemberId));
        var active = rows[0];
        Assert.Equal(12.50m, active.Price);
        Assert.Equal(30, active.DurationMinutes);
        Assert.True(active.IsActive);
        Assert.True(active.ServiceIsActive);
        Assert.True(active.StaffIsActive);
        Assert.False(rows[1].IsActive);
        Assert.True(rows[1].ServiceIsActive);
        Assert.True(rows[2].IsActive);
        Assert.False(rows[2].ServiceIsActive);
        var publicRows = (await client.GetFromJsonAsync<StaffServiceDto[]>($"/api/staff/{setup.Staff.Id}/services"))!;
        Assert.Equal(setup.Active.Id, Assert.Single(publicRows).Id);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("invalid", HttpStatusCode.Unauthorized)]
    [InlineData("customer", HttpStatusCode.Forbidden)]
    public async Task AuthenticationAndManagementAccessAreRequired(string? key, HttpStatusCode status)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var client = setup.Client(key);
        using var response = await client.GetAsync(setup.Url);
        Assert.Equal(status, response.StatusCode);
        using var selection = await client.GetAsync("/api/offerings/staff");
        Assert.Equal(status, selection.StatusCode);
    }

    [Fact]
    public async Task EmployeeCannotReadAnotherEmployeesManagementCatalogue()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var client = setup.Client();
        client.DefaultRequestHeaders.Add("X-User-Id", setup.Owner.Id.ToString());
        using var response = await client.GetAsync($"/api/staff/{setup.Other.Id}/services/management?isOwner=true");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ErrorCodes.Forbidden, json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task StaffSelectionIsScopedToTheCaller()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var employee = setup.Client();
        var own = Assert.Single((await employee.GetFromJsonAsync<CatalogStaffDto[]>("/api/offerings/staff"))!);
        Assert.Equal(setup.Staff.Id, own.Id);
        Assert.Equal(setup.Staff.DisplayName, own.DisplayName);
        using var owner = setup.Client("owner");
        var all = (await owner.GetFromJsonAsync<CatalogStaffDto[]>("/api/offerings/staff"))!;
        Assert.Contains(all, s => s.Id == setup.Staff.Id);
        Assert.Contains(all, s => s.Id == setup.Other.Id);
        Assert.Equal(all.OrderBy(s => s.DisplayName, StringComparer.Ordinal).ThenBy(s => s.Id), all);
    }

    [Fact]
    public async Task OwnerCanInspectInactiveStaffButEmployeeLosesManagementAccess()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        using var owner = setup.Client("owner");
        var rows = (await owner.GetFromJsonAsync<ManagedOfferingDto[]>(setup.Url))!;
        Assert.Equal(3, rows.Length);
        Assert.All(rows, r => Assert.False(r.StaffIsActive));
        var staff = (await owner.GetFromJsonAsync<CatalogStaffDto[]>("/api/offerings/staff"))!;
        Assert.False(staff.Single(s => s.Id == setup.Staff.Id).IsActive);
        using var employee = setup.Client();
        foreach (var url in new[] { setup.Url, "/api/offerings/staff" })
        {
            using var response = await employee.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(ErrorCodes.StaffInactive, json.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task EmptyCatalogueAndMissingStaffAreDifferentResults()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var owner = setup.Client("owner");
        Assert.Empty((await owner.GetFromJsonAsync<ManagedOfferingDto[]>(
            $"/api/staff/{setup.Other.Id}/services/management"))!);
        foreach (var id in new[] { Guid.NewGuid(), Guid.Empty })
        {
            using var response = await owner.GetAsync($"/api/staff/{id}/services/management");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(ErrorCodes.StaffNotFound, json.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task ServiceEnforcesAccessWithoutAnHttpAdapter()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var scope = setup.Host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<OfferingManagementService>();
        var forbidden = await Assert.ThrowsAsync<AppException>(() => service.ListAsync(
            new Actor(setup.Customer.Id), setup.Staff.Id, default));
        Assert.Equal(ErrorCodes.Forbidden, forbidden.Code);
        var stale = await Assert.ThrowsAsync<AppException>(() => service.ListStaffAsync(new Actor(Guid.NewGuid()), default));
        Assert.Equal(ErrorCodes.Unauthorized, stale.Code);
        var rows = await service.ListAsync(new Actor(setup.Owner.Id), setup.Staff.Id, default);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task ReadsReflectLatestTermsAndNeverModifyStoredRows()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var client = setup.Client();
        var before = (await client.GetFromJsonAsync<ManagedOfferingDto[]>(setup.Url))!;
        await using (var db = fixture.CreateContext())
        {
            var offering = await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id);
            offering.UpdateTerms(9999999999.99m, 90);
            (await db.Services.SingleAsync(s => s.Id == setup.ActiveService.Id)).Rename("Updated name");
            await db.SaveChangesAsync();
        }
        var after = (await client.GetFromJsonAsync<ManagedOfferingDto[]>(setup.Url))!;
        var updated = after.Single(o => o.Id == setup.Active.Id);
        Assert.Equal(9999999999.99m, updated.Price);
        Assert.Equal(90, updated.DurationMinutes);
        Assert.Equal("Updated name", updated.ServiceName);
        Assert.Equal(before.Single(o => o.Id == setup.Disabled.Id), after.Single(o => o.Id == setup.Disabled.Id));
        await using var verify = fixture.CreateContext();
        Assert.Equal(3, await verify.StaffServices.CountAsync(o => o.StaffMemberId == setup.Staff.Id));
        Assert.Equal(9999999999.99m, (await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).Price);
    }
}
