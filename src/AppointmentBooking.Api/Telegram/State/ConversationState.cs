using System.Text.Json;

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
    private readonly HashSet<string> plainCallbacks = [];
    private readonly Dictionary<string, JsonElement> featureData = [];
    private readonly Dictionary<string, (string Route, Guid Id)> items = [];
    private readonly List<Screen> history = [];
    internal (string Key, string Route)? PendingNavigation { get; set; }
    internal (string Key, string Route, Guid? ItemId)? PendingDispatch { get; set; }
    public bool CanGoBack => history.Count > 0;
    public bool AllowsCallback(string route) => allowedCallbacks.Contains(route);
    public bool AllowsPlainCallback(string route) => plainCallbacks.Contains(route);

    // Store JSON values, not references to mutable feature objects, entities or services.
    public void SetFeatureData<T>(string feature, T value) where T : notnull =>
        featureData[feature] = JsonSerializer.SerializeToElement(value);

    public T? GetFeatureData<T>(string feature) =>
        featureData.TryGetValue(feature, out var value) ? value.Deserialize<T>() : default;

    public void ClearFeatureData(string feature) => featureData.Remove(feature);

    public TelegramMenuButton Button(string label, string route)
    {
        var data = CallbackDataParser.Format(route, Revision);
        allowedCallbacks.Add(route);
        plainCallbacks.Add(route);
        return new(label, data);
    }

    public TelegramMenuButton ItemButton(string label, string route, Guid itemId)
    {
        if (itemId == Guid.Empty) throw new ArgumentException("An item ID is required.", nameof(itemId));
        if (items.Count >= 256) throw new InvalidOperationException("Too many buttons on one screen.");
        string key;
        do { key = Guid.NewGuid().ToString("N")[..8]; } while (items.ContainsKey(key));
        var data = CallbackDataParser.FormatItem(route, Revision, key);
        items.Add(key, (route, itemId));
        allowedCallbacks.Add(route);
        return new(label, data);
    }

    public bool TryResolveItem(TelegramCallback callback, out Guid itemId)
    {
        itemId = default;
        if (callback.Revision != Revision || callback.ButtonKey is null ||
            !items.TryGetValue(callback.ButtonKey, out var item) || item.Route != callback.Route) return false;
        itemId = item.Id;
        return true;
    }

    public void EnterFlow(string step, DateTime nowUtc)
    {
        ValidateStep(step);
        Reset(nowUtc);
        NavigateTo(step, nowUtc);
    }

    public void NavigateTo(string step, DateTime nowUtc)
    {
        ValidateStep(step);
        if (history.Count == 32) history.RemoveAt(0);
        history.Add(new Screen(Step, new(Selections), new(featureData), ConfirmationRequestId));
        BeginStep(step, nowUtc);
    }

    public bool GoBack(DateTime nowUtc)
    {
        if (history.Count == 0) { Reset(nowUtc); return false; }
        var previous = history[^1];
        history.RemoveAt(history.Count - 1);
        Selections.Clear();
        foreach (var pair in previous.Selections) Selections.Add(pair.Key, pair.Value);
        featureData.Clear();
        foreach (var pair in previous.FeatureData) featureData.Add(pair.Key, pair.Value);
        ConfirmationRequestId = previous.ConfirmationRequestId;
        BeginStep(previous.Step, nowUtc);
        return true;
    }

    public void SetAllowedCallbacks(IEnumerable<string> routes)
    {
        var values = routes.ToArray();
        if (values.Any(route => !CallbackDataParser.IsValidRoute(route)))
            throw new ArgumentException("Invalid callback route.", nameof(routes));
        allowedCallbacks.Clear();
        allowedCallbacks.UnionWith(values);
        plainCallbacks.Clear();
        plainCallbacks.UnionWith(values);
    }

    public ConversationState(DateTime nowUtc) => ExpiresAtUtc = nowUtc.AddMinutes(30);

    // Handlers call this when changing screens; old buttons then become invalid.
    // Do not call it or replace ConfirmationRequestId just to retry a failed send.
    public void BeginStep(string step, DateTime nowUtc)
    {
        ValidateStep(step);
        allowedCallbacks.Clear();
        plainCallbacks.Clear();
        items.Clear();
        Step = step;
        Revision = Guid.NewGuid();
        ExpiresAtUtc = nowUtc.AddMinutes(30);
    }

    public void Reset(DateTime nowUtc)
    {
        Selections.Clear();
        history.Clear();
        featureData.Clear();
        PendingNavigation = null;
        PendingDispatch = null;
        ConfirmationRequestId = null;
        BeginStep("start", nowUtc);
    }

    private static void ValidateStep(string step)
    {
        if (!CallbackDataParser.IsValidRoute(step) || step is "back" or "cancel")
            throw new ArgumentException("Invalid screen route.", nameof(step));
    }

    private sealed record Screen(string Step, Dictionary<string, string> Selections,
        Dictionary<string, JsonElement> FeatureData, Guid? ConfirmationRequestId);
}
