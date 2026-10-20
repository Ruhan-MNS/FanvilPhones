using Microsoft.Extensions.Hosting;

namespace FancilPhones.Tests;

internal sealed class TestApplicationLifetime(CancellationToken stopping = default) : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => stopping;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => throw new NotSupportedException();
}
