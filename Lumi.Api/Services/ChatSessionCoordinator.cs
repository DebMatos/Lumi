namespace Lumi.Api.Services;

public sealed class ChatSessionCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SessionEntry> _sessions = [];

    public int TrackedSessionCount
    {
        get
        {
            lock (_gate) return _sessions.Count;
        }
    }

    public async ValueTask<IDisposable> AcquireAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        SessionEntry entry;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out entry!))
            {
                entry = new SessionEntry();
                _sessions.Add(sessionId, entry);
            }

            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            return new SessionLease(this, sessionId, entry);
        }
        catch
        {
            ReleaseReference(sessionId, entry);
            throw;
        }
    }

    private void Release(Guid sessionId, SessionEntry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(sessionId, entry);
    }

    private void ReleaseReference(Guid sessionId, SessionEntry entry)
    {
        lock (_gate)
        {
            entry.References--;
            if (entry.References != 0) return;

            if (_sessions.TryGetValue(sessionId, out var current) &&
                ReferenceEquals(current, entry))
            {
                _sessions.Remove(sessionId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class SessionEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class SessionLease(
        ChatSessionCoordinator coordinator,
        Guid sessionId,
        SessionEntry entry) : IDisposable
    {
        private ChatSessionCoordinator? _coordinator = coordinator;

        public void Dispose()
        {
            Interlocked.Exchange(ref _coordinator, null)
                ?.Release(sessionId, entry);
        }
    }
}
