using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class OfferingTermsEndpointsTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("owner")]
    [InlineData("employee")]
    public async Task CreateReturnsPersistedOfferingAndAReadableManagementLocation(string key)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var service = await NewService();
        using var client = setup.Client(key);
        using var created = await client.PostAsJsonAsync(Collection(setup),
            new { serviceId = service.Id, price = 0m, durationMinutes = 20 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = (await created.Content.ReadFromJsonAsync<StaffServiceDto>())!;
        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal(setup.Staff.Id, dto.StaffMemberId);
        Assert.Equal(service.Id, dto.ServiceId);
        Assert.Equal(service.Name, dto.ServiceName);
        Assert.Equal(0m, dto.Price);
        Assert.Equal(20, dto.DurationMinutes);
        Assert.True(dto.IsActive);
        Assert.NotNull(created.Headers.Location);
        var management = (await client.GetFromJsonAsync<ManagedOfferingDto[]>(created.Headers.Location))!;
        Assert.Equal(dto.Id, management.Single(o => o.ServiceId == service.Id).Id);
        var visible = (await client.GetFromJsonAsync<StaffServiceDto[]>(Collection(setup)))!;
        Assert.Equal(dto, visible.Single(o => o.Id == dto.Id));
        await using var db = fixture.CreateContext();
        var saved = await db.StaffServices.SingleAsync(o => o.Id == dto.Id);
        Assert.Equal(0m, saved.Price);
        Assert.Equal(20, saved.DurationMinutes);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("employee")]
    public async Task UpdateChangesBothTermsAndRepeatedPutKeepsTheSameOfferingId(string key)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var client = setup.Client(key);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.PutAsJsonAsync(Terms(setup), new { price = 5.2500m, durationMinutes = 55 });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dto = (await response.Content.ReadFromJsonAsync<StaffServiceDto>())!;
            Assert.Equal(setup.Active.Id, dto.Id);
            Assert.Equal(setup.ActiveService.Id, dto.ServiceId);
            Assert.Equal(5.25m, dto.Price);
            Assert.Equal(55, dto.DurationMinutes);
        }
        using var customer = setup.Client("customer");
        var catalogue = (await customer.GetFromJsonAsync<StaffServiceDto[]>(Collection(setup)))!;
        Assert.Equal(5.25m, Assert.Single(catalogue).Price);
        Assert.Equal(55, catalogue[0].DurationMinutes);
        await using var db = fixture.CreateContext();
        Assert.Equal(3, await db.StaffServices.CountAsync(o => o.StaffMemberId == setup.Staff.Id));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("invalid", HttpStatusCode.Unauthorized)]
    [InlineData("customer", HttpStatusCode.Forbidden)]
    public async Task BothMutationRoutesRequireTrustedManagementAccess(string? key, HttpStatusCode expected)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var service = await NewService();
        using var client = setup.Client(key);
        client.DefaultRequestHeaders.Add("X-User-Id", setup.Owner.Id.ToString());
        using var created = await client.PostAsJsonAsync(Collection(setup),
            new { serviceId = service.Id, price = 45m, durationMinutes = 45 });
        using var changed = await client.PutAsJsonAsync(Terms(setup), new { price = 45m, durationMinutes = 45 });
        Assert.Equal(expected, created.StatusCode);
        Assert.Equal(expected, changed.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.False(await db.StaffServices.AnyAsync(o => o.ServiceId == service.Id));
        Assert.Equal(12.50m, (await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).Price);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"durationMinutes\":30}")]
    [InlineData("{\"price\":0}")]
    [InlineData("{\"price\":null,\"durationMinutes\":30}")]
    [InlineData("{\"price\":10,\"durationMinutes\":null}")]
    [InlineData("{\"price\":10,\"durationMinutes\":1.5}")]
    [InlineData("{\"price\":10,\"durationMinutes\":30,\"isActive\":false}")]
    [InlineData("{\"price\":10,\"durationMinutes\":30,\"serviceId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"price\":10,\"durationMinutes\":30,\"staffMemberId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{")]
    public async Task IncompleteOrUnexpectedUpdateFieldsDoNotSilentlyDefaultOrChangeOtherProperties(string body)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var client = setup.Client();
        using var response = await client.PutAsync(Terms(setup), Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = fixture.CreateContext();
        var saved = await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id);
        Assert.Equal(12.50m, saved.Price);
        Assert.Equal(30, saved.DurationMinutes);
        Assert.True(saved.IsActive);
        Assert.Equal(setup.Staff.Id, saved.StaffMemberId);
        Assert.Equal(setup.ActiveService.Id, saved.ServiceId);
    }

    [Theory]
    [InlineData("{\"price\":0,\"durationMinutes\":30}")]
    [InlineData("{\"serviceId\":\"00000000-0000-0000-0000-000000000000\",\"price\":0,\"durationMinutes\":30}")]
    [InlineData("{\"serviceId\":\"not-an-id\",\"price\":0,\"durationMinutes\":30}")]
    public async Task CreateRequiresAnExplicitNonemptySharedServiceId(string body)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var client = setup.Client();
        using var response = await client.PostAsync(Collection(setup), Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Equal(3, await db.StaffServices.CountAsync(o => o.StaffMemberId == setup.Staff.Id));
    }

    [Theory]
    [InlineData("price")]
    [InlineData("durationMinutes")]
    [InlineData("isActive")]
    public async Task CreateRequiresBothTermsAndRejectsActivationInput(string field)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var service = await NewService();
        using var client = setup.Client();
        var body = new Dictionary<string, object>
            { ["serviceId"] = service.Id, ["price"] = 0m, ["durationMinutes"] = 30 };
        if (field == "isActive") body[field] = false;
        else body.Remove(field);
        using var response = await client.PostAsJsonAsync(Collection(setup), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.False(await db.StaffServices.AnyAsync(o => o.ServiceId == service.Id));
    }

    [Theory]
    [InlineData("-0.01", 30)]
    [InlineData("1.001", 30)]
    [InlineData("10000000000", 30)]
    [InlineData("10", 0)]
    public async Task InvalidNumericValuesUseApplicationValidationForBothRoutes(string price, int duration)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var service = await NewService();
        using var client = setup.Client();
        using var create = await client.PostAsync(Collection(setup), Json(
            $"{{\"serviceId\":\"{service.Id}\",\"price\":{price},\"durationMinutes\":{duration}}}"));
        using var update = await client.PutAsync(Terms(setup), Json($"{{\"price\":{price},\"durationMinutes\":{duration}}}"));
        await AssertError(create, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        await AssertError(update, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        await using var db = fixture.CreateContext();
        Assert.False(await db.StaffServices.AnyAsync(o => o.ServiceId == service.Id));
        Assert.Equal(30, (await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).DurationMinutes);
    }

    [Fact]
    public async Task RepeatedPostRejectsDuplicatePairAndLeavesOriginalTermsIntact()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var service = await NewService();
        using var client = setup.Client();
        using var first = await client.PostAsJsonAsync(Collection(setup), new { serviceId = service.Id, price = 10m, durationMinutes = 20 });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = (await first.Content.ReadFromJsonAsync<StaffServiceDto>())!;
        using var duplicate = await client.PostAsJsonAsync(Collection(setup), new { serviceId = service.Id, price = 99m, durationMinutes = 90 });
        await AssertError(duplicate, HttpStatusCode.Conflict, OfferingTermsService.DuplicateOfferingCode);
        await using var db = fixture.CreateContext();
        var saved = Assert.Single(await db.StaffServices.Where(o => o.StaffMemberId == setup.Staff.Id && o.ServiceId == service.Id).ToListAsync());
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(10m, saved.Price);
        Assert.Equal(20, saved.DurationMinutes);
    }

    [Fact]
    public async Task EmployeeCannotRedirectWritesToAnotherEmployee()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var client = setup.Client();
        var collection = $"/api/staff/{setup.Other.Id}/services";
        using var create = await client.PostAsJsonAsync(collection,
            new { serviceId = setup.ActiveService.Id, price = 10m, durationMinutes = 30 });
        using var update = await client.PutAsJsonAsync($"{collection}/{setup.ActiveService.Id}/terms", new { price = 10m, durationMinutes = 30 });
        await AssertError(create, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await AssertError(update, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await using var db = fixture.CreateContext();
        Assert.False(await db.StaffServices.AnyAsync(o => o.StaffMemberId == setup.Other.Id));
    }

    [Fact]
    public async Task MissingOfferingReturnsNotFoundAndDoesNotCreateOrChangeAnotherEmployeesRow()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var owner = setup.Client("owner");
        using var update = await owner.PutAsJsonAsync($"/api/staff/{setup.Other.Id}/services/{setup.ActiveService.Id}/terms",
            new { price = 10m, durationMinutes = 30 });
        await AssertError(update, HttpStatusCode.NotFound, ErrorCodes.StaffServiceNotFound);
        using var missing = await owner.PostAsJsonAsync(Collection(setup),
            new { serviceId = Guid.NewGuid(), price = 10m, durationMinutes = 30 });
        await AssertError(missing, HttpStatusCode.NotFound, ErrorCodes.ServiceNotFound);
        await using var db = fixture.CreateContext();
        Assert.False(await db.StaffServices.AnyAsync(o => o.StaffMemberId == setup.Other.Id));
        Assert.Equal(12.50m, (await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).Price);
    }

    [Fact]
    public async Task UpdatingDisabledOfferingLeavesItHiddenAndAllowsMaximumPrice()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var client = setup.Client();
        using var response = await client.PutAsJsonAsync($"{Collection(setup)}/{setup.Disabled.ServiceId}/terms",
            new { price = 9999999999.99m, durationMinutes = 120 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<StaffServiceDto>())!;
        Assert.Equal(setup.Disabled.Id, result.Id);
        Assert.False(result.IsActive);
        Assert.Equal(9999999999.99m, result.Price);
        Assert.Equal(120, result.DurationMinutes);
        using var customer = setup.Client("customer");
        Assert.DoesNotContain((await customer.GetFromJsonAsync<StaffServiceDto[]>(Collection(setup)))!, o => o.Id == result.Id);
    }

    private static string Collection(OfferingOverviewScenario setup) => $"/api/staff/{setup.Staff.Id}/services";
    private static string Terms(OfferingOverviewScenario setup) => $"{Collection(setup)}/{setup.ActiveService.Id}/terms";
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        Assert.Equal(expected, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
    private async Task<Service> NewService()
    {
        await using var db = fixture.CreateContext();
        var service = new Service("HTTP terms service " + Guid.NewGuid());
        db.Services.Add(service);
        await db.SaveChangesAsync();
        return service;
    }
}
