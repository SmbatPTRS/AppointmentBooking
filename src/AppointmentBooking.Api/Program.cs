using AppointmentBooking.Api.Swagger;
using Microsoft.OpenApi.Models;
using AppointmentBooking.Api.Middleware;
using AppointmentBooking.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using AppointmentBooking.Api.Configuration;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Identity;
using AppointmentBooking.Infrastructure.Repositories;
using Microsoft.Extensions.Options;
using AppointmentBooking.Infrastructure;
using AppointmentBooking.Infrastructure.Persistence;

// Protected endpoints opt into the API-key policy. Keep it off unrelated routes.
AppContext.SetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", true);

var builder = WebApplication.CreateBuilder(args);

// Local development only; a deployed container receives environment variables.
var envFile = Path.Combine(builder.Environment.ContentRootPath, ".env");
if (builder.Environment.IsDevelopment() && File.Exists(envFile))
{
    DotNetEnv.Env.NoClobber().Load(envFile);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddTelegramInfrastructure();
builder.Services.AddOptions<OwnerOptions>()
    .BindConfiguration(OwnerOptions.SectionName)
    .Validate(options => options.TelegramUserId >= 0, "Owner:TelegramUserId must be zero or a positive Telegram user ID.")
    .ValidateOnStart();
builder.Services.AddScoped<IAccessService>(provider => new AccessService(
    provider.GetRequiredService<IUserRepository>(),
    provider.GetRequiredService<IStaffRepository>(),
    provider.GetRequiredService<IOptions<OwnerOptions>>().Value.TelegramUserId));
builder.Services.AddOptions<BookingOptions>().BindConfiguration("Booking").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddScoped<IStaffRepository, StaffRepository>();
builder.Services.AddScoped<IBusinessSettingsRepository, BusinessSettingsRepository>();
builder.Services.AddScoped(provider => new AvailabilityCalculator(provider.GetRequiredService<IOptions<BookingOptions>>().Value));
builder.Services.AddScoped<AvailabilityService>();
// Member B: schedule operations share the existing scoped repositories and lock.
builder.Services.AddScoped<AppointmentBooking.Application.Scheduling.ScheduleChangeValidator>();
builder.Services.AddScoped<AppointmentBooking.Application.Scheduling.ScheduleService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddOptions<ApiAccessOptions>()
    .BindConfiguration(ApiAccessOptions.SectionName)
    .Validate(options => options.Keys.All(entry =>
        entry.TelegramUserId > 0 && !string.IsNullOrWhiteSpace(entry.Key) && !entry.Key.Contains(',')),
        "Each API key must be nonblank, contain no comma, and identify a positive Telegram user ID.")
    .Validate(options => options.Keys.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() == options.Keys.Length,
        "Configured API keys must be unique.")
    .ValidateOnStart();
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ApiKeyAuthenticationHandler.SchemeName, policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());
builder.Services.AddScoped<HttpActorResolver>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition(ApiKeyAuthenticationHandler.SchemeName, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = ApiKeyAuthenticationHandler.HeaderName,
        Description = "Enter the API key for your registered user."
    });
    options.OperationFilter<ApiKeyOperationFilter>();
});

var app = builder.Build();
app.UseMiddleware<ExceptionHandlingMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/db", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? (IResult)Results.Ok(new { status = "database reachable" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program { }
