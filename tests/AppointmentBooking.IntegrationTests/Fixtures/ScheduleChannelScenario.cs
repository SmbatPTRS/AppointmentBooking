using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using User = AppointmentBooking.Domain.Entities.User;

namespace AppointmentBooking.IntegrationTests.Fixtures;

// Used only by the serialized PostgreSQL collection. Restore business settings on disposal.
public sealed class ScheduleChannelScenario : IAsyncDisposable
{
    private readonly PostgresFixture fixture;
    private readonly ApiFactory factory;
    private readonly WebApplicationFactory<Program> app;
    private readonly SettingsSnapshot original;
    private int updateId;
    public User Employee { get; }
    public User Customer { get; }
    public StaffMember Staff { get; }
    public StaffService Offering { get; }
    public FakeTelegramClient Sender { get; } = new();
    public DateOnly Date { get; } = new(2026, 11, 10);
    public TimeZoneInfo Zone { get; }
    public string ScheduleUrl => $"/api/staff/{Staff.Id}/schedule";
    public string DaysUrl => $"/api/staff/{Staff.Id}/days-off";
    public string AvailabilityUrl => AvailabilityUrlFor(Date);
    public string AvailabilityUrlFor(DateOnly date) =>
        $"/api/availability?staffId={Staff.Id}&staffServiceId={Offering.Id}&date={date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}";
    public string LastMenu => Sender.SentMenus.Last().Text;

    private sealed record SettingsSnapshot(string Name, string Label, string Zone, string Currency);
    private sealed class Clock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
    }

    private ScheduleChannelScenario(PostgresFixture fixture, SettingsSnapshot original,
        User employee, User customer, StaffMember staff, StaffService offering, string zone)
    {
        this.fixture = fixture;
        this.original = original;
        Employee = employee;
        Customer = customer;
        Staff = staff;
        Offering = offering;
        Zone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        factory = new ApiFactory(fixture.ConnectionString);
        app = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ApiAccess:Keys:0:Key"] = "channel-employee",
                    ["ApiAccess:Keys:0:TelegramUserId"] = Employee.TelegramUserId.ToString(),
                    ["ApiAccess:Keys:1:Key"] = "channel-customer",
                    ["ApiAccess:Keys:1:TelegramUserId"] = Customer.TelegramUserId.ToString(),
                    ["Booking:SlotStepMinutes"] = "15",
                    ["Booking:MaxAdvanceDays"] = "30"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IClock>(new Clock());
                services.AddSingleton<ITelegramMessageSender>(Sender);
            });
        });
    }

    public static async Task<ScheduleChannelScenario> Create(PostgresFixture fixture,
        string zone = "Asia/Yerevan", int duration = 30)
    {
        await using var db = fixture.CreateContext();
        var clock = new Clock();
        User NewUser(string name) => new(Random.Shared.NextInt64(10_000_000_000, 80_000_000_000), name, null, clock.UtcNow);
        var employee = NewUser("Channel employee");
        var customer = NewUser("Channel customer");
        var staff = new StaffMember(employee.Id, "Channel staff");
        var service = new Service("Channel service");
        var offering = new StaffService(staff.Id, service.Id, 20m, duration);
        var settings = await db.BusinessSettings.SingleOrDefaultAsync();
        if (settings is null)
            db.Add(settings = new BusinessSettings("Studio", "Staff", "Asia/Yerevan", "AMD"));
        var original = new SettingsSnapshot(settings.DisplayName, settings.StaffLabel, settings.TimeZoneId, settings.CurrencyCode);
        if (!await db.BookingCoordinationRows.AnyAsync(r => r.BusinessSettingsId == settings.Id))
            db.Add(new BookingCoordination(settings));
        db.AddRange(employee, customer, staff, service, offering, new BookingCoordination(staff));
        // Fixture arrangement only; this does not exercise A's settings mutation workflow.
        settings.Update(settings.DisplayName, settings.StaffLabel, zone, settings.CurrencyCode);
        await db.SaveChangesAsync();
        return new(fixture, original, employee, customer, staff, offering, zone);
    }

    public HttpClient Client(bool employee = false)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName,
            employee ? "channel-employee" : "channel-customer");
        return client;
    }

    public Update Message(string text) => new()
    {
        Id = ++updateId,
        Message = new Message
        {
            From = new Telegram.Bot.Types.User { Id = Employee.TelegramUserId, FirstName = "Employee" },
            Chat = new Chat { Id = Employee.TelegramUserId }, Text = text
        }
    };

    public Task Send(string text) => Deliver(Message(text));
    public Task Click(string label) => Callback(Sender.SentMenus.Last().Buttons.Single(b => b.Text == label).CallbackData);
    public Task Callback(string data) => Deliver(CallbackUpdate(data));
    public Update CallbackUpdate(string data) => new Update
    {
        Id = ++updateId,
        CallbackQuery = new CallbackQuery
        {
            Id = Guid.NewGuid().ToString(), ChatInstance = "schedule-channel",
            Data = data,
            From = new Telegram.Bot.Types.User { Id = Employee.TelegramUserId, FirstName = "Employee" },
            Message = new Message { Chat = new Chat { Id = Employee.TelegramUserId } }
        }
    };

    public async Task Deliver(Update update)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>().DispatchAsync(update);
    }

    public DateTimeOffset At(TimeOnly local) => new(TimeZoneInfo.ConvertTimeToUtc(
        Date.ToDateTime(local, DateTimeKind.Unspecified), Zone));

    public async ValueTask DisposeAsync()
    {
        try { await app.DisposeAsync(); }
        finally
        {
            try { await factory.DisposeAsync(); }
            finally
            {
                await using var db = fixture.CreateContext();
                var settings = await db.BusinessSettings.SingleAsync();
                settings.Update(original.Name, original.Label, original.Zone, original.Currency);
                await db.SaveChangesAsync();
            }
        }
    }
}
