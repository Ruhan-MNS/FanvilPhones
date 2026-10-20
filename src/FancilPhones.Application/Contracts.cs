using FancilPhones.Domain;

namespace FancilPhones.Application;

public static class PhonePolicies
{
    public const string Administrator = "PhoneAdministrator";
}

public interface IUserAccess
{
    Task RequireAuthenticatedAsync(CancellationToken ct = default);
    Task RequireAdministratorAsync(CancellationToken ct = default);
}

public record FleetSummary(int PhoneCount, int ContactCount, DateTime? LastActivity, int EnabledPhoneCount = 0);
public record PhoneImportResult(int Added, int Updated);
public record SyncResult(int PhoneId, string PhoneName, bool Success, int? HttpStatus, string Message);
public record SyncProgress(int Completed, int Total, int Succeeded, int Failed, SyncResult? Latest = null);

public sealed class DuplicatePhoneException(string ipAddress, Exception innerException)
    : Exception($"A phone with IP {ipAddress} already exists.", innerException);

public interface IPhoneDirectory
{
    Task<List<Phone>> GetPhonesAsync(CancellationToken ct = default);
    Task<List<Contact>> GetContactsAsync(CancellationToken ct = default);
    Task<List<SyncRun>> GetHistoryAsync(int limit, CancellationToken ct = default, string? action = null);
    Task<FleetSummary> GetSummaryAsync(CancellationToken ct = default);
    Task SavePhoneAsync(Phone phone, CancellationToken ct = default);
    Task SetPhoneEnabledAsync(int id, bool enabled, CancellationToken ct = default);
    Task DeletePhoneAsync(int id, CancellationToken ct = default);
    Task<PhoneImportResult> ImportPhonesAsync(IReadOnlyList<Phone> phones, bool replace, CancellationToken ct = default);
    Task SaveContactAsync(Contact contact, CancellationToken ct = default);
    Task DeleteContactAsync(int id, CancellationToken ct = default);
    Task ReplaceContactsAsync(IReadOnlyList<Contact> contacts, CancellationToken ct = default);
}

public interface IPhoneSyncService
{
    Task<IReadOnlyList<SyncResult>> SyncAllAsync(CancellationToken ct = default, IProgress<SyncProgress>? progress = null);
    Task<SyncResult> SyncOneAsync(int phoneId, CancellationToken ct = default);
    Task<(bool ok, string message)> PushSipDisplayNameAsync(int phoneId, CancellationToken ct = default);
    Task<(bool reachable, int? status, string message)> ProbeAsync(Phone phone, CancellationToken ct = default);
}

public sealed record DiscoveredPhone(
    string Ip,
    bool IsFanvil,
    bool AlreadyKnown,
    string Detail);

public interface IPhoneDiscoveryService
{
    IAsyncEnumerable<DiscoveredPhone> ScanAsync(string cidrOrRange, HashSet<string> knownIps,
        int maxConcurrency = 32, TimeSpan? tcpTimeout = null, TimeSpan? httpTimeout = null,
        CancellationToken ct = default);
}
