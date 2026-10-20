namespace FancilPhones.UI;

public enum DialogChoice { Cancel, Primary, Secondary }
public record Notice(Guid Id, string Message, string Tone);
public record Prompt(string Title, string Message, string Primary, string? Secondary, bool Destructive);

/// <summary>Circuit-scoped feedback for Radzen dialogs and workspace notifications.</summary>
public sealed class UiFeedback : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Notice> notices = [];
    private TaskCompletionSource<DialogChoice>? pending;
    private bool disposed;
    public event Action? Changed;
    public IReadOnlyList<Notice> Notices => notices;
    public Prompt? ActivePrompt { get; private set; }

    public void Notify(string message, string tone = "info")
    {
        if (disposed) return;
        var id = Guid.NewGuid();
        notices.Add(new(id, message, tone));
        if (notices.Count > 4) notices.RemoveAt(0);
        Changed?.Invoke();
        if (tone is "success" or "info") _ = ExpireAsync(id);
    }

    private async Task ExpireAsync(Guid id)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(7), lifetime.Token); Dismiss(id); }
        catch (OperationCanceledException) { }
    }

    public void Dismiss(Guid id)
    {
        if (disposed) return;
        notices.RemoveAll(n => n.Id == id);
        Changed?.Invoke();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string action = "Continue", bool destructive = false, CancellationToken ct = default)
        => await ChooseAsync(title, message, action, destructive: destructive, ct: ct) == DialogChoice.Primary;

    public async Task<DialogChoice> ChooseAsync(string title, string message, string primary, string? secondary = null, bool destructive = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (disposed || pending is not null) return DialogChoice.Cancel;
        var completion = new TaskCompletionSource<DialogChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = completion;
        ActivePrompt = new(title, message, primary, secondary, destructive);
        Changed?.Invoke();
        try { return await completion.Task.WaitAsync(ct); }
        finally
        {
            if (ReferenceEquals(pending, completion)) Resolve(DialogChoice.Cancel);
        }
    }

    public void Resolve(DialogChoice choice)
    {
        var completion = pending;
        pending = null;
        ActivePrompt = null;
        Changed?.Invoke();
        completion?.TrySetResult(choice);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        pending?.TrySetResult(DialogChoice.Cancel);
        pending = null;
        ActivePrompt = null;
    }
}
