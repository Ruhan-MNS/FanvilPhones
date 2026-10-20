using FancilPhones.UI;

namespace FancilPhones.Tests;

public sealed class UiActivityTests
{
    [Fact]
    public void CompletingOverlappingWorkPreservesTheRemainingIndicator()
    {
        using var activity = new UiActivity();
        using var first = activity.Begin("Loading fleet");
        using var second = activity.Begin("Saving contact");
        Assert.Equal("Saving contact", activity.Current!.Message);
        first.Dispose();
        Assert.Equal("Saving contact", activity.Current.Message);
        second.Dispose();
        Assert.Null(activity.Current);
    }

    [Fact]
    public void CancellationClearsOnlyTheWorkOwnedByThatToken()
    {
        using var activity = new UiActivity();
        using var surviving = activity.Begin("Saving contact");
        using var cancellation = new CancellationTokenSource();
        using var cancelled = activity.Begin("Delivering phonebook", cancellation.Token);
        cancellation.Cancel();
        Assert.Equal("Saving contact", activity.Current!.Message);
        cancelled.Dispose();
        Assert.Equal("Saving contact", activity.Current.Message);
    }

    [Fact]
    public void PreCancelledWorkNeverInstallsAnIndicator()
    {
        using var activity = new UiActivity();
        Assert.Throws<OperationCanceledException>(() => activity.Begin("Export", new CancellationToken(true)));
        Assert.Null(activity.Current);
    }
}
