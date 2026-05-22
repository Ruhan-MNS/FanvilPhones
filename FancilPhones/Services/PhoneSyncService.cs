using FancilPhones.Data;
using Microsoft.EntityFrameworkCore;

namespace FancilPhones.Services;

public record SyncResult(int PhoneId, string PhoneName, bool Success, int? HttpStatus, string Message);

public class PhoneSyncService
{
    private readonly IDbContextFactory<AppDbContext> _dbf;
    private readonly ILogger<PhoneSyncService> _log;

    public PhoneSyncService(IDbContextFactory<AppDbContext> dbf, ILogger<PhoneSyncService> log)
    {
        _dbf = dbf;
        _log = log;
    }

    public async Task<IReadOnlyList<SyncResult>> SyncAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var phones = await db.Phones.Where(p => p.Enabled).ToListAsync(ct);
        var contacts = await db.Contacts.OrderBy(c => c.DisplayName).ToListAsync(ct);
        var csv = PhonebookCsv.Build(contacts);

        var tasks = phones.Select(p => PushAsync(p, contacts.Count, csv, ct)).ToList();
        return await Task.WhenAll(tasks);
    }

    public async Task<SyncResult> SyncOneAsync(int phoneId, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var phone = await db.Phones.FindAsync(new object?[] { phoneId }, ct);
        if (phone is null) return new SyncResult(phoneId, "?", false, null, "Phone not found");
        var contacts = await db.Contacts.OrderBy(c => c.DisplayName).ToListAsync(ct);
        var csv = PhonebookCsv.Build(contacts);
        return await PushAsync(phone, contacts.Count, csv, ct);
    }

    private async Task<SyncResult> PushAsync(Phone phone, int contactCount, byte[] csv, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var run = new SyncRun
        {
            PhoneId = phone.Id,
            StartedAt = DateTime.UtcNow,
            ContactCount = contactCount,
            Status = "Running"
        };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            using var client = new FanvilHttpClient(phone.IpAddress, phone.Scheme == "https");

            // Step 1: nonce/MD5 login to obtain the session ('auth') cookie.
            var login = await client.LoginAsync(phone.Username, phone.Password, ct);
            if (!login.ok)
                throw new Exception(login.message);

            // Step 2: wipe the phone's existing phonebook then upload the master
            // CSV - the phone's import only merges, so a delete-all first makes the
            // sync a true mirror (deletions in the app propagate). Finally log out
            // so the phone releases its (limited) session slot.
            FanvilResponse resp;
            try
            {
                await client.DeleteAllContactsAsync(ct);
                resp = await client.PostFileAsync(
                    phone.UploadPath, phone.UploadFieldName, "phonebook.csv",
                    csv, "application/octet-stream", ct);
            }
            finally
            {
                await client.LogoutAsync(ct);
            }

            var ok = resp.StatusCode is >= 200 and < 400;
            var msg = ok
                ? $"Uploaded {contactCount} contacts (HTTP {resp.StatusCode})"
                : $"HTTP {resp.StatusCode}: {Truncate(resp.Body, 400)}";

            run.FinishedAt = DateTime.UtcNow;
            run.Status = ok ? "Success" : "Failed";
            run.HttpStatusCode = resp.StatusCode;
            run.Message = msg;

            phone.LastSyncedAt = run.FinishedAt;
            phone.LastSyncStatus = run.Status;
            phone.LastSyncMessage = msg;
            db.Phones.Update(phone);
            await db.SaveChangesAsync(ct);

            return new SyncResult(phone.Id, phone.Name, ok, resp.StatusCode, msg);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sync failed for {Phone} ({Ip})", phone.Name, phone.IpAddress);
            run.FinishedAt = DateTime.UtcNow;
            run.Status = "Error";
            run.Message = ex.Message;

            phone.LastSyncedAt = run.FinishedAt;
            phone.LastSyncStatus = "Error";
            phone.LastSyncMessage = ex.Message;
            db.Phones.Update(phone);
            await db.SaveChangesAsync(ct);

            return new SyncResult(phone.Id, phone.Name, false, null, ex.Message);
        }
    }

    public async Task<(bool reachable, int? status, string message)> ProbeAsync(
        Phone phone, CancellationToken ct = default)
    {
        try
        {
            using var client = new FanvilHttpClient(phone.IpAddress, phone.Scheme == "https");
            var (ok, msg) = await client.LoginAsync(phone.Username, phone.Password, ct);
            if (ok)
                await client.LogoutAsync(ct); // free the session slot
            return (ok, ok ? 200 : 401, ok ? "Reachable, login OK" : msg);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");
}
