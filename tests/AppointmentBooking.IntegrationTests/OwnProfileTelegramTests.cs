using AppointmentBooking.Api.Telegram.Handlers;
using AppointmentBooking.Api.Telegram.State;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class OwnProfileTelegramTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EmployeeRenamesOnlyOwnActiveProfileAndRecoversFromInvalidInputAndReplyFailure()
    {
        const long ownerId = 9_057_000_001;
        const long employeeId = 9_057_000_002;
        const long clientId = 9_057_000_003;
        const long inactiveId = 9_057_000_004;
        Guid staffId;
        Guid inactiveStaffId;
        await using (var db = fixture.CreateContext())
        {
            var employee = new User(employeeId, "Employee", null, DateTime.UtcNow);
            var inactive = new User(inactiveId, "Inactive employee", null, DateTime.UtcNow);
            var profile = new StaffMember(employee.Id, "Original own name");
            var inactiveProfile = new StaffMember(inactive.Id, "Inactive name");
            inactiveProfile.Deactivate();
            staffId = profile.Id;
            inactiveStaffId = inactiveProfile.Id;
            db.AddRange(employee, inactive, profile, inactiveProfile);
            await db.SaveChangesAsync();
        }
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, ownerId);
        await using (var scope = bot.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<StaffManagementService>();
            var stale = await Assert.ThrowsAsync<AppException>(() => service.GetOwnProfileAsync(new Actor(Guid.NewGuid())));
            Assert.Equal(ErrorCodes.Unauthorized, stale.Code);
        }
        await bot.Send("/start", employeeId);
        await bot.Click("Display name", employeeId);
        Assert.Contains("Original own name", bot.Sender.SentMenus.Last().Text);
        foreach (var input in new[] { "  ", new string('x', 201) })
        {
            await bot.Send(input, employeeId);
            Assert.Contains("Type another name", bot.Sender.SentMenus.Last().Text);
        }
        await bot.Click("Back", employeeId);
        Assert.Contains("Welcome", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Display name", employeeId);
        var save = bot.Message("  My new name  ", employeeId);
        bot.Sender.FailNextSend(new HttpRequestException("Rename reply failed"));
        await Assert.ThrowsAsync<HttpRequestException>(() => bot.Deliver(save));
        await using (var db = fixture.CreateContext())
        {
            var profile = await db.StaffMembers.SingleAsync(profile => profile.Id == staffId);
            Assert.Equal("My new name", profile.DisplayName);
            profile.Rename("Later own name");
            await db.SaveChangesAsync();
        }
        await bot.Deliver(save);
        Assert.Contains("updated to My new name", bot.Sender.SentMenus.Last().Text);
        await bot.Send(" ", employeeId);
        Assert.Contains("Type another name", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Cancel", employeeId);
        Assert.Contains("Welcome", bot.Sender.SentMenus.Last().Text);
        // Even a corrupt selection cannot redirect self-service to another employee.
        await bot.Click("Display name", employeeId);
        using (var lease = await bot.Services.GetRequiredService<ConversationStateStore>().AcquireAsync(new(employeeId, employeeId)))
            lease.State.SetFeatureData("profile", new OwnProfileDraft(inactiveStaffId));
        await bot.Send("Wrong target", employeeId);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal("Later own name", (await db.StaffMembers.SingleAsync(profile => profile.Id == staffId)).DisplayName);
            Assert.Equal("Inactive name", (await db.StaffMembers.SingleAsync(profile => profile.Id == inactiveStaffId)).DisplayName);
        }
        await bot.Send("/profile", employeeId);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(profile => profile.Id == staffId)).Deactivate();
            await db.SaveChangesAsync();
        }
        await bot.Send("After deactivation", employeeId);
        Assert.Contains("active employee profile is required", bot.Sender.SentMenus.Last().Text);
        foreach (var userId in new[] { clientId, inactiveId, employeeId })
        {
            await bot.Send("/start", userId);
            Assert.DoesNotContain(bot.Sender.SentMenus.Last().Buttons, button => button.Text == "Display name");
            await bot.Send("/profile", userId);
            Assert.Contains("active employee profile is required", bot.Sender.SentMenus.Last().Text);
        }
        await using (var db = fixture.CreateContext())
            Assert.Equal("Later own name", (await db.StaffMembers.SingleAsync(profile => profile.Id == staffId)).DisplayName);
    }
}
