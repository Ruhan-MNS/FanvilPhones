namespace FancilPhones.Infrastructure.Services;

public interface IFanvilClient : IDisposable
{
    Task<(bool ok, string message)> LoginAsync(string username, string password, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task<FanvilResponse> GetAsync(string path, CancellationToken ct = default);
    Task<FanvilResponse> DeleteAllContactsAsync(CancellationToken ct = default);
    Task<FanvilResponse> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default);
    Task<FanvilResponse> PostFileAsync(string path, string fieldName, string fileName, byte[] fileBytes, string fileContentType, CancellationToken ct = default);
}

public interface IFanvilClientFactory
{
    IFanvilClient Create(string host, bool useTls, TimeSpan? connectTimeout = null);
}

public sealed class FanvilClientFactory : IFanvilClientFactory
{
    public IFanvilClient Create(string host, bool useTls, TimeSpan? connectTimeout = null) => new FanvilHttpClient(host, useTls, connectTimeout: connectTimeout);
}
