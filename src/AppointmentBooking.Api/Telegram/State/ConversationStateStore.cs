using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.State;

/// <summary>Single-instance state. The dispatcher holds a lease for the whole interaction.</summary>
public sealed class ConversationStateStore(IClock clock)
{
    private readonly object gate = new();
    private readonly Dictionary<ConversationKey, Entry> entries = [];
    internal DateTime UtcNow => clock.UtcNow;

    public async Task<Lease> AcquireAsync(ConversationKey key, CancellationToken ct = default)
    {
        Entry entry;
        lock (gate)
        {
            // Opportunistic cleanup cannot remove a state entry with active or waiting callers.
            foreach (var expired in entries.Where(pair => pair.Value.Users == 0 &&
                         pair.Value.State.ExpiresAtUtc <= clock.UtcNow).Select(pair => pair.Key).ToArray())
            {
                entries[expired].Semaphore.Dispose();
                entries.Remove(expired);
            }
            if (!entries.TryGetValue(key, out entry!))
                entries[key] = entry = new Entry(new ConversationState(clock.UtcNow));
            entry.Users++;
        }
        try { await entry.Semaphore.WaitAsync(ct); }
        catch { Release(entry, false); throw; }
        if (entry.State.ExpiresAtUtc <= clock.UtcNow) entry.State = new ConversationState(clock.UtcNow);
        return new Lease(this, entry);
    }

    private void Release(Entry entry, bool acquired)
    {
        lock (gate)
        {
            if (acquired) entry.Semaphore.Release();
            entry.Users--;
        }
    }

    internal sealed class Entry(ConversationState state)
    {
        public ConversationState State = state;
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    public sealed class Lease : IDisposable
    {
        private ConversationStateStore? owner;
        private readonly Entry entry;
        internal Lease(ConversationStateStore owner, Entry entry) { this.owner = owner; this.entry = entry; }
        public ConversationState State => entry.State;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(entry, true);
    }
}
