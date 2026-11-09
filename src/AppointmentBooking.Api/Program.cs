using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Infrastructure.Repositories;
using Microsoft.Extensions.Options;
using AppointmentBooking.Infrastructure;
using AppointmentBooking.Infrastructure.Persistence;

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
builder.Services.AddOptions<BookingOptions>().BindConfiguration("Booking").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddScoped<IStaffRepository, StaffRepository>();
builder.Services.AddScoped<IBusinessSettingsRepository, BusinessSettingsRepository>();
builder.Services.AddScoped(provider => new AvailabilityCalculator(provider.GetRequiredService<IOptions<BookingOptions>>().Value));
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
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

// Add authentication/authorization and their middleware in Task 03,
// before exposing any protected feature controllers.
app.MapControllers();
app.Run();

public partial class Program { }
