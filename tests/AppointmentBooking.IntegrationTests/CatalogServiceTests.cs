using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogServiceTests(PostgresFixture fixture)
{
    private sealed class OwnerAccess(bool denied = false) : IAccessService
    {
        public Task<bool> IsOwnerAsync(Actor actor, CancellationToken ct) => Task.FromResult(!denied);
        public Task RequireOwnerAsync(Actor actor, CancellationToken ct) => denied
            ? throw new AppException(ErrorCodes.Forbidden, "Owner required.", AppErrorKind.Forbidden)
            : Task.CompletedTask;
        public Task RequireStaffManagementAsync(Actor actor, Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task RequireAppointmentManagementAsync(Actor actor, Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(200)]
    public async Task CreatePersistsTrimmedActiveService(int length)
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var sut = new CatalogService(new CatalogRepository(db), new OwnerAccess(), db);
        var name = new string('a', length);
        var result = await sut.CreateServiceAsync(new Actor(Guid.NewGuid()), new CreateServiceRequest("  " + name + "  "), default);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(name, result.Name);
        Assert.True(result.IsActive);
        db.ChangeTracker.Clear();
        var saved = await db.Services.SingleAsync(s => s.Id == result.Id);
        Assert.Equal(name, saved.Name);
        Assert.True(saved.IsActive);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 201)]
    [InlineData(true, 7)]
    public async Task RejectedCreateDoesNotStageOrPersistService(bool denied, int length)
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var before = await db.Services.CountAsync();
        var sut = new CatalogService(new CatalogRepository(db), new OwnerAccess(denied), db);
        var error = await Assert.ThrowsAsync<AppException>(() => sut.CreateServiceAsync(
            new Actor(Guid.NewGuid()), new CreateServiceRequest("  " + new string('a', length) + "  "), default));
        Assert.Equal(denied ? ErrorCodes.Forbidden : ErrorCodes.ValidationFailed, error.Code);
        Assert.Equal(denied ? AppErrorKind.Forbidden : AppErrorKind.Validation, error.Kind);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(before, await db.Services.CountAsync());
    }
    [Fact]
    public async Task CustomerServiceListExcludesInactiveServices()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var active = new Service("Visible service");
        var hidden = new Service("Hidden service");
        hidden.Deactivate();
        db.AddRange(active, hidden);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // Customer reads must not require owner privileges.
        var sut = new CatalogService(new CatalogRepository(db), new OwnerAccess(true), db);
        var result = await sut.ListActiveServicesAsync(default);
        Assert.Contains(new ServiceDto(active.Id, active.Name, true), result);
        Assert.DoesNotContain(result, s => s.Id == hidden.Id);
        Assert.All(result, s => Assert.True(s.IsActive));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task CustomerOfferingsRequireActiveStaffServiceAndOffering(bool staffActive, bool serviceActive, bool offeringActive)
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var user = new User(Random.Shared.NextInt64(5000000000, 9000000000), "Catalogue", null, DateTime.UtcNow);
        var otherUser = new User(Random.Shared.NextInt64(5000000000, 9000000000), "Other", null, DateTime.UtcNow);
        var staff = new StaffMember(user.Id, "Catalogue");
        var other = new StaffMember(otherUser.Id, "Other");
        var service = new Service("Haircut");
        var offering = new StaffService(staff.Id, service.Id, 5000.25m, 40);
        var otherOffering = new StaffService(other.Id, service.Id, 7000m, 60);
        if (!staffActive) staff.Deactivate();
        if (!serviceActive) service.Deactivate();
        if (!offeringActive) offering.Deactivate();
        db.AddRange(user, otherUser, staff, other, service, offering, otherOffering);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var sut = new CatalogService(new CatalogRepository(db), new OwnerAccess(true), db);
        var result = await sut.ListActiveOfferingsAsync(staff.Id, default);
        if (staffActive && serviceActive && offeringActive)
            Assert.Equal(new StaffServiceDto(offering.Id, staff.Id, service.Id, "Haircut", 5000.25m, 40, true), Assert.Single(result));
        else Assert.Empty(result);
        Assert.Empty(await sut.ListActiveOfferingsAsync(Guid.NewGuid(), default));
        Assert.Empty(db.ChangeTracker.Entries());
    }
    private sealed class CatalogClock : IClock { public DateTime UtcNow => new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc); }

    [Fact]
    public async Task TelegramCatalogueShowsActiveNamesAndCurrentMainMenuButton()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var visible = new Service("Visible-" + Guid.NewGuid());
        var hidden = new Service("Hidden-" + Guid.NewGuid());
        hidden.Deactivate();
        db.AddRange(visible, hidden);
        await db.SaveChangesAsync();
        var sender = new FakeTelegramClient();
        var clock = new CatalogClock();
        var state = new AppointmentBooking.Api.Telegram.State.ConversationState(clock.UtcNow);
        var handler = new AppointmentBooking.Api.Telegram.Handlers.CatalogHandler(
            new CatalogService(new CatalogRepository(db), new OwnerAccess(true), db), sender, clock);
        var context = new AppointmentBooking.Api.Telegram.TelegramHandlerContext(
            new(new Actor(Guid.NewGuid()), 42, "Client"), 123, "/catalog", null, state);
        await handler.HandleAsync(context, default);
        var text = string.Join("\n", sender.SentMessages.Select(m => m.Text));
        Assert.Contains(visible.Name, text);
        Assert.DoesNotContain(hidden.Name, text);
        Assert.All(sender.SentMessages, m => { Assert.Equal(123, m.ChatId); Assert.True(m.Text.Length <= 4096); });
        var button = Assert.Single(Assert.Single(sender.SentMenus).Buttons);
        Assert.True(AppointmentBooking.Api.Telegram.CallbackDataParser.TryParse(button.CallbackData, out var parsed));
        Assert.Equal("start", parsed!.Route);
        Assert.Equal(state.Revision, parsed.Revision);
        Assert.True(state.AllowsCallback("start"));
        Assert.Equal("catalog", state.Step);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task TelegramCatalogueHandlesEmptyAndLongLists(int count)
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Isolate this screen's catalogue; the transaction rolls back all test changes.
        await db.Services.ExecuteUpdateAsync(setters => setters.SetProperty(s => s.IsActive, false));
        var names = Enumerable.Range(0, count).Select(i => i.ToString("D3") + new string('x', 197)).ToArray();
        db.Services.AddRange(names.Select(name => new Service(name)));
        await db.SaveChangesAsync();
        var sender = new FakeTelegramClient();
        var clock = new CatalogClock();
        var state = new AppointmentBooking.Api.Telegram.State.ConversationState(clock.UtcNow);
        var handler = new AppointmentBooking.Api.Telegram.Handlers.CatalogHandler(
            new CatalogService(new CatalogRepository(db), new OwnerAccess(true), db), sender, clock);
        var context = new AppointmentBooking.Api.Telegram.TelegramHandlerContext(
            new(new Actor(Guid.NewGuid()), 42, "Client"), 123, "/catalog", null, state);
        await handler.HandleAsync(context, default);
        Assert.Equal(count == 0 ? 1 : 4, sender.SentMessages.Count);
        var lines = sender.SentMessages.SelectMany(m => m.Text.Split('\n'))
            .Where(line => line.StartsWith("• ")).Select(line => line[2..]).ToArray();
        Assert.Equal(names, lines);
        Assert.All(sender.SentMessages, m => Assert.InRange(m.Text.Length, 1, 4096));
        var menu = Assert.Single(sender.SentMenus);
        Assert.Single(menu.Buttons);
        if (count == 0) Assert.Equal("No services are available yet.", menu.Text);
        Assert.True(state.AllowsCallback("start"));
    }
    [Fact]
    public async Task RegisteredCatalogueCommandRunsThroughRealDispatcherAndReturnsToStart()
    {
        await using var db = fixture.CreateContext();
        var service = new Service("Dispatcher-" + Guid.NewGuid());
        db.Services.Add(service);
        await db.SaveChangesAsync();
        var sender = new FakeTelegramClient();
        await using var factory = new ApiFactory(fixture.ConnectionString);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ITelegramMessageSender>(sender)));
        using var client = configured.CreateClient();
        var telegramId = Random.Shared.NextInt64(5000000000, 9000000000);
        await using (var scope = configured.Services.CreateAsyncScope())
        {
            Assert.IsType<AppointmentBooking.Application.Identity.AccessService>(scope.ServiceProvider.GetRequiredService<IAccessService>());
            var update = new Telegram.Bot.Types.Update { Message = new Telegram.Bot.Types.Message {
                From = new Telegram.Bot.Types.User { Id = telegramId, FirstName = "Customer" },
                Chat = new Telegram.Bot.Types.Chat { Id = telegramId }, Text = "/catalog" } };
            await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>().DispatchAsync(update);
        }
        Assert.Contains(sender.SentMessages, m => m.Text.Contains(service.Name));
        var sentCount = sender.SentMessages.Count;
        await using (var ordinaryScope = configured.Services.CreateAsyncScope())
        {
            var ordinary = new Telegram.Bot.Types.Update { Message = new Telegram.Bot.Types.Message {
                From = new Telegram.Bot.Types.User { Id = telegramId, FirstName = "Customer" },
                Chat = new Telegram.Bot.Types.Chat { Id = telegramId }, Text = "hello" } };
            await ordinaryScope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>().DispatchAsync(ordinary);
        }
        Assert.Equal(sentCount, sender.SentMessages.Count);
        var button = Assert.Single(Assert.Single(sender.SentMenus).Buttons);
        await using (var scope = configured.Services.CreateAsyncScope())
        {
            var callback = new Telegram.Bot.Types.Update { CallbackQuery = new Telegram.Bot.Types.CallbackQuery {
                Id = "catalog-return", From = new Telegram.Bot.Types.User { Id = telegramId, FirstName = "Customer" },
                ChatInstance = "test", Data = button.CallbackData,
                Message = new Telegram.Bot.Types.Message { Chat = new Telegram.Bot.Types.Chat { Id = telegramId } } } };
            await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>().DispatchAsync(callback);
        }
        Assert.Equal(2, sender.SentMenus.Count);
        Assert.Single(sender.CallbackAnswers);
        Assert.Single(await db.Users.Where(u => u.TelegramUserId == telegramId).ToListAsync());
    }
}
