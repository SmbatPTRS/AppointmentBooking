using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Api.Telegram.State;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.UnitTests;

public sealed class ConversationStateStoreTests
{
    [Fact]
    public void BackRestoresTypedFieldsAndCancelClearsTheEntireFlow()
    {
        var now = DateTime.UtcNow;
        var state = new ConversationState(now);
        state.EnterFlow("staff", now);
        state.NavigateTo("details", now);
        state.SetFeatureData("management", new Draft(Guid.NewGuid(), null));
        var selected = state.GetFeatureData<Draft>("management");
        state.Selections["employee"] = "Ada";
        var requestId = state.ConfirmationRequestId = Guid.NewGuid();
        state.NavigateTo("rename", now);
        state.SetFeatureData("management", selected! with { PendingName = "New name" });
        var oldButton = state.ItemButton("Employee", "details", selected!.StaffId);
        var oldRevision = state.Revision;

        Assert.True(state.GoBack(now));
        Assert.Equal("details", state.Step);
        Assert.Equal(selected, state.GetFeatureData<Draft>("management"));
        Assert.Equal("Ada", state.Selections["employee"]);
        Assert.Equal(requestId, state.ConfirmationRequestId);
        Assert.NotEqual(oldRevision, state.Revision);
        CallbackDataParser.TryParse(oldButton.CallbackData, out var parsed);
        Assert.False(state.TryResolveItem(parsed!, out _));

        state.Reset(now);
        Assert.Equal("start", state.Step);
        Assert.False(state.CanGoBack);
        Assert.Empty(state.Selections);
        Assert.Null(state.GetFeatureData<Draft>("management"));
        Assert.Null(state.ConfirmationRequestId);
    }

    [Fact]
    public void TypedValuesAreCopiedRatherThanKeepingMutableObjectReferences()
    {
        var now = DateTime.UtcNow;
        var state = new ConversationState(now);
        var values = new List<string> { "original" };
        state.SetFeatureData("management", values);
        values.Add("external mutation");
        var returned = state.GetFeatureData<List<string>>("management")!;
        returned.Add("another mutation");
        Assert.Equal(new[] { "original" }, state.GetFeatureData<List<string>>("management"));
    }

