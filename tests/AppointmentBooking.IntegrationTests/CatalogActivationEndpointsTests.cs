using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogActivationEndpointsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task OwnerCanChangeBothStatusesWithoutCascadingOfferingFlagsOrChangingTerms()
    {
        await using var setup = await CatalogActivationScenario.Create(fixture);
        using var owner = setup.Client("owner");
        using var off = await owner.PutAsJsonAsync(OfferingUrl(setup), new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var offering = (await off.Content.ReadFromJsonAsync<StaffServiceDto>())!;
        Assert.Equal(setup.Active.Id, offering.Id);
        Assert.False(offering.IsActive);
        Assert.Equal(12.50m, offering.Price);
        Assert.Equal(30, offering.DurationMinutes);
        using var hidden = await owner.PutAsJsonAsync(ServiceUrl(setup), new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        Assert.False((await hidden.Content.ReadFromJsonAsync<ServiceDto>())!.IsActive);
        using var visible = await owner.PutAsJsonAsync(ServiceUrl(setup), new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<StaffServiceDto[]>($"/api/staff/{setup.Staff.Id}/services"))!);
        using var active = await owner.PutAsJsonAsync(OfferingUrl(setup), new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        Assert.Equal(setup.Active.Id, Assert.Single((await owner.GetFromJsonAsync<StaffServiceDto[]>($"/api/staff/{setup.Staff.Id}/services"))!).Id);
    }

    [Fact]
    public async Task EmployeeCanManageOwnOfferingButCannotChangeSharedServiceOrAnotherEmployee()
    {
        await using var setup = await CatalogActivationScenario.Create(fixture);
        using var employee = setup.Client();
        using var own = await employee.PutAsJsonAsync(OfferingUrl(setup), new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using var shared = await employee.PutAsJsonAsync(ServiceUrl(setup), new { isActive = false });
        await AssertError(shared, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        using var foreign = await employee.PutAsJsonAsync($"/api/staff/{setup.Other.Id}/services/{setup.ActiveService.Id}/activation", new { isActive = false });
        await AssertError(foreign, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.ActiveService.Id)).IsActive);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("invalid", HttpStatusCode.Unauthorized)]
    [InlineData("customer", HttpStatusCode.Forbidden)]
    public async Task BothRoutesRequireTrustedIdentityAndPermissions(string? key, HttpStatusCode expected)
    {
        await using var setup = await CatalogActivationScenario.Create(fixture);
        using var client = setup.Client(key);
        client.DefaultRequestHeaders.Add("X-User-Id", setup.Owner.Id.ToString());
        foreach (var url in new[] { ServiceUrl(setup), OfferingUrl(setup) })
        {
            using var result = await client.PutAsJsonAsync(url, new { isActive = false });
            Assert.Equal(expected, result.StatusCode);
        }
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.ActiveService.Id)).IsActive);
        Assert.True((await verify.StaffServices.SingleAsync(s => s.Id == setup.Active.Id)).IsActive);
        Assert.Equal(0L, (await verify.BookingCoordinationRows.SingleAsync(r => r.StaffMemberId == setup.Staff.Id)).ChangeVersion);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"isActive\":null}")]
    [InlineData("{\"isActive\":\"false\"}")]
    [InlineData("{\"isActive\":0}")]
    [InlineData("{\"isActive\":false,\"price\":1}")]
    public async Task ExplicitBooleanIsRequiredAndExtraFieldsDoNotSilentlyChangeStatus(string body)
    {
        await using var setup = await CatalogActivationScenario.Create(fixture);
        using var owner = setup.Client("owner");
        foreach (var url in new[] { ServiceUrl(setup), OfferingUrl(setup) })
        {
            using var result = await owner.PutAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.ActiveService.Id)).IsActive);
        Assert.True((await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).IsActive);
        Assert.Equal(0L, (await verify.BookingCoordinationRows.SingleAsync(r => r.StaffMemberId == setup.Staff.Id)).ChangeVersion);
    }

    [Fact]
    public async Task MissingServiceOrOfferingReturnsTheSharedNotFoundError()
    {
        await using var setup = await CatalogActivationScenario.Create(fixture);
        using var owner = setup.Client("owner");
        using var missing = await owner.PutAsJsonAsync($"/api/services/{Guid.NewGuid()}/activation", new { isActive = false });
        await AssertError(missing, HttpStatusCode.NotFound, ErrorCodes.ServiceNotFound);
        using var absent = await owner.PutAsJsonAsync($"/api/staff/{setup.Other.Id}/services/{setup.ActiveService.Id}/activation", new { isActive = false });
        await AssertError(absent, HttpStatusCode.NotFound, ErrorCodes.StaffServiceNotFound);
    }

    private static string ServiceUrl(OfferingOverviewScenario setup) => $"/api/services/{setup.ActiveService.Id}/activation";
    private static string OfferingUrl(OfferingOverviewScenario setup) => $"/api/staff/{setup.Staff.Id}/services/{setup.ActiveService.Id}/activation";
    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        Assert.Equal(expected, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
}
