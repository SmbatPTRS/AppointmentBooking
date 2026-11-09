using System.Net.Http.Json;
using Swashbuckle.AspNetCore.Swagger;
using Microsoft.OpenApi.Models;
using System.Text.Json;
using AppointmentBooking.Application.Common;
using System.Net;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

public sealed class StaffAndAccessTests
{
    [Fact]
    public async Task ApiKeyIdentityAndHttpErrorsUseTheProductionPipeline()
    {
        var user = new User(42, "Test caller", null, DateTime.UnixEpoch);
        var owner = new User(43, "Test owner", null, DateTime.UnixEpoch);
        await using var factory = new ApiFactory("Host=localhost;Database=not_opened;Username=test;Password=test");
        await using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ApiAccess:Keys:0:Key"] = "fixture-key",
                    ["ApiAccess:Keys:0:TelegramUserId"] = "42",
                    ["ApiAccess:Keys:1:Key"] = "fixture-owner-key",
                    ["ApiAccess:Keys:1:TelegramUserId"] = "43",
                    ["Owner:TelegramUserId"] = "43"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IUserRepository>(new UserLookup(user, owner));
                services.AddControllers().AddApplicationPart(typeof(ApiKeyProbeController).Assembly);
            });
        });
        using var client = configured.CreateClient();
        var target = owner.Id;
        string path = $"/test/api-key/{target}?userId={target}";

        using var missing = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, "wrong-key");
        using var invalid = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);

        client.DefaultRequestHeaders.Remove(ApiKeyAuthenticationHandler.HeaderName);
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, "fixture-key");
        using var valid = await client.GetAsync(path);
        valid.EnsureSuccessStatusCode();
        Assert.Equal(user.Id.ToString(), await valid.Content.ReadAsStringAsync());

        using var denied = await client.PostAsJsonAsync(
            $"/test/api-key/{target}/owner", new { UserId = owner.Id });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("application/problem+json", denied.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal(ErrorCodes.Forbidden, problem.RootElement.GetProperty("code").GetString());

        using var failed = await client.GetAsync($"/test/api-key/{target}/failure");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain("fixture failure detail", await failed.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Remove(ApiKeyAuthenticationHandler.HeaderName);
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, "fixture-owner-key");
        using var ownerIdentity = await client.GetAsync(path);
        ownerIdentity.EnsureSuccessStatusCode();
        Assert.Equal(owner.Id.ToString(), await ownerIdentity.Content.ReadAsStringAsync());

        using var allowed = await client.PostAsJsonAsync(
            $"/test/api-key/{target}/owner", new { UserId = user.Id });
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task SwaggerDocumentsApiKeyOnlyOnProtectedOperations()
    {
        await using var factory = new ApiFactory("Host=localhost;Database=not_opened;Username=test;Password=test");
        await using var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddControllers().AddApplicationPart(typeof(ApiKeyProbeController).Assembly)));

        using var scope = configured.Services.CreateScope();
        var swagger = scope.ServiceProvider.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var scheme = swagger.Components.SecuritySchemes[ApiKeyAuthenticationHandler.SchemeName];
        Assert.Equal(SecuritySchemeType.ApiKey, scheme.Type);
        Assert.Equal(ParameterLocation.Header, scheme.In);
        Assert.Equal(ApiKeyAuthenticationHandler.HeaderName, scheme.Name);

        var protectedGet = swagger.Paths["/test/api-key/{target}"].Operations[OperationType.Get];
        var requiredScheme = Assert.Single(Assert.Single(protectedGet.Security).Keys);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, requiredScheme.Reference.Id);
        var task04Operations = new (string Path, OperationType Method)[]
        {
            ("/api/business-settings", OperationType.Get),
            ("/api/business-settings", OperationType.Put),
            ("/api/business-settings/time-zone-and-currency", OperationType.Put),
            ("/api/staff", OperationType.Get),
            ("/api/staff", OperationType.Post),
            ("/api/staff/{staffId}", OperationType.Patch),
            ("/api/staff/{staffId}/activation", OperationType.Put)
        };
        foreach (var (path, method) in task04Operations)
        {
            var operation = swagger.Paths[path].Operations[method];
            var operationScheme = Assert.Single(Assert.Single(operation.Security).Keys);
            Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, operationScheme.Reference.Id);
        }

        Assert.Empty(swagger.Paths["/health"].Operations[OperationType.Get].Security);
    }

    private sealed class UserLookup(params User[] users) : IUserRepository
    {
        public Task<User?> GetByTelegramUserIdAsync(long id, CancellationToken ct) =>
            Task.FromResult(users.SingleOrDefault(user => user.TelegramUserId == id));

        public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(users.SingleOrDefault(user => user.Id == id));
        public Task<User> InsertOrGetAsync(User candidate, CancellationToken ct) => throw new NotSupportedException();
        public void Add(User candidate) => throw new NotSupportedException();
    }
}

[Authorize(Policy = ApiKeyAuthenticationHandler.SchemeName)]
[Route("test/api-key/{target:guid}")]
public sealed class ApiKeyProbeController(HttpActorResolver actors, IAccessService access) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Content(actor.UserId.ToString());
    }

    [HttpPost("owner")]
    public async Task<IActionResult> Owner([FromBody] OwnerProbeRequest request, CancellationToken ct)
    {
        if (request.UserId == Guid.Empty)
        {
            return BadRequest();
        }

        await access.RequireOwnerAsync(await actors.ResolveAsync(HttpContext), ct);
        return NoContent();
    }

    [HttpGet("failure")]
    public IActionResult Failure() => throw new InvalidOperationException("fixture failure detail");
}

public sealed record OwnerProbeRequest(Guid UserId);
