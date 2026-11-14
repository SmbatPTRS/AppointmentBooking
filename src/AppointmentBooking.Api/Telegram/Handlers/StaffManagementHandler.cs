using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class StaffManagementHandler(
    StaffManagementService staff, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "staff";
    private const int PageSize = 10;

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var profiles = await staff.ListAsync(context.Caller.Actor, includeInactive: true, ct);
        if (!context.IsNavigation && !(context.IsRetry && context.State.Step == Route))
        {
            if (context.ItemId is { } firstOnPage)
            {
                var index = profiles.ToList().FindIndex(profile => profile.Id == firstOnPage);
                if (index < 0)
                {
                    await sender.SendMenuAsync(context.ChatId, "This staff list changed. Open Staff management again.",
                        NavigationKeyboard.Create(context.State), ct);
                    return;
                }
                context.State.NavigateTo(Route, clock.UtcNow);
                context.State.SetFeatureData("management", new StaffManagementDraft(index / PageSize));
            }
            else
            {
                context.State.EnterFlow(Route, clock.UtcNow);
                context.State.SetFeatureData("management", new StaffManagementDraft());
            }
        }

        var draft = context.State.GetFeatureData<StaffManagementDraft>("management") ?? new();
        var lastPage = Math.Max(0, (profiles.Count - 1) / PageSize);
        var page = Math.Min(draft.Page, lastPage);
        context.State.SetFeatureData("management", draft with { Page = page });
        var visible = profiles.Skip(page * PageSize).Take(PageSize).ToArray();
        var buttons = visible.Select(profile => context.State.ItemButton(
            profile.DisplayName, "staff.details", profile.Id)).ToList();
        if (page > 0)
            buttons.Add(context.State.ItemButton("Previous page", Route, profiles[(page - 1) * PageSize].Id));
        if (page < lastPage)
            buttons.Add(context.State.ItemButton("Next page", Route, profiles[(page + 1) * PageSize].Id));
        buttons.Add(context.State.Button("Add employee", "staff.add"));
        buttons.AddRange(NavigationKeyboard.Create(context.State));

        var text = profiles.Count == 0 ? "No staff profiles yet." :
            $"Staff profiles — page {page + 1} of {lastPage + 1}\n\n" +
            string.Join("\n", visible.Select(profile =>
                $"{profile.DisplayName} — {(profile.IsActive ? "Active" : "Inactive")}"));
        await sender.SendMenuAsync(context.ChatId, text, buttons, ct);
    }
}
