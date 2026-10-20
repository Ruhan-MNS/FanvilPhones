using Microsoft.AspNetCore.Components;

namespace FancilPhones.UI;

/// <summary>Owns work started by a page/dialog until navigation or disposal.</summary>
public abstract class AsyncComponentBase : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenRegistration requestAborted;
    private CancellationTokenRegistration stopping;
    private bool loading;
    protected AsyncComponentBase() => LifetimeToken = lifetime.Token;
    protected CancellationToken LifetimeToken { get; }
    protected bool IsDisposed { get; private set; }
    [CascadingParameter] private HttpContext? RequestContext { get; set; }
    [Inject] private IHostApplicationLifetime Application { get; set; } = default!;

    protected override void OnInitialized()
    {
        stopping = Application.ApplicationStopping.Register(() => lifetime.Cancel());
        if (RequestContext is not null)
            requestAborted = RequestContext.RequestAborted.Register(() => lifetime.Cancel());
    }

    protected bool TryBeginLoad()
    {
        if (loading || IsDisposed) return false;
        loading = true;
        return true;
    }

    protected void EndLoad() => loading = false;

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        requestAborted.Dispose();
        stopping.Dispose();
        lifetime.Cancel();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
