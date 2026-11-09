using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Staff;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.Extensions.Configuration;

namespace AppointmentBooking.IntegrationTests;

public sealed class StaffEndpointsTests
{
    [Fact]
    public async Task StaffEndpointsRespectCallerPermissions()
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();

        var owner = new User(42, "Owner", null, DateTime.UnixEpoch);
        var employee = new User(43, "Employee", null, DateTime.UnixEpoch);
        var client = new User(44, "Client", null, DateTime.UnixEpoch);
        var formerEmployee = new User(45, "Former employee", null, DateTime.UnixEpoch);
        var active = new StaffMember(employee.Id, "Active employee");
        var inactive = new StaffMember(formerEmployee.Id, "Inactive employee");
        inactive.Deactivate();

        await using (var db = fixture.CreateContext())
        {
            db.AddRange(owner, employee, client, formerEmployee, active, inactive);
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(fixture.ConnectionString);
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Owner:TelegramUserId"] = "42",
                    ["ApiAccess:Keys:0:Key"] = "staff-list-owner-key",
                    ["ApiAccess:Keys:0:TelegramUserId"] = "42",
                    ["ApiAccess:Keys:1:Key"] = "staff-list-employee-key",
                    ["ApiAccess:Keys:1:TelegramUserId"] = "43",
                    ["ApiAccess:Keys:2:Key"] = "staff-list-client-key",
                    ["ApiAccess:Keys:2:TelegramUserId"] = "44",
                    ["ApiAccess:Keys:3:Key"] = "staff-inactive-key",
                    ["ApiAccess:Keys:3:TelegramUserId"] = "45"
                })));

        const string route = "/api/staff";
        const string allProfilesRoute = route + "?includeInactive=true";
        var activeItem = new StaffListItem(active.Id, active.DisplayName, true);
        var inactiveItem = new StaffListItem(inactive.Id, inactive.DisplayName, false);

        var create = new CreateStaffRequest(client.Id, "New employee");

        using var anonymous = host.CreateClient();
        using var unauthorized = await anonymous.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var anonymousCreate = await anonymous.PostAsJsonAsync(route, create);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCreate.StatusCode);

        foreach (string key in new[] { "staff-list-employee-key", "staff-list-client-key" })
        {
            using var reader = host.CreateClient();
            reader.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, key);
            Assert.Equal(new[] { activeItem }, await reader.GetFromJsonAsync<StaffListItem[]>(route));

            using var forbidden = await reader.GetAsync(allProfilesRoute);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var forbiddenCreate = await reader.PostAsJsonAsync(route, create);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenCreate.StatusCode);
        }

        using var manager = host.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, "staff-list-owner-key");
        Assert.Equal(new[] { activeItem }, await manager.GetFromJsonAsync<StaffListItem[]>(route));
        Assert.Equal(new[] { activeItem, inactiveItem },
            await manager.GetFromJsonAsync<StaffListItem[]>(allProfilesRoute));

        using var malformed = await manager.PostAsJsonAsync(route, new { create.DisplayName });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        using var invalidName = await manager.PostAsJsonAsync(route, create with { DisplayName = " " });
        await AssertProblemAsync(invalidName, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        using var missingUser = await manager.PostAsJsonAsync(route, create with { UserId = Guid.NewGuid() });
        await AssertProblemAsync(missingUser, HttpStatusCode.NotFound, ErrorCodes.UserNotFound);

        using var created = await manager.PostAsJsonAsync(route, create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var profile = Assert.IsType<StaffProfile>(await created.Content.ReadFromJsonAsync<StaffProfile>());
        Assert.NotEqual(Guid.Empty, profile.Id);
        Assert.Equal(new StaffProfile(profile.Id, client.Id, create.DisplayName, true), profile);

        using var duplicate = await manager.PostAsJsonAsync(route, create);
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, ErrorCodes.StaffAlreadyExists);

        Assert.Equal(new[] { activeItem, new StaffListItem(profile.Id, create.DisplayName, true) },
            await manager.GetFromJsonAsync<StaffListItem[]>(route));

        using var employeeClient = host.CreateClient();
        employeeClient.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, "staff-list-employee-key");
        using var formerEmployeeClient = host.CreateClient();
        formerEmployeeClient.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, "staff-inactive-key");
        using var clientCaller = host.CreateClient();
        clientCaller.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, "staff-list-client-key");
        var rename = new RenameStaffRequest("Renamed employee");
        string activeRoute = $"{route}/{active.Id}";
        string inactiveRoute = $"{route}/{inactive.Id}";

        using var anonymousRename = await anonymous.PatchAsJsonAsync(activeRoute, rename);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRename.StatusCode);

        using var clientRename = await clientCaller.PatchAsJsonAsync(activeRoute, rename);
        await AssertProblemAsync(clientRename, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        using var otherRename = await employeeClient.PatchAsJsonAsync($"{route}/{profile.Id}", rename);
        await AssertProblemAsync(otherRename, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        using var inactiveRename = await formerEmployeeClient.PatchAsJsonAsync(inactiveRoute, rename);
        await AssertProblemAsync(inactiveRename, HttpStatusCode.Forbidden, ErrorCodes.StaffInactive);

        using var invalidRename = await employeeClient.PatchAsJsonAsync(activeRoute, rename with { DisplayName = " " });
        await AssertProblemAsync(invalidRename, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        using var activationPayload = await employeeClient.PatchAsJsonAsync(
            activeRoute, new { rename.DisplayName, IsActive = false });
        Assert.Equal(HttpStatusCode.BadRequest, activationPayload.StatusCode);
        Assert.Contains(activeItem, (await manager.GetFromJsonAsync<StaffListItem[]>(route))!);

        using var renamed = await employeeClient.PatchAsJsonAsync(activeRoute, rename);
        renamed.EnsureSuccessStatusCode();
        Assert.Equal(new StaffProfile(active.Id, employee.Id, rename.DisplayName, true),
            await renamed.Content.ReadFromJsonAsync<StaffProfile>());

        using var ownerRename = await manager.PatchAsJsonAsync(inactiveRoute, rename);
        ownerRename.EnsureSuccessStatusCode();
        Assert.Equal(new StaffProfile(inactive.Id, formerEmployee.Id, rename.DisplayName, false),
            await ownerRename.Content.ReadFromJsonAsync<StaffProfile>());

        var profiles = await manager.GetFromJsonAsync<StaffListItem[]>(allProfilesRoute);
        Assert.NotNull(profiles);
        Assert.Contains(activeItem with { DisplayName = rename.DisplayName }, profiles);
        Assert.Contains(inactiveItem with { DisplayName = rename.DisplayName }, profiles);

        string activationRoute = $"{route}/{profile.Id}/activation";
        var deactivate = new SetStaffActivationRequest(false);

        using var anonymousActivation = await anonymous.PutAsJsonAsync(activationRoute, deactivate);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousActivation.StatusCode);

        using var selfActivation = await clientCaller.PutAsJsonAsync(activationRoute, deactivate);
        await AssertProblemAsync(selfActivation, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        using var missingStatus = await manager.PutAsJsonAsync(activationRoute, new { });
        Assert.Equal(HttpStatusCode.BadRequest, missingStatus.StatusCode);
        var listedProfile = new StaffListItem(profile.Id, profile.DisplayName, true);
        Assert.Contains(listedProfile, (await manager.GetFromJsonAsync<StaffListItem[]>(route))!);

        using var unknownProfile = await manager.PutAsJsonAsync($"{route}/{Guid.NewGuid()}/activation", deactivate);
        await AssertProblemAsync(unknownProfile, HttpStatusCode.NotFound, ErrorCodes.StaffNotFound);

        using var deactivated = await manager.PutAsJsonAsync(activationRoute, deactivate);
        deactivated.EnsureSuccessStatusCode();
        Assert.Equal(profile with { IsActive = false }, await deactivated.Content.ReadFromJsonAsync<StaffProfile>());
        Assert.DoesNotContain(listedProfile, (await manager.GetFromJsonAsync<StaffListItem[]>(route))!);
        Assert.Contains(listedProfile with { IsActive = false },
            (await manager.GetFromJsonAsync<StaffListItem[]>(allProfilesRoute))!);

        using var reactivated = await manager.PutAsJsonAsync(activationRoute, new SetStaffActivationRequest(true));
        reactivated.EnsureSuccessStatusCode();
        Assert.Equal(profile, await reactivated.Content.ReadFromJsonAsync<StaffProfile>());
        Assert.Contains(listedProfile, (await manager.GetFromJsonAsync<StaffListItem[]>(route))!);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }
}
