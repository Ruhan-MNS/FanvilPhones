using FancilPhones.Infrastructure.Services;

namespace FancilPhones.Tests;

internal sealed class ControlledFanvilFactory : IFanvilClientFactory
{
    public Func<string, CancellationToken, Task<(bool, string)>>? Login { get; init; }
    public Func<string, CancellationToken, Task<FanvilResponse>>? Get { get; init; }
    public Func<string, CancellationToken, Task<FanvilResponse>>? Upload { get; init; }
    public Func<CancellationToken, Task>? Logout { get; init; }
    private int created, disposed, loggedOut;
    public int Created => Volatile.Read(ref created);
    public int Disposed => Volatile.Read(ref disposed);
    public int LoggedOut => Volatile.Read(ref loggedOut);
    public IFanvilClient Create(string host, bool useTls, TimeSpan? connectTimeout = null)
    {
        Interlocked.Increment(ref created);
        return new Client(this, host);
    }

    private sealed class Client(ControlledFanvilFactory owner, string host) : IFanvilClient
    {
        public Task<(bool ok, string message)> LoginAsync(string username, string password, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return owner.Login?.Invoke(host, ct) ?? Task.FromResult((true, "OK"));
        }
        public async Task LogoutAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref owner.loggedOut);
            if (owner.Logout is not null) await owner.Logout(ct);
        }
        public Task<FanvilResponse> GetAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return owner.Get?.Invoke(host, ct) ?? Task.FromResult(new FanvilResponse { StatusCode = 200, Body = "Fanvil" });
        }
        public Task<FanvilResponse> DeleteAllContactsAsync(CancellationToken ct = default) => Response(ct);
        public Task<FanvilResponse> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default) => Response(ct);
        public Task<FanvilResponse> PostFileAsync(string path, string fieldName, string fileName, byte[] fileBytes, string fileContentType, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return owner.Upload?.Invoke(host, ct) ?? Response(ct);
        }
        private static Task<FanvilResponse> Response(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new FanvilResponse { StatusCode = 200 });
        }
        public void Dispose() => Interlocked.Increment(ref owner.disposed);
    }
}