    [Fact]
    public async Task SameConversationWaitsButOtherUsersAndChatsRemainIndependent()
    {
        var store = new ConversationStateStore(new Clock());
        using var first = await store.AcquireAsync(new(42, 100));
        var pending = store.AcquireAsync(new(42, 100));
        Assert.False(pending.IsCompleted);
        using var otherUser = await store.AcquireAsync(new(43, 100)).WaitAsync(TimeSpan.FromSeconds(2));
        using var otherChat = await store.AcquireAsync(new(42, 101)).WaitAsync(TimeSpan.FromSeconds(2));
        first.State.Selections["item"] = "A";
        first.Dispose();
        using var second = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("A", second.State.Selections["item"]);
        Assert.Empty(otherUser.State.Selections);
        Assert.Empty(otherChat.State.Selections);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotRemoveTheActiveConversationsLock()
    {
        var clock = new Clock();
        var store = new ConversationStateStore(clock);
        using var first = await store.AcquireAsync(new(42, 100));
        var originalState = first.State;
        using var cancellation = new CancellationTokenSource();
        var cancelled = store.AcquireAsync(new(42, 100), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        clock.UtcNow = clock.UtcNow.AddMinutes(31);
        var waiting = store.AcquireAsync(new(42, 100));
        Assert.False(waiting.IsCompleted);
        using var cleanupTrigger = await store.AcquireAsync(new(43, 100));
        first.Dispose();
        using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotSame(originalState, next.State);
    }

    [Fact]
    public async Task ExpiryAndRestartLoseSelectionsHistoryAndOldButtonBindings()
    {
        var clock = new Clock();
        var store = new ConversationStateStore(clock);
        string data;
        using (var lease = await store.AcquireAsync(new(42, 100)))
        {
            lease.State.EnterFlow("staff", clock.UtcNow);
            lease.State.SetFeatureData("management", new Draft(Guid.NewGuid(), "Pending"));
            lease.State.ConfirmationRequestId = Guid.NewGuid();
            data = lease.State.ItemButton("Employee", "details", Guid.NewGuid()).CallbackData;
        }
        clock.UtcNow = clock.UtcNow.AddMinutes(30);
        using var expired = await store.AcquireAsync(new(42, 100));
        Assert.Equal("start", expired.State.Step);
        Assert.Null(expired.State.GetFeatureData<Draft>("management"));
        Assert.Null(expired.State.ConfirmationRequestId);
        Assert.False(expired.State.CanGoBack);
        Assert.True(CallbackDataParser.TryParse(data, out var parsed));
        Assert.False(expired.State.TryResolveItem(parsed!, out _));
        using var restarted = await new ConversationStateStore(clock).AcquireAsync(new(42, 100));
        Assert.False(restarted.State.TryResolveItem(parsed!, out _));
    }

    [Fact]
    public async Task CleanupCannotSplitQueuedOperationsIntoDifferentLocks()
    {
        var clock = new Clock();
        var store = new ConversationStateStore(clock);
        var first = await store.AcquireAsync(new(42, 100));
        clock.UtcNow = clock.UtcNow.AddMinutes(31);
        var queued = Enumerable.Range(0, 20).Select(_ => store.AcquireAsync(new(42, 100))).ToArray();
        using var other = await store.AcquireAsync(new(43, 100));
        first.Dispose();
        foreach (var task in queued)
        {
            using var lease = await task.WaitAsync(TimeSpan.FromSeconds(2));
            var count = lease.State.Selections.GetValueOrDefault("count", "0");
            lease.State.Selections["count"] = (int.Parse(count) + 1).ToString();
        }
        using var final = await store.AcquireAsync(new(42, 100));
        Assert.Equal("20", final.State.Selections["count"]);
    }

    [Fact]
    public void RepeatedItemRendersReuseMappingsAtCapacity()
    {
        var state = new ConversationState(DateTime.UtcNow);
        var ids = Enumerable.Range(0, 256).Select(_ => Guid.NewGuid()).ToArray();
        var original = ids.Select(id => state.ItemButton("Employee", "details", id).CallbackData).ToArray();

        for (var render = 0; render < 3; render++)
        {
            var buttons = ids.Reverse().Select(id => state.ItemButton("Updated label", "details", id)).ToArray();
            Assert.Equal(original.Reverse(), buttons.Select(button => button.CallbackData));
            foreach (var button in buttons)
            {
                Assert.Equal("Updated label", button.Text);
                Assert.True(CallbackDataParser.TryParse(button.CallbackData, out var callback));
                Assert.True(state.TryResolveItem(callback!, out var selected));
                Assert.Contains(selected, ids);
            }
        }
        Assert.Throws<InvalidOperationException>(() => state.ItemButton("Another", "details", Guid.NewGuid()));
    }

    [Fact]
    public void ReuseBindsBothRouteAndItemAndDoesNotSurviveScreenTransitions()
    {
        var now = DateTime.UtcNow;
        var state = new ConversationState(now);
        var id = Guid.NewGuid();
        var details = state.ItemButton("Details", "details", id).CallbackData;
        var rename = state.ItemButton("Rename", "rename", id).CallbackData;
        Assert.NotEqual(details, rename);
        Assert.True(CallbackDataParser.TryParse(details, out var parsed));
        Assert.True(CallbackDataParser.TryParse(rename, out var otherRoute));
        Assert.False(state.TryResolveItem(parsed! with { Route = "rename" }, out _));
        Assert.True(state.TryResolveItem(otherRoute!, out var selected));
        Assert.Equal(id, selected);

        state.BeginStep("details", now);
        var fresh = state.ItemButton("Details", "details", id).CallbackData;
        Assert.NotEqual(details, fresh);
        Assert.False(state.TryResolveItem(parsed!, out _));
        state.Reset(now);
        Assert.NotEqual(fresh, state.ItemButton("Details", "details", id).CallbackData);
    }

    public sealed record Draft(Guid StaffId, string? PendingName);
    private sealed class Clock : IClock
    { public DateTime UtcNow { get; set; } = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc); }
}
