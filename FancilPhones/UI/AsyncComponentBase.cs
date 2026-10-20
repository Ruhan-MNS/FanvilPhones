using Microsoft.AspNetCore.Components;

namespace FancilPhones.UI;

/// <summary>Owns work started by a page/dialog until navigation or disposal.</summary>
public abstract class AsyncComponentBase : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenRegistration requestAborted;
    private CancellationTokenRegistration stopping;
    private bool loading;
    private IDisposable? loadActivity;
    protected AsyncComponentBase() => LifetimeToken = lifetime.Token;
    protected CancellationToken LifetimeToken { get; }
    protected bool IsDisposed { get; private set; }
    [CascadingParameter] private HttpContext? RequestContext { get; set; }
    [Inject] private IHostApplicationLifetime Application { get; set; } = default!;
    [Inject] protected UiActivity Activity { get; set; } = default!;

    protected override void OnInitialized()
    {
        stopping = Application.ApplicationStopping.Register(() => lifetime.Cancel());
        if (RequestContext is not null)
            requestAborted = RequestContext.RequestAborted.Register(() => lifetime.Cancel());
    }

    protected bool TryBeginLoad(string message = "Loading workspace…")
    {
        if (loading || IsDisposed || LifetimeToken.IsCancellationRequested) return false;
        loading = true;
        try { loadActivity = Activity.Begin(message, LifetimeToken, UiActivityKind.Load); }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested) { loading = false; return false; }
        return true;
    }

    protected void EndLoad() { loading = false; loadActivity?.Dispose(); loadActivity = null; }
    protected IDisposable BeginActivity(string message) => Activity.Begin(message, LifetimeToken);

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        EndLoad();
        requestAborted.Dispose();
        stopping.Dispose();
        lifetime.Cancel();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
