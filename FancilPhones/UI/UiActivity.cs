namespace FancilPhones.UI;

public enum UiActivityKind { Load, Action }
public sealed record UiWork(Guid Id, string Message, UiActivityKind Kind);

/// <summary>Circuit-scoped activity leases; overlapping work clears only its own indicator.</summary>
public sealed class UiActivity : IDisposable
{
    private readonly object gate = new();
    private readonly List<UiWork> work = [];
    private bool disposed;
    public event Action? Changed;
    public UiWork? Current { get { lock (gate) return work.LastOrDefault(); } }

    public IDisposable Begin(string message, CancellationToken ct = default, UiActivityKind kind = UiActivityKind.Action)
    {
        ct.ThrowIfCancellationRequested();
        var item = new UiWork(Guid.NewGuid(), message, kind);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            work.Add(item);
        }
        Changed?.Invoke();
        var lease = new Lease(this, item.Id);
        lease.Register(ct);
        return lease;
    }

    private void End(Guid id)
    {
        lock (gate) { if (disposed || work.RemoveAll(w => w.Id == id) == 0) return; }
        Changed?.Invoke();
    }

    public void Dispose() { lock (gate) { disposed = true; work.Clear(); } }

    private sealed class Lease(UiActivity owner, Guid id) : IDisposable
    {
        private int disposed;
        private CancellationTokenRegistration registration;
        public void Register(CancellationToken ct)
        {
            registration = ct.Register(Dispose);
            if (Volatile.Read(ref disposed) != 0) registration.Unregister();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            registration.Unregister();
            owner.End(id);
        }
    }
}
