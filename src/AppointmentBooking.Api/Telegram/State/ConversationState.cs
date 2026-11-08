namespace AppointmentBooking.Api.Telegram.State;

public readonly record struct ConversationKey(long TelegramUserId, long ChatId);

/// <summary>Only navigation and scalar selections belong here; never EF entities or scopes.</summary>
public sealed class ConversationState
{
    public string Step { get; private set; } = "start";
    public Guid Revision { get; private set; } = Guid.NewGuid();
    public DateTime ExpiresAtUtc { get; private set; }
    public Dictionary<string, string> Selections { get; } = [];
    public Guid? ConfirmationRequestId { get; set; }
    private readonly HashSet<string> allowedCallbacks = [];
    public bool AllowsCallback(string route) => allowedCallbacks.Contains(route);

    public void SetAllowedCallbacks(IEnumerable<string> routes)
    {
        var values = routes.ToArray();
        if (values.Any(route => !CallbackDataParser.IsValidRoute(route)))
            throw new ArgumentException("Invalid callback route.", nameof(routes));
        allowedCallbacks.Clear();
        allowedCallbacks.UnionWith(values);
    }

    public ConversationState(DateTime nowUtc) => ExpiresAtUtc = nowUtc.AddMinutes(30);

    // Handlers call this when changing screens; old buttons then become invalid.
    // Do not call it or replace ConfirmationRequestId just to retry a failed send.
    public void BeginStep(string step, DateTime nowUtc)
    {
        if (!CallbackDataParser.IsValidRoute(step)) throw new ArgumentException("Invalid handler route.", nameof(step));
        allowedCallbacks.Clear();
        Step = step;
        Revision = Guid.NewGuid();
        ExpiresAtUtc = nowUtc.AddMinutes(30);
    }

    public void Reset(DateTime nowUtc)
    {
        Selections.Clear();
        ConfirmationRequestId = null;
        BeginStep("start", nowUtc);
    }
}
