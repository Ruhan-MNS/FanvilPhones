using FancilPhones.Application;
using FancilPhones.Domain;
using FancilPhones.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FancilPhones.Infrastructure.Services;

public sealed class PhoneDirectory(IDbContextFactory<AppDbContext> factory, IUserAccess access, ILogger<PhoneDirectory> logger) : IPhoneDirectory
{
    public async Task<List<Phone>> GetPhonesAsync(CancellationToken ct = default)
    {
        await access.RequireAuthenticatedAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Phones.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<List<Contact>> GetContactsAsync(CancellationToken ct = default)
    {
        await access.RequireAuthenticatedAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Contacts.AsNoTracking().OrderBy(c => c.DisplayName).ToListAsync(ct);
    }

    public async Task<List<SyncRun>> GetHistoryAsync(int limit, CancellationToken ct = default)
    {
        await access.RequireAuthenticatedAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SyncRuns.AsNoTracking().Include(r => r.Phone)
            .OrderByDescending(r => r.StartedAt).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
    }

    public async Task<FleetSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await access.RequireAuthenticatedAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        return new(await db.Phones.CountAsync(ct), await db.Contacts.CountAsync(ct),
            await db.SyncRuns.MaxAsync(r => (DateTime?)r.StartedAt, ct));
    }

    public async Task SavePhoneAsync(Phone phone, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        var target = phone.Id == 0 ? new Phone() : await db.Phones.FindAsync([phone.Id], ct)
            ?? throw new InvalidOperationException("Phone no longer exists.");
        CopyPhoneSettings(phone, target);
        if (phone.Id == 0) db.Phones.Add(target);
        await SavePhoneChangesAsync(db, phone.IpAddress, ct);
        phone.Id = target.Id;
        logger.LogInformation("Phone settings saved for {PhoneId}.", phone.Id);
    }

    public async Task SetPhoneEnabledAsync(int id, bool enabled, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        var phone = await db.Phones.FindAsync([id], ct) ?? throw new InvalidOperationException("Phone no longer exists.");
        phone.Enabled = enabled;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Phone {PhoneId} fleet synchronization enabled: {Enabled}.", id, enabled);
    }

    public async Task DeletePhoneAsync(int id, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        var phone = await db.Phones.FindAsync([id], ct);
        if (phone is null) return;
        db.Phones.Remove(phone);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Phone {PhoneId} and its activity history deleted.", id);
    }

    public async Task<PhoneImportResult> ImportPhonesAsync(IReadOnlyList<Phone> phones, bool replace, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (replace)
        {
            db.Phones.RemoveRange(await db.Phones.ToListAsync(ct));
            await db.SaveChangesAsync(ct);
        }
        var byIp = (await db.Phones.ToListAsync(ct)).ToDictionary(p => p.IpAddress, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var updated = 0;
        foreach (var phone in phones)
        {
            if (!byIp.TryGetValue(phone.IpAddress, out var target))
            {
                target = new Phone();
                db.Phones.Add(target);
                byIp[phone.IpAddress] = target;
                added++;
            }
            else updated++;
            CopyPhoneSettings(phone, target);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Phone import committed. Replace: {Replace}; added: {AddedCount}; updated: {UpdatedCount}.", replace, added, updated);
        return new(added, updated);
    }

    public async Task SaveContactAsync(Contact contact, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        var target = contact.Id == 0 ? new Contact() : await db.Contacts.FindAsync([contact.Id], ct)
            ?? throw new InvalidOperationException("Contact no longer exists.");
        target.DisplayName = contact.DisplayName;
        target.OfficeNumber = contact.OfficeNumber;
        target.MobileNumber = contact.MobileNumber;
        target.OtherNumber = contact.OtherNumber;
        target.Group = contact.Group;
        target.Ring = contact.Ring;
        target.UpdatedAt = DateTime.UtcNow;
        if (contact.Id == 0) db.Contacts.Add(target);
        await db.SaveChangesAsync(ct);
        contact.Id = target.Id;
        logger.LogInformation("Contact {ContactId} saved.", contact.Id);
    }

    public async Task DeleteContactAsync(int id, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        var contact = await db.Contacts.FindAsync([id], ct);
        if (contact is null) return;
        db.Contacts.Remove(contact);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Contact {ContactId} deleted.", id);
    }

    public async Task ReplaceContactsAsync(IReadOnlyList<Contact> contacts, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Contacts.RemoveRange(await db.Contacts.ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        db.Contacts.AddRange(contacts);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Phonebook replacement committed with {ContactCount} contacts.", contacts.Count);
    }

    private static void CopyPhoneSettings(Phone source, Phone target)
    {
        target.Name = source.Name;
        target.IpAddress = source.IpAddress;
        target.Scheme = source.Scheme;
        target.Username = source.Username;
        target.Password = source.Password;
        target.UploadPath = source.UploadPath;
        target.UploadFieldName = source.UploadFieldName;
        target.Enabled = source.Enabled;
        target.SipDisplayName = source.SipDisplayName;
        target.SipLineIndex = source.SipLineIndex;
        target.SipExtension = source.SipExtension;
        target.SipRegistrationEnabled = source.SipRegistrationEnabled;
    }

    private static async Task SavePhoneChangesAsync(AppDbContext db, string ip, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicatePhoneException(ip, ex);
        }
    }
}
