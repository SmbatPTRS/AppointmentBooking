using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

public sealed class ManagementRetryTests
{
    private const long OwnerId = 9_058_000_001;
    private const long EmployeeId = 9_058_000_002;

    [Theory]
    [InlineData("settings.labels", "First\nEmployee", "Second\nSpecialist", "Second")]
    [InlineData("settings.region", "UTC\nAMD", "Asia/Yerevan\nUSD", "USD")]
    [InlineData("staff.rename", "First", "Second", "Second")]
    [InlineData("profile", "First", "Second", "Second")]
    public async Task PreviousSuccessCannotReplaceNewDeliveryAfterAuthorizationReadFailure(
        string route, string firstInput, string secondInput, string expected)
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        var profile = await Seed(fixture);
        var failures = new Failures();
        await using var bot = CreateBot(fixture, failures);
        var userId = route == "profile" ? EmployeeId : OwnerId;
        if (route == "staff.rename")
        {
            await bot.Send("/staff", userId);
            await bot.Click("Original", userId);
            await bot.Click("Rename", userId);
        }
        else await bot.Send("/" + route, userId);

        await bot.Send(firstInput, userId);
        var second = bot.Message(secondInput, userId);
        failures.FailNextAccess = true;
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(second));
        await bot.Deliver(second);

        await using var db = fixture.CreateContext();
        var actual = route switch
        {
            "settings.labels" => (await db.BusinessSettings.SingleAsync()).DisplayName,
            "settings.region" => (await db.BusinessSettings.SingleAsync()).CurrencyCode,
            _ => (await db.StaffMembers.SingleAsync(staff => staff.Id == profile.Id)).DisplayName
        };
        Assert.Equal(expected, actual);
        Assert.Contains(expected, bot.Sender.SentMenus.Last().Text);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PageRetryAppliesTransitionExactlyOnce(bool previousPage, bool failBeforeTransition)
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        await Seed(fixture, extraStaff: 24);
        var failures = new Failures();
        await using var bot = CreateBot(fixture, failures);
        await bot.Send("/staff", OwnerId);
        if (previousPage) await bot.Click("Next page", OwnerId);
        var originalPage = bot.Sender.SentMenus.Last().Text;
        var update = bot.Callback(bot.Button(previousPage ? "Previous page" : "Next page"), OwnerId);
        if (failBeforeTransition) failures.FailNextAccess = true;
        else bot.Sender.FailNextSend(new IOException("Page reply failed"));

        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(update));
        await bot.Deliver(update);
        Assert.Contains(previousPage ? "page 1 of 3" : "page 2 of 3", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", OwnerId);
        Assert.Equal(originalPage, bot.Sender.SentMenus.Last().Text);
    }

    private static async Task<StaffMember> Seed(PostgresFixture fixture, int extraStaff = 0)
    {
        await using var db = fixture.CreateContext();
        await new DatabaseSeeder(db).SeedBusinessSettingsAsync(new BusinessSettings("Studio", "Employee", "UTC", "AMD"));
        var owner = new User(OwnerId, "Owner", null, DateTime.UtcNow);
        var employee = new User(EmployeeId, "Employee", null, DateTime.UtcNow);
        var profile = new StaffMember(employee.Id, "Original");
        db.AddRange(owner, employee, profile, new BookingCoordination(profile));
        for (var index = 0; index < extraStaff; index++)
        {
            var user = new User(9_058_000_100 + index, "Page employee", null, DateTime.UtcNow);
            var member = new StaffMember(user.Id, $"Employee {index:00}");
            db.AddRange(user, member, new BookingCoordination(member));
        }
        await db.SaveChangesAsync();
        return profile;
    }

    private static TelegramManagementBot CreateBot(PostgresFixture fixture, Failures failures) =>
        new(fixture.ConnectionString, OwnerId, services =>
        {
            var factory = services.Last(descriptor => descriptor.ServiceType == typeof(IAccessService)).ImplementationFactory!;
            services.AddScoped<IAccessService>(provider =>
                new FailingAccess((IAccessService)factory(provider), failures));
        });

    private sealed class Failures
    {
        public bool FailNextAccess;

        public void Check()
        {
            if (!FailNextAccess) return;
            FailNextAccess = false;
            throw new IOException("Transient authorization read failure");
        }
    }

    private sealed class FailingAccess(IAccessService inner, Failures failures) : IAccessService
    {
        public Task<bool> IsOwnerAsync(Actor actor, CancellationToken ct)
        {
            return inner.IsOwnerAsync(actor, ct);
        }

        public Task RequireOwnerAsync(Actor actor, CancellationToken ct)
        {
            failures.Check();
            return inner.RequireOwnerAsync(actor, ct);
        }

        public Task RequireStaffManagementAsync(Actor actor, Guid staffId, CancellationToken ct)
        {
            failures.Check();
            return inner.RequireStaffManagementAsync(actor, staffId, ct);
        }

        public Task RequireAppointmentManagementAsync(Actor actor, Guid staffId, CancellationToken ct) =>
            inner.RequireAppointmentManagementAsync(actor, staffId, ct);
    }
}
