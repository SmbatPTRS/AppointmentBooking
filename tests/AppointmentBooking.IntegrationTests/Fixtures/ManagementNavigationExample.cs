using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.IntegrationTests.Fixtures;

// Executable handoff example, not registered in production. A owns the real services/screens.
public static class ManagementNavigationExample
{
    public sealed record ManagementDraft(Guid StaffId, string? PendingName = null);
    public const string Feature = "management";

    public static IEnumerable<ITelegramHandler> Create(ITelegramMessageSender sender, IClock clock, Guid staffId)
    {
        yield return new Screen("demo.staff", async (context, ct) =>
        {
            if (!context.IsNavigation && !(context.IsRetry && context.State.Step == "demo.staff"))
                context.State.EnterFlow("demo.staff", clock.UtcNow);
            await sender.SendMenuAsync(context.ChatId, "Staff list",
                [context.State.ItemButton("Ada", "demo.details", staffId), .. NavigationKeyboard.Create(context.State)], ct);
        });
        yield return new Screen("demo.details", async (context, ct) =>
        {
            if (!context.IsNavigation && !(context.IsRetry && context.State.Step == "demo.details"))
            {
                if (context.ItemId is not { } selected) return;
                context.State.NavigateTo("demo.details", clock.UtcNow);
                context.State.SetFeatureData(Feature, new ManagementDraft(selected));
            }
            var draft = context.State.GetFeatureData<ManagementDraft>(Feature)!;
            await sender.SendMenuAsync(context.ChatId, $"Employee details: {draft.StaffId}",
                [context.State.Button("Rename", "demo.rename"), .. NavigationKeyboard.Create(context.State)], ct);
        });
        yield return new Screen("demo.rename", async (context, ct) =>
        {
            if (!context.IsNavigation && !(context.IsRetry && context.State.Step == "demo.rename") && context.Callback is not null)
                context.State.NavigateTo("demo.rename", clock.UtcNow);
            var draft = context.State.GetFeatureData<ManagementDraft>(Feature)!;
            if (!context.IsNavigation && context.Text is { } name)
                context.State.SetFeatureData(Feature, draft = draft with { PendingName = name });
            // Preview only. A calls StaffManagementService with context.Caller.Actor to save.
            await sender.SendMenuAsync(context.ChatId,
                draft.PendingName is null ? "Type a new display name." : $"Pending name: {draft.PendingName}",
                NavigationKeyboard.Create(context.State), ct);
        });
    }

    private sealed class Screen(string route, Func<TelegramHandlerContext, CancellationToken, Task> handle) : ITelegramHandler
    {
        public string Route => route;
        public Task HandleAsync(TelegramHandlerContext context, CancellationToken ct) => handle(context, ct);
    }
}
