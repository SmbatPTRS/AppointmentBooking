using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Api.Telegram.State;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using AppUser = AppointmentBooking.Domain.Entities.User;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class StaffManagementTelegramTests(PostgresFixture fixture)
{
    private static bool HasRoute(TelegramMenuButton button, string route) =>
        CallbackDataParser.TryParse(button.CallbackData, out var callback) && callback!.Route == route;

    [Fact]
    public async Task OwnerListsAllStaffWhileOtherRolesAreDeniedAndNavigationRemainsUsable()
    {
        const long ownerId = 9_050_000_001;
        const long employeeId = 9_050_000_002;
        const long clientId = 9_050_000_003;
        await using (var db = fixture.CreateContext())
        {
            var employee = new AppUser(employeeId, "Management employee", null, DateTime.UtcNow);
            var inactiveUser = new AppUser(9_050_000_004, "Inactive employee", null, DateTime.UtcNow);
            var active = new StaffMember(employee.Id, "Task05 active employee");
            var inactive = new StaffMember(inactiveUser.Id, "Task05 inactive employee");
            inactive.Deactivate();
            db.Users.AddRange(employee, inactiveUser);
            db.StaffMembers.AddRange(active, inactive);
            for (var index = 0; index < 11; index++)
            {
                var user = new AppUser(9_050_000_100 + index, "List employee", null, DateTime.UtcNow);
                db.Users.Add(user);
                db.StaffMembers.Add(new StaffMember(user.Id, $"Task05 employee {index}"));
            }
            // Exercise names that can also appear on navigation buttons.
            var collisionNames = new[] { "Cancel", "Back", "Next page", "Previous page", "Add employee" };
            for (var index = 0; index < collisionNames.Length; index++)
            {
                var user = new AppUser(9_050_000_200 + index, "Navigation test account", null, DateTime.UtcNow);
                db.Users.Add(user);
                db.StaffMembers.Add(new StaffMember(user.Id, collisionNames[index]));
            }
            await db.SaveChangesAsync();
        }

        var sender = new FakeTelegramClient();
        await using var factory = new ApiFactory(fixture.ConnectionString);
        await using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Owner:TelegramUserId"] = ownerId.ToString() }));
            builder.ConfigureTestServices(services => services.AddSingleton<ITelegramMessageSender>(sender));
        });
        var updateId = 0;
        async Task Dispatch(Update update)
        {
            update.Id = ++updateId;
            await using var scope = configured.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>().DispatchAsync(update);
        }
        Update Click(string data, long userId = ownerId) => new()
        {
            CallbackQuery = new CallbackQuery
            {
                Id = Guid.NewGuid().ToString("N"), Data = data, ChatInstance = "test",
                From = new Telegram.Bot.Types.User { Id = userId, FirstName = "Caller" },
                Message = new Message { Chat = new Chat { Id = ownerId, Type = ChatType.Private } }
            }
        };

        await Dispatch(TelegramFoundationTests.Message("/start", ownerId, ownerId));
        var open = sender.SentMenus.Last().Buttons.Single(button => button.Text == "Staff management");
        var listStart = sender.SentMenus.Count;
        await Dispatch(Click(open.CallbackData));
        while (sender.SentMenus.Last().Buttons.Any(button => button.Text == "Next page" && HasRoute(button, "staff")))
            await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => button.Text == "Next page" && HasRoute(button, "staff")).CallbackData));
        var lists = sender.SentMenus.Skip(listStart).ToArray();
        Assert.True(lists.Length >= 2);
        var text = string.Join("\n", lists.Select(menu => menu.Text));
        Assert.Contains("Task05 active employee — Active", text);
        Assert.Contains("Task05 inactive employee — Inactive", text);
        Assert.All(lists, menu => Assert.True(menu.Text.Length < 4096));

        await Dispatch(TelegramFoundationTests.Message("/staff", ownerId, ownerId));
        while (!sender.SentMenus.Last().Buttons.Any(button => button.Text == "Task05 inactive employee"))
            await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => button.Text == "Next page" && HasRoute(button, "staff")).CallbackData));
        var lastPageText = sender.SentMenus.Last().Text;
        var employeeButton = sender.SentMenus.Last().Buttons.Single(button => button.Text == "Task05 inactive employee");
        var menuCountBeforeForgery = sender.SentMenus.Count;
        await Dispatch(Click(employeeButton.CallbackData, employeeId));
        Assert.Equal(menuCountBeforeForgery, sender.SentMenus.Count);
        Assert.Contains("expired", sender.CallbackAnswers.Last().Text);
        Assert.True(CallbackDataParser.TryParse(employeeButton.CallbackData, out var selectedCallback));
        await Dispatch(Click(CallbackDataParser.Format(selectedCallback!.Route, selectedCallback.Revision)));
        Assert.Equal(menuCountBeforeForgery, sender.SentMenus.Count);
        Assert.Contains("expired", sender.CallbackAnswers.Last().Text);

        var detailsUpdate = Click(employeeButton.CallbackData);
        detailsUpdate.Id = ++updateId;
        sender.FailNextSend(new HttpRequestException("Temporary details failure"));
        await using (var scope = configured.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>();
            await Assert.ThrowsAsync<HttpRequestException>(() => dispatcher.DispatchAsync(detailsUpdate));
            await dispatcher.DispatchAsync(detailsUpdate);
        }
        Assert.Contains("Name — Task05 inactive employee", sender.SentMenus.Last().Text);
        Assert.Contains("Status — Inactive", sender.SentMenus.Last().Text);
        await Dispatch(TelegramFoundationTests.Message("unexpected input", ownerId, ownerId));
        Assert.Contains("Task05 inactive employee", sender.SentMenus.Last().Text);
        await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => HasRoute(button, "back")).CallbackData));
        Assert.Equal(lastPageText, sender.SentMenus.Last().Text);
        var beforeOldSelection = sender.SentMenus.Count;
        await Dispatch(Click(employeeButton.CallbackData));
        Assert.Equal(beforeOldSelection, sender.SentMenus.Count);
        Assert.Contains("expired", sender.CallbackAnswers.Last().Text);
        await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => button.Text == "Previous page" && HasRoute(button, "staff")).CallbackData));
        while (!sender.SentMenus.Last().Buttons.Any(button => button.Text == "Task05 active employee"))
            await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => button.Text == "Previous page" && HasRoute(button, "staff")).CallbackData));
        await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => button.Text == "Task05 active employee").CallbackData));
        Assert.Contains("Status — Active", sender.SentMenus.Last().Text);
        var renameButton = sender.SentMenus.Last().Buttons.Single(button => button.Text == "Rename").CallbackData;
        await Dispatch(Click(renameButton));
        Assert.Contains("Type a new display name", sender.SentMenus.Last().Text);
        foreach (var invalid in new[] { "   ", new string('x', 201) })
        {
            await Dispatch(TelegramFoundationTests.Message(invalid, ownerId, ownerId));
            Assert.Contains("Type another name", sender.SentMenus.Last().Text);
            await using var db = fixture.CreateContext();
            Assert.Equal("Task05 active employee", (await db.StaffMembers
                .SingleAsync(profile => db.Users.Any(user => user.Id == profile.UserId && user.TelegramUserId == employeeId))).DisplayName);
        }
        await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => HasRoute(button, "back")).CallbackData));
        Assert.Contains("Name — Task05 active employee", sender.SentMenus.Last().Text);
        var menusBeforeStaleRename = sender.SentMenus.Count;
        await Dispatch(Click(renameButton));
        Assert.Equal(menusBeforeStaleRename, sender.SentMenus.Count);
        Assert.Contains("expired", sender.CallbackAnswers.Last().Text);
        await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => button.Text == "Rename").CallbackData));

        var saveName = TelegramFoundationTests.Message("  Task05 renamed employee  ", ownerId, ownerId);
        saveName.Id = ++updateId;
        sender.FailNextSend(new HttpRequestException("Temporary rename confirmation failure"));
        await using (var scope = configured.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>();
            await Assert.ThrowsAsync<HttpRequestException>(() => dispatcher.DispatchAsync(saveName));
            await using (var db = fixture.CreateContext())
            {
                var profile = await db.StaffMembers.SingleAsync(profile => db.Users.Any(user => user.Id == profile.UserId && user.TelegramUserId == employeeId));
                Assert.Equal("Task05 renamed employee", profile.DisplayName);
                profile.Rename("Task05 later rename");
                await db.SaveChangesAsync();
            }
            await dispatcher.DispatchAsync(saveName);
        }
        Assert.Contains("Display name updated to Task05 renamed employee", sender.SentMenus.Last().Text);
        await using (var db = fixture.CreateContext())
            Assert.Equal("Task05 later rename", (await db.StaffMembers
                .SingleAsync(profile => db.Users.Any(user => user.Id == profile.UserId && user.TelegramUserId == employeeId))).DisplayName);
        await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => HasRoute(button, "back")).CallbackData));
        Assert.Contains("Name — Task05 later rename", sender.SentMenus.Last().Text);
        await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => button.Text == "Rename").CallbackData));
        await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => HasRoute(button, "cancel")).CallbackData));
        Assert.Contains("Welcome", sender.SentMenus.Last().Text);
        await Dispatch(TelegramFoundationTests.Message("/staff", ownerId, ownerId));

        var oldBack = sender.SentMenus.Last().Buttons.Single(button => HasRoute(button, "back")).CallbackData;
        await Dispatch(Click(oldBack));
        Assert.Contains("Welcome", sender.SentMenus.Last().Text);
        var menuCount = sender.SentMenus.Count;
        await Dispatch(Click(oldBack));
        Assert.Equal(menuCount, sender.SentMenus.Count);
        Assert.Contains("expired", sender.CallbackAnswers.Last().Text);

        var retry = TelegramFoundationTests.Message("/staff", ownerId, ownerId);
        retry.Id = ++updateId;
        sender.FailNextSend(new HttpRequestException("Temporary failure"));
        await using (var scope = configured.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>();
            await Assert.ThrowsAsync<HttpRequestException>(() => dispatcher.DispatchAsync(retry));
            await dispatcher.DispatchAsync(retry);
        }
        await Dispatch(Click(sender.SentMenus.Last().Buttons.Single(button => HasRoute(button, "cancel")).CallbackData));
        Assert.Contains("Welcome", sender.SentMenus.Last().Text);
        using (var lease = await configured.Services.GetRequiredService<ConversationStateStore>()
                   .AcquireAsync(new(ownerId, ownerId)))
        {
            Assert.Equal("start", lease.State.Step);
            Assert.False(lease.State.CanGoBack);
        }

        foreach (var userId in new[] { employeeId, clientId })
        {
            await Dispatch(TelegramFoundationTests.Message("/start", userId, userId));
            Assert.DoesNotContain(sender.SentMenus.Last().Buttons, button => button.Text == "Staff management");
            var before = sender.SentMenus.Count;
            await Dispatch(TelegramFoundationTests.Message("/staff", userId, userId));
            Assert.Equal(before, sender.SentMenus.Count);
            Assert.Contains("permission", sender.SentMessages.Last().Text);
            foreach (var command in new[] { "/staff.details", "/staff.rename" })
            {
                await Dispatch(TelegramFoundationTests.Message(command, userId, userId));
                Assert.Equal(before, sender.SentMenus.Count);
                Assert.Contains("permission", sender.SentMessages.Last().Text);
            }
        }
    }

    [Fact]
    public async Task ActivationRequiresConfirmationAndRetriesDoNotRepeatCommittedChanges()
    {
        const long ownerId = 9_054_000_001;
        const long employeeId = 9_054_000_002;
        const long clientId = 9_054_000_003;
        Guid staffId;
        await using (var db = fixture.CreateContext())
        {
            var employee = new AppUser(employeeId, "Activation employee", null, DateTime.UtcNow);
            var profile = new StaffMember(employee.Id, "Task05 activation employee");
            staffId = profile.Id;
            db.Users.Add(employee);
            db.StaffMembers.Add(profile);
            db.BookingCoordinationRows.Add(new BookingCoordination(profile));
            await db.SaveChangesAsync();
        }
        var sender = new FakeTelegramClient();
        await using var factory = new ApiFactory(fixture.ConnectionString);
        await using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Owner:TelegramUserId"] = ownerId.ToString() }));
            builder.ConfigureTestServices(services => services.AddSingleton<ITelegramMessageSender>(sender));
        });
        var updateId = 0;
        async Task Deliver(Update update)
        {
            await using var scope = configured.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>().DispatchAsync(update);
        }
        Task Send(Update update)
        {
            update.Id = ++updateId;
            return Deliver(update);
        }
        Update Click(string data, long userId = ownerId) => new()
        {
            CallbackQuery = new CallbackQuery
            {
                Id = Guid.NewGuid().ToString("N"), Data = data, ChatInstance = "test",
                From = new Telegram.Bot.Types.User { Id = userId, FirstName = "Caller" },
                Message = new Message { Chat = new Chat { Id = ownerId, Type = ChatType.Private } }
            }
        };
        string Button(string label) => sender.SentMenus.Last().Buttons.Single(button => button.Text == label).CallbackData;
        async Task OpenDetails()
        {
            await Send(TelegramFoundationTests.Message("/staff", ownerId, ownerId));
            while (!sender.SentMenus.Last().Buttons.Any(button => button.Text == "Task05 activation employee"))
                await Send(Click(sender.SentMenus.Last().Buttons.Single(button =>
                    button.Text == "Next page" && HasRoute(button, "staff")).CallbackData));
            await Send(Click(Button("Task05 activation employee")));
        }
        async Task AssertStatus(bool active, long version)
        {
            await using var db = fixture.CreateContext();
            Assert.Equal(active, (await db.StaffMembers.SingleAsync(profile => profile.Id == staffId)).IsActive);
            Assert.Equal(version, (await db.BookingCoordinationRows.SingleAsync(row => row.StaffMemberId == staffId)).ChangeVersion);
        }

        await OpenDetails();
        var openPrompt = Click(Button("Deactivate"));
        openPrompt.Id = ++updateId;
        sender.FailNextSend(new HttpRequestException("Prompt send failed"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Deliver(openPrompt));
        await Deliver(openPrompt);
        await AssertStatus(true, 0);
        Assert.Contains("Deactivate Task05 activation employee?", sender.SentMenus.Last().Text);
        await Send(Click(Button("Back")));
        Assert.Contains("Status — Active", sender.SentMenus.Last().Text);
        await Send(Click(Button("Deactivate")));
        var cancelledConfirm = Button("Confirm deactivation");
        await Send(Click(Button("Cancel")));
        Assert.Contains("Welcome", sender.SentMenus.Last().Text);
        await Send(Click(cancelledConfirm));
        Assert.Contains("expired", sender.CallbackAnswers.Last().Text);
        await AssertStatus(true, 0);

        await OpenDetails();
        await Send(Click(Button("Deactivate")));
        await Send(TelegramFoundationTests.Message("yes", ownerId, ownerId));
        await AssertStatus(true, 0);
        var confirm = Button("Confirm deactivation");
        Assert.True(CallbackDataParser.TryParse(confirm, out var parsed));
        await Send(Click(CallbackDataParser.Format(parsed!.Route, parsed.Revision)));
        Assert.Contains("expired", sender.CallbackAnswers.Last().Text);
        await Send(Click(confirm, employeeId));
        Assert.Contains("expired", sender.CallbackAnswers.Last().Text);
        await AssertStatus(true, 0);

        var save = Click(confirm);
        save.Id = ++updateId;
        sender.FailNextSend(new HttpRequestException("Saved reply failed"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Deliver(save));
        await AssertStatus(false, 1);
        // A later owner action must survive retrying the earlier action's failed reply.
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(profile => profile.Id == staffId)).Activate();
            await db.SaveChangesAsync();
        }
        await Deliver(save);
        Assert.Equal("Employee deactivated.", sender.SentMenus.Last().Text);
        await AssertStatus(true, 1);
        await Send(Click(Button("Back")));
        Assert.Contains("Status — Active", sender.SentMenus.Last().Text);

        await Send(Click(Button("Deactivate")));
        confirm = Button("Confirm deactivation");
        await Send(Click(confirm));
        await AssertStatus(false, 2);
        await Send(Click(confirm));
        Assert.Contains("expired", sender.CallbackAnswers.Last().Text);
        await AssertStatus(false, 2);
        await Send(Click(Button("Back")));
        Assert.Contains("Status — Inactive", sender.SentMenus.Last().Text);
        await Send(Click(Button("Reactivate")));
        await AssertStatus(false, 2);
        await Send(Click(Button("Confirm reactivation")));
        Assert.Equal("Employee reactivated.", sender.SentMenus.Last().Text);
        await AssertStatus(true, 2);
        await Send(Click(Button("Back")));
        Assert.Contains("Status — Active", sender.SentMenus.Last().Text);

        foreach (var userId in new[] { employeeId, clientId })
        {
            var before = sender.SentMenus.Count;
            await Send(TelegramFoundationTests.Message("/staff.activation", userId, userId));
            Assert.Equal(before, sender.SentMenus.Count);
            Assert.Contains("permission", sender.SentMessages.Last().Text);
            await AssertStatus(true, 2);
        }
    }

    [Fact]
    public async Task OwnerCreatesStaffFromRegisteredUserAndHandlesInvalidInputDuplicatesAndReplyRetry()
    {
        const long ownerId = 9_055_000_001;
        const long candidateId = 9_055_000_002;
        const long raceId = 9_055_000_003;
        const long existingId = 9_055_000_004;
        const long clientId = 9_055_000_005;
        Guid candidateUserId;
        Guid raceUserId;
        await using (var db = fixture.CreateContext())
        {
            var candidate = new AppUser(candidateId, "New employee", "new_employee", DateTime.UtcNow);
            var race = new AppUser(raceId, "Other candidate", null, DateTime.UtcNow);
            var existing = new AppUser(existingId, "Existing employee", null, DateTime.UtcNow);
            candidateUserId = candidate.Id;
            raceUserId = race.Id;
            var profile = new StaffMember(existing.Id, "Already on staff");
            db.Users.AddRange(candidate, race, existing);
            db.StaffMembers.Add(profile);
            db.BookingCoordinationRows.Add(new BookingCoordination(profile));
            await db.SaveChangesAsync();
        }
        var sender = new FakeTelegramClient();
        await using var factory = new ApiFactory(fixture.ConnectionString);
        await using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Owner:TelegramUserId"] = ownerId.ToString() }));
            builder.ConfigureTestServices(services => services.AddSingleton<ITelegramMessageSender>(sender));
        });
        var updateId = 0;
        async Task Deliver(Update update)
        {
            await using var scope = configured.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>().DispatchAsync(update);
        }
        Task Send(Update update)
        {
            update.Id = ++updateId;
            return Deliver(update);
        }
        Task Text(string text) => Send(TelegramFoundationTests.Message(text, ownerId, ownerId));
        Update Click(string data, long userId = ownerId) => new()
        {
            CallbackQuery = new CallbackQuery
            {
                Id = Guid.NewGuid().ToString("N"), Data = data, ChatInstance = "test",
                From = new Telegram.Bot.Types.User { Id = userId, FirstName = "Caller" },
                Message = new Message { Chat = new Chat { Id = ownerId, Type = ChatType.Private } }
            }
        };
        string Button(string label) => sender.SentMenus.Last().Buttons.Single(button => button.Text == label).CallbackData;
        async Task OpenAdd()
        {
            await Text("/staff");
            await Send(Click(sender.SentMenus.Last().Buttons.Single(button => HasRoute(button, "staff.add")).CallbackData));
        }
        async Task AssertNoCandidateProfile()
        {
            await using var db = fixture.CreateContext();
            Assert.False(await db.StaffMembers.AnyAsync(profile => profile.UserId == candidateUserId));
        }

        await OpenAdd();
        foreach (var invalid in new[] { "abc", "0", "-1", "9223372036854775808" })
        {
            await Text(invalid);
            Assert.Contains("positive numeric", sender.SentMenus.Last().Text);
        }
        await Text("9055000999");
        Assert.Contains("not registered", sender.SentMenus.Last().Text);
        await Text(existingId.ToString());
        Assert.Contains("already has a staff profile", sender.SentMenus.Last().Text);
        await Text(candidateId.ToString());
        Assert.Contains("Name — New employee", sender.SentMenus.Last().Text);
        Assert.Contains("@new_employee", sender.SentMenus.Last().Text);
        var oldSelection = Button("Use this user");
        await Text("bad input");
        var before = sender.SentMenus.Count;
        await Send(Click(oldSelection));
        Assert.Equal(before, sender.SentMenus.Count);
        Assert.Contains("expired", sender.CallbackAnswers.Last().Text);
        await Text(candidateId.ToString());
        var choose = Button("Use this user");
        await Send(Click(choose, clientId));
        Assert.Contains("expired", sender.CallbackAnswers.Last().Text);
        await Send(Click(choose));
        Assert.Contains("Type the staff display name for New employee", sender.SentMenus.Last().Text);
        foreach (var invalid in new[] { "   ", new string('x', 201) })
        {
            await Text(invalid);
            Assert.Contains("Type another name", sender.SentMenus.Last().Text);
            await AssertNoCandidateProfile();
        }
        await Send(Click(Button("Back")));
        Assert.Contains("Name — New employee", sender.SentMenus.Last().Text);
        await Send(Click(Button("Use this user")));
        await Send(Click(Button("Cancel")));
        await AssertNoCandidateProfile();
        Assert.Contains("Welcome", sender.SentMenus.Last().Text);

        await OpenAdd();
        await Text(candidateId.ToString());
        await Send(Click(Button("Use this user")));
        var save = TelegramFoundationTests.Message("  Created employee  ", ownerId, ownerId);
        save.Id = ++updateId;
        sender.FailNextSend(new HttpRequestException("Creation result send failed"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Deliver(save));
        await using (var db = fixture.CreateContext())
        {
            var profile = await db.StaffMembers.SingleAsync(profile => profile.UserId == candidateUserId);
            Assert.Equal("Created employee", profile.DisplayName);
            Assert.True(profile.IsActive);
            Assert.Equal(0, (await db.BookingCoordinationRows.SingleAsync(row => row.StaffMemberId == profile.Id)).ChangeVersion);
        }
        await Deliver(save);
        Assert.Equal("Staff profile created for Created employee.", sender.SentMenus.Last().Text);
        await Text("Repeated input");
        Assert.Equal("Staff profile created for Created employee.", sender.SentMenus.Last().Text);
        await Send(Click(Button("View employee")));
        Assert.Contains("Name — Created employee", sender.SentMenus.Last().Text);
        await using (var db = fixture.CreateContext())
            Assert.Equal(1, await db.StaffMembers.CountAsync(profile => profile.UserId == candidateUserId));

        await OpenAdd();
        await Text(candidateId.ToString());
        Assert.Contains("already has a staff profile", sender.SentMenus.Last().Text);
        await Text(raceId.ToString());
        await Send(Click(Button("Use this user")));
        await using (var db = fixture.CreateContext())
        {
            var profile = new StaffMember(raceUserId, "Created by another operation");
            db.StaffMembers.Add(profile);
            db.BookingCoordinationRows.Add(new BookingCoordination(profile));
            await db.SaveChangesAsync();
        }
        await Text("Duplicate attempt");
        Assert.Contains("already has a staff profile", sender.SentMenus.Last().Text);
        await using (var db = fixture.CreateContext())
            Assert.Equal("Created by another operation", (await db.StaffMembers.SingleAsync(profile => profile.UserId == raceUserId)).DisplayName);

        foreach (var userId in new[] { existingId, clientId })
        {
            foreach (var command in new[] { "/staff.add", "/staff.create" })
            {
                before = sender.SentMenus.Count;
                await Send(TelegramFoundationTests.Message(command, userId, userId));
                Assert.Equal(before, sender.SentMenus.Count);
                Assert.Contains("permission", sender.SentMessages.Last().Text);
            }
        }
    }
}
