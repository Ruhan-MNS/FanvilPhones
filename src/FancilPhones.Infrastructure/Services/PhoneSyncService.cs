using System.Collections.Concurrent;
using FancilPhones.Domain;
using FancilPhones.Infrastructure.Data;
using FancilPhones.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace FancilPhones.Infrastructure.Services;

public sealed class PhoneSyncService(
    IDbContextFactory<AppDbContext> dbf,
    ILogger<PhoneSyncService> logger,
    IUserAccess access,
    IFanvilClientFactory clients,
    IOptions<PhoneOperationOptions> options,
    IHostApplicationLifetime application) : IPhoneSyncService
{
    private PhoneOperationOptions Settings => options.Value;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> HandsetLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly FanvilSipAdapter sip = new();
    private sealed record HandsetResult(bool Success, int? HttpStatus, string Message);

    // A SIP push or connection probe must not change phonebook retry eligibility.
    private static IQueryable<SyncRun> LatestDeliveries(AppDbContext db) => db.SyncRuns.AsNoTracking()
        .Where(r => r.Action == "Sync" && !db.SyncRuns.Any(next => next.PhoneId == r.PhoneId && next.Action == "Sync" && next.Id > r.Id));

    public async Task<IReadOnlyList<PhoneSyncStatus>> GetStatusAsync(CancellationToken ct = default)
    {
        await access.RequireAuthenticatedAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var phones = await db.Phones.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.IpAddress, p.Enabled }).ToListAsync(ct);
        var latest = await LatestDeliveries(db).ToDictionaryAsync(r => r.PhoneId, ct);
        return phones.Select(p =>
        {
            latest.TryGetValue(p.Id, out var run);
            return new PhoneSyncStatus(p.Id, p.Name, p.IpAddress, p.Enabled, run?.Status ?? "Pending", run?.StartedAt, run?.Message);
        }).ToList();
    }

    public Task<IReadOnlyList<SyncResult>> SyncAllAsync(CancellationToken ct = default, IProgress<SyncProgress>? progress = null) =>
        SyncBatchAsync(null, false, ct, progress);

    public Task<IReadOnlyList<SyncResult>> SyncFailedAsync(CancellationToken ct = default, IProgress<SyncProgress>? progress = null) =>
        SyncBatchAsync(null, true, ct, progress);

    public Task<IReadOnlyList<SyncResult>> SyncSelectedAsync(IReadOnlyCollection<int> phoneIds, CancellationToken ct = default, IProgress<SyncProgress>? progress = null) =>
        SyncBatchAsync(phoneIds?.Distinct().ToArray() ?? throw new ArgumentNullException(nameof(phoneIds)), false, ct, progress);

    private async Task<IReadOnlyList<SyncResult>> SyncBatchAsync(IReadOnlyCollection<int>? phoneIds, bool failedOnly,
        CancellationToken ct, IProgress<SyncProgress>? progress)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        ct = stopping.Token;
        await access.RequireAdministratorAsync(ct);
        List<Phone> phones;
        List<Contact> contacts;
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            var query = db.Phones.AsNoTracking().Where(p => p.Enabled);
            if (phoneIds is not null)
            {
                if (phoneIds.Count == 0) throw new PhoneSyncValidationException("Select at least one phone to sync.");
                query = query.Where(p => phoneIds.Contains(p.Id));
            }
            if (failedOnly)
            {
                var failed = LatestDeliveries(db).Where(r => r.Status == "Failed" || r.Status == "Error" || r.Status == "TimedOut" || r.Status == "Cancelled");
                query = query.Where(p => failed.Any(r => r.PhoneId == p.Id));
            }
            phones = await query.OrderBy(p => p.Id).ToListAsync(ct);
            if (phoneIds is not null && phones.Count != phoneIds.Count)
                throw new PhoneSyncValidationException("A selected phone was removed or disabled. Refresh delivery status and try again.");
            contacts = await db.Contacts.AsNoTracking().OrderBy(c => c.DisplayName).ToListAsync(ct);
        }
        var csv = PhonebookCsv.Build(contacts, ct);
        using var scope = logger.BeginScope("FleetSyncId:{FleetSyncId}", Guid.NewGuid());
        logger.LogInformation("Fleet synchronization started for {PhoneCount} phones with {ContactCount} contacts.", phones.Count, contacts.Count);
        var results = new SyncResult[phones.Count];
        var progressGate = new object();
        var completed = 0;
        var succeeded = 0;
        progress?.Report(new(0, phones.Count, 0, 0));
        await Parallel.ForEachAsync(Enumerable.Range(0, phones.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = Settings.MaxConcurrentSyncs,
            CancellationToken = ct
        }, async (index, token) =>
        {
            var result = await PushAsync(phones[index], contacts.Count, csv, token);
            results[index] = result;
            lock (progressGate)
            {
                completed++;
                if (result.Success) succeeded++;
                progress?.Report(new(completed, phones.Count, succeeded, completed - succeeded, result));
            }
        });
        logger.LogInformation("Fleet synchronization completed. Successful: {SuccessCount}; failed: {FailureCount}.",
            results.Count(r => r.Success), results.Count(r => !r.Success));
        return results;
    }

    public async Task<SyncResult> SyncOneAsync(int phoneId, CancellationToken ct = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        ct = stopping.Token;
        await access.RequireAdministratorAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var phone = await db.Phones.AsNoTracking().SingleOrDefaultAsync(p => p.Id == phoneId, ct);
        if (phone is null) return new(phoneId, "?", false, null, "Phone not found");
        var contacts = await db.Contacts.AsNoTracking().OrderBy(c => c.DisplayName).ToListAsync(ct);
        return await PushAsync(phone, contacts.Count, PhonebookCsv.Build(contacts, ct), ct);
    }

    private async Task<SyncResult> PushAsync(Phone phone, int count, byte[] csv, CancellationToken ct)
    {
        var result = await RunHandsetAsync(phone, "Sync", count, async (client, token, stage) =>
        {
            stage("clearing the phonebook");
            var deleted = await client.DeleteAllContactsAsync(token);
            if (deleted.StatusCode is < 200 or >= 400)
                return new(false, deleted.StatusCode, "The handset rejected clearing its phonebook; upload was not attempted.");
            stage("uploading the phonebook");
            var response = await client.PostFileAsync(phone.UploadPath, phone.UploadFieldName, "phonebook.csv", csv, "application/octet-stream", token);
            var ok = response.StatusCode is >= 200 and < 400;
            return new(ok, response.StatusCode, ok ? $"Uploaded {count} contacts (HTTP {response.StatusCode})" : $"HTTP {response.StatusCode}: {Truncate(response.Body, 400)}");
        }, ct);
        return new(phone.Id, phone.Name, result.Success, result.HttpStatus, result.Message);
    }

    public async Task<(bool ok, string message)> PushSipDisplayNameAsync(int phoneId, CancellationToken ct = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        ct = stopping.Token;
        await access.RequireAdministratorAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var phone = await db.Phones.AsNoTracking().SingleOrDefaultAsync(p => p.Id == phoneId, ct);
        if (phone is null) return (false, "Phone not found");
        if (string.IsNullOrWhiteSpace(phone.SipDisplayName) && string.IsNullOrWhiteSpace(phone.SipExtension))
            return (false, "No SIP display name or extension configured for this phone.");
        var result = await RunHandsetAsync(phone, "Push", 0, async (client, token, stage) =>
        {
            stage("reading SIP settings");
            var before = await sip.ReadAsync(client, phone.SipLineIndex, token);
            var desired = before.Settings.Copy();
            if (!string.IsNullOrWhiteSpace(phone.SipDisplayName)) desired.DisplayName = phone.SipDisplayName.Trim();
            if (!string.IsNullOrWhiteSpace(phone.SipExtension)) desired.Username = phone.SipExtension.Trim();
            desired.RegistrationEnabled = phone.SipRegistrationEnabled;
            stage("applying and verifying SIP settings");
            var applied = await sip.ApplyAsync(client, phone.SipLineIndex, before.Fingerprint, desired, token);
            return new(applied.ok, null, applied.message);
        }, ct);
        return (result.Success, result.Message);
    }

    public async Task<SipSettingsSnapshot> ReadSipSettingsAsync(int phoneId, int lineIndex, CancellationToken ct = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        ct = stopping.Token;
        await access.RequireAdministratorAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var phone = await db.Phones.AsNoTracking().SingleOrDefaultAsync(p => p.Id == phoneId, ct)
            ?? throw new InvalidOperationException("Phone not found.");
        SipSettingsSnapshot? snapshot = null;
        var result = await RunHandsetAsync(phone, "SipRead", 0, async (client, token, stage) =>
        {
            stage("reading SIP settings");
            var capture = await sip.ReadAsync(client, lineIndex, token);
            snapshot = new(phoneId, lineIndex, capture.Fingerprint, await sip.ReadStatusAsync(client, token), capture.Settings);
            return new(true, 200, "SIP settings read; no configuration changes requested.");
        }, ct);
        if (!result.Success || snapshot is null) throw new InvalidOperationException(result.Message);
        return snapshot;
    }

    public async Task<(bool ok, string message)> PushSipSettingsAsync(SipSettingsSnapshot baseline, SipLineSettings settings, CancellationToken ct = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        ct = stopping.Token;
        await access.RequireAdministratorAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var phone = await db.Phones.AsNoTracking().SingleOrDefaultAsync(p => p.Id == baseline.PhoneId, ct);
        if (phone is null) return (false, "Phone not found.");
        var result = await RunHandsetAsync(phone, "Push", 0, async (client, token, stage) =>
        {
            stage("applying and verifying SIP settings");
            var applied = await sip.ApplyAsync(client, baseline.LineIndex, baseline.Fingerprint, settings, token);
            if (applied.ok)
            {
                // The handset accepts 39 characters; the legacy fleet reference holds 32.
                // Preserve a longer handset identity without truncating it or failing after Apply.
                await db.Phones.Where(p => p.Id == phone.Id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.SipDisplayName, settings.DisplayName)
                    .SetProperty(p => p.SipExtension, p => settings.Username.Length <= 32 ? settings.Username : p.SipExtension)
                    .SetProperty(p => p.SipLineIndex, baseline.LineIndex)
                    .SetProperty(p => p.SipRegistrationEnabled, settings.RegistrationEnabled), token);
            }
            return new(applied.ok, null, applied.message);
        }, ct);
        return (result.Success, result.Message);
    }

    public async Task<(bool reachable, int? status, string message)> ProbeAsync(Phone phone, CancellationToken ct = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        ct = stopping.Token;
        await access.RequireAuthenticatedAsync(ct);
        var result = await RunHandsetAsync(phone, "Probe", 0,
            (_, _, _) => Task.FromResult(new HandsetResult(true, 200, "Reachable, login OK")), ct);
        return (result.Success, result.HttpStatus, result.Message);
    }

    // Each operation owns its timeout and client. Cleanup gets a separate, short token
    // so caller cancellation cannot strand sessions or leave history marked Running.
    private async Task<HandsetResult> RunHandsetAsync(Phone phone, string action, int contactCount,
        Func<IFanvilClient, CancellationToken, Action<string>, Task<HandsetResult>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var handsetLock = HandsetLocks.GetOrAdd(phone.IpAddress, _ => new SemaphoreSlim(1, 1));
        await handsetLock.WaitAsync(ct);
        using var handsetLease = new HandsetLease(handsetLock);
        var run = new SyncRun { PhoneId = phone.Id, Action = action, Status = "Running", ContactCount = contactCount };
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            db.SyncRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }
        using var scope = logger.BeginScope("PhoneId:{PhoneId} SyncRunId:{SyncRunId} Operation:{Operation}", phone.Id, run.Id, action);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Settings.TimeoutSeconds));
        IFanvilClient? client = null;
        HandsetResult result;
        string status;
        bool canceled = false;
        var stage = "signing in";
        void SetStage(string value) { stage = value; logger.LogDebug("Handset operation stage: {Stage}.", stage); }
        string StateHint() => stage is "signing in" or "reading SIP settings" ? "No phonebook or SIP changes were requested."
            : stage == "clearing the phonebook" ? "The phonebook may have been cleared; the upload was not started."
            : "The handset may have applied changes; verify its state before retrying.";
        logger.LogInformation("Handset operation started.");
        try
        {
            client = clients.Create(phone.IpAddress, phone.Scheme == "https");
            var login = await client.LoginAsync(phone.Username, phone.Password, timeout.Token);
            result = login.ok ? await operation(client, timeout.Token, SetStage) : new(false, 401, login.message);
            timeout.Token.ThrowIfCancellationRequested();
            status = result.Success ? "Success" : "Failed";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            canceled = true;
            status = "Cancelled";
            result = new(false, null, $"Cancelled while {stage}. {StateHint()}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            status = "TimedOut";
            logger.LogWarning("Handset operation deadline expired while {Stage}.", stage);
            result = new(false, null, $"Timed out while {stage}. {StateHint()}");
        }
        catch (TimeoutException ex)
        {
            status = "TimedOut";
            logger.LogWarning(ex, "Handset response timed out while {Stage}.", stage);
            result = new(false, null, $"Timed out waiting for a response while {stage}. {StateHint()}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Handset operation failed while {Stage}.", stage);
            status = "Error";
            result = new(false, null, ex.Message);
        }
        finally
        {
            if (client is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.CleanupTimeoutSeconds));
                try { await client.LogoutAsync(cleanup.Token); }
                catch (Exception ex) { logger.LogDebug(ex, "Best-effort handset logout did not complete."); }
                finally { client.Dispose(); }
            }
        }

        using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.CleanupTimeoutSeconds));
        try { await FinishRunAsync(run.Id, phone.Id, status, result, action is "Sync" or "Push", persist.Token); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not persist the handset operation result {Status}.", status);
            if (!canceled) throw;
        }
        logger.Log(canceled || result.Success ? LogLevel.Information : LogLevel.Warning,
            "Handset operation finished with {Status}; HTTP {HttpStatus}.", status, result.HttpStatus);
        if (canceled) throw new OperationCanceledException(ct);
        return result;
    }

    private async Task FinishRunAsync(int runId, int phoneId, string status, HandsetResult result, bool updatePhone, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var finished = DateTime.UtcNow;
        await db.SyncRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(setters => setters
            .SetProperty(r => r.FinishedAt, finished).SetProperty(r => r.Status, status)
            .SetProperty(r => r.HttpStatusCode, result.HttpStatus).SetProperty(r => r.Message, result.Message), ct);
        if (updatePhone)
            await db.Phones.Where(p => p.Id == phoneId).ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.LastSyncedAt, finished).SetProperty(p => p.LastSyncStatus, status)
                .SetProperty(p => p.LastSyncMessage, result.Message), ct);
        await transaction.CommitAsync(ct);
    }

    private sealed class HandsetLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");
}
