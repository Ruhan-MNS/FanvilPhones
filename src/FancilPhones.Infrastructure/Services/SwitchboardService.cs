using System.Text.Json;
using FancilPhones.Application;
using FancilPhones.Domain;
using FancilPhones.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FancilPhones.Infrastructure.Services;

public sealed class SwitchboardService(IDbContextFactory<AppDbContext> factory, IUserAccess access,
    IFanvilClientFactory clients, FanvilSwitchboardAdapter adapter, IOptions<PhoneOperationOptions> options,
    IHostApplicationLifetime application, ILogger<SwitchboardService> logger) : ISwitchboardService
{
    public async Task<SwitchboardState> GetStateAsync(CancellationToken ct = default)
    {
        await access.RequireAuthenticatedAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var phones = await db.Phones.AsNoTracking().Where(p => db.SwitchboardProfiles.Any(s => s.PhoneId == p.Id))
            .OrderBy(p => p.IpAddress).Select(p => new SwitchboardPhone(p.Id, p.Name, p.IpAddress,
                db.SwitchboardProfiles.Where(s => s.PhoneId == p.Id).Select(s => (DateTime?)s.CapturedAt).FirstOrDefault(), null)).ToListAsync(ct);
        var profiles = await db.SwitchboardProfiles.AsNoTracking().ToListAsync(ct);
        phones = phones.Select(p =>
        {
            var captured = profiles.SingleOrDefault(c => c.PhoneId == p.Id);
            if (captured is null) return p;
            try { SwitchboardRules.ValidateGeometry(p.IpAddress, SwitchboardLayout.FromJson(captured.BaselineJson)); return p with { HasDraft = captured.DraftJson is not null }; }
            catch (InvalidOperationException ex) { return p with { ConfigurationWarning = ex.Message, HasDraft = captured.DraftJson is not null }; }
        }).ToList();
        // Snapshot contents are loaded only through a specific preview, not the history listing.
        var history = await db.SwitchboardDeployments.AsNoTracking().OrderByDescending(p => p.CreatedAt).Take(30)
            .Select(p => new SwitchboardDeployment { Id = p.Id, PhoneId = p.PhoneId, IpAddress = p.IpAddress,
                Status = p.Status, Purpose = p.Purpose, Message = p.Message, CreatedAt = p.CreatedAt, FinishedAt = p.FinishedAt }).ToListAsync(ct);
        return new(phones, await db.Contacts.AsNoTracking().OrderBy(c => c.DisplayName).ToListAsync(ct),
            await db.SwitchboardMembers.AsNoTracking().Include(m => m.Contact).ToListAsync(ct),
            await db.SwitchboardOverrides.AsNoTracking().ToListAsync(ct), history)
        { FleetPhones = await db.Phones.AsNoTracking().OrderBy(p => p.Name).Select(p => new SwitchboardFleetPhone(p.Id, p.Name, p.IpAddress, p.Enabled)).ToListAsync(ct) };
    }

    public async Task CaptureAsync(int phoneId, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var phone = await PhoneAsync(db, phoneId, ct);
        var profile = await db.SwitchboardProfiles.SingleOrDefaultAsync(p => p.PhoneId == phoneId, ct);
        EnsureIdle(profile);
        SwitchboardLayout layout;
        try { layout = await OnPhoneAsync(phone, async (client, token) => await adapter.ReadAsync(client, token), ct); }
        catch (SwitchboardCompatibilityException ex)
        {
            logger.LogInformation("PhoneId {PhoneId} did not expose a compatible switchboard layout. No profile or keys were changed.", phone.Id);
            var message = profile is null ? $"{phone.Name} ({phone.IpAddress}) was not added."
                : $"Could not read keys from {phone.Name} ({phone.IpAddress}). Its saved layout was kept.";
            throw new SwitchboardCompatibilityException($"{message} {ex.Message}");
        }
        SwitchboardRules.ValidateGeometry(phone.IpAddress, layout);
        if (profile is null)
        {
            db.SwitchboardProfiles.Add(new() { PhoneId = phoneId, BaselineJson = layout.ToJson(), CapturedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
        }
        else
        {
            var now = DateTime.UtcNow;
            if (await db.SwitchboardProfiles.Where(p => p.PhoneId == phoneId && p.Revision == profile.Revision
                && (p.LeaseUntil == null || p.LeaseUntil < now)).ExecuteUpdateAsync(s => s.SetProperty(p => p.BaselineJson, layout.ToJson())
                .SetProperty(p => p.DraftJson, (string?)null).SetProperty(p => p.DraftRevision, Guid.NewGuid())
                .SetProperty(p => p.CapturedAt, now).SetProperty(p => p.Revision, Guid.NewGuid()), ct) != 1)
                throw new InvalidOperationException("The profile changed or a deployment started during capture. Try again after it finishes.");
        }
        logger.LogInformation("Captured switchboard keys for PhoneId {PhoneId}.", phoneId);
    }

    public async Task RemovePhoneAsync(int phoneId, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var profile = await ProfileAsync(db, phoneId, ct);
        EnsureIdle(profile);
        var now = DateTime.UtcNow;
        if (await db.SwitchboardProfiles.Where(p => p.PhoneId == phoneId && p.Revision == profile.Revision && (p.LeaseUntil == null || p.LeaseUntil < now)).ExecuteDeleteAsync(ct) != 1)
            throw new SwitchboardValidationException("This phone changed or a sync started. Refresh and try again.");
        // Fleet membership, physical keys and audited deployments are retained.
    }

    public async Task<SwitchboardEditor> GetEditorAsync(int phoneId, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        await PhoneAsync(db, phoneId, ct);
        return Editor(await ProfileAsync(db, phoneId, ct));
    }

    private static SwitchboardEditor Editor(SwitchboardProfile profile) => new(profile.PhoneId, profile.Revision,
        SwitchboardLayout.FromJson(profile.BaselineJson), SwitchboardLayout.FromJson(profile.DraftJson ?? profile.BaselineJson), profile.SortAlphabetically);

    public async Task<SwitchboardEditor> SaveDraftAsync(int phoneId, Guid revision, SwitchboardLayout layout, bool sortAlphabetically = true, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var phone = await PhoneAsync(db, phoneId, ct);
        var profile = await ProfileAsync(db, phoneId, ct); EnsureIdle(profile);
        SwitchboardEditing.ValidateDraft(phone.IpAddress, SwitchboardLayout.FromJson(profile.BaselineJson), layout);
        var next = Guid.NewGuid(); var now = DateTime.UtcNow;
        if (await db.SwitchboardProfiles.Where(p => p.PhoneId == phoneId && p.Revision == revision && (p.LeaseUntil == null || p.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DraftJson, layout.ToJson()).SetProperty(p => p.Revision, next).SetProperty(p => p.SortAlphabetically, sortAlphabetically)
                .SetProperty(p => p.DraftRevision, Guid.NewGuid()), ct) != 1)
            throw new SwitchboardValidationException("This layout was changed by another session. Reload the editor before saving.");
        logger.LogInformation("Saved function-key draft for PhoneId {PhoneId}.", phoneId);
        return new(phoneId, next, SwitchboardLayout.FromJson(profile.BaselineJson), SwitchboardEditing.Clone(layout), sortAlphabetically);
    }

    public async Task<SwitchboardDeployment> PreviewDraftAsync(int sourcePhoneId, int targetPhoneId, bool keepTargetNumbers, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var sourcePhone = await PhoneAsync(db, sourcePhoneId, ct);
        var source = await ProfileAsync(db, sourcePhoneId, ct); EnsureIdle(source);
        var draft = SwitchboardLayout.FromJson(source.DraftJson ?? source.BaselineJson);
        SwitchboardEditing.ValidateDraft(sourcePhone.IpAddress, SwitchboardLayout.FromJson(source.BaselineJson), draft);
        var phone = await PhoneAsync(db, targetPhoneId, ct);
        var profile = await ProfileAsync(db, targetPhoneId, ct); EnsureIdle(profile);
        var baseline = SwitchboardLayout.FromJson(profile.BaselineJson);
        var current = await OnPhoneAsync(phone, (client, token) => adapter.ReadAsync(client, token), ct);
        if (current.Fingerprint() != baseline.Fingerprint())
            throw new SwitchboardValidationException($"{phone.Name} changed since it was read. Read its keys again before syncing.");
        var target = sourcePhoneId == targetPhoneId ? SwitchboardEditing.Clone(draft) : SwitchboardEditing.Copy(draft, baseline, keepTargetNumbers, SwitchboardEditing.PhoneLine(SwitchboardLayout.FromJson(source.BaselineJson)));
        SwitchboardEditing.ValidateDraft(phone.IpAddress, baseline, target);
        SwitchboardRules.ValidateGeometry(phone.IpAddress, target);
        var preview = new SwitchboardDeployment { PhoneId = phone.Id, IpAddress = phone.IpAddress, ProfileRevision = profile.Revision,
            SourcePhoneId = sourcePhoneId, SourceRevision = source.DraftRevision, Purpose = "CustomLayout",
            BeforeJson = current.ToJson(), TargetJson = target.ToJson(), Message = "Five-page layout preview ready" };
        db.SwitchboardDeployments.Add(preview); await db.SaveChangesAsync(ct);
        return preview;
    }

    public async Task ImportContactsAsync(int phoneId, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        await PhoneAsync(db, phoneId, ct);
        var profile = await ProfileAsync(db, phoneId, ct);
        EnsureIdle(profile);
        var layout = SwitchboardLayout.FromJson(profile.BaselineJson);
        var contacts = await db.Contacts.ToListAsync(ct);
        var members = await db.SwitchboardMembers.ToListAsync(ct);
        var overrides = await db.SwitchboardOverrides.Where(o => o.PhoneId == phoneId).ToListAsync(ct);
        foreach (var key in layout.Keys.Where(k => !SwitchboardRules.LegacyDirectoryReserved(k) && k.Name.Length > 0 && k.Extension.Length > 0))
        {
            var matches = contacts.Where(c => SwitchboardRules.NameKey(c.DisplayName) == SwitchboardRules.NameKey(key.Name)).ToList();
            if (matches.Count > 1) throw new InvalidOperationException("The shared phonebook contains ambiguous names. Reconcile duplicate names before importing.");
            var contact = matches.SingleOrDefault();
            if (contact is null)
            {
                contact = new Contact { DisplayName = key.Name, OfficeNumber = key.Extension };
                contacts.Add(contact); db.Contacts.Add(contact);
            }
            if (!members.Any(m => ReferenceEquals(m.Contact, contact) || contact.Id != 0 && m.ContactId == contact.Id))
            {
                var member = new SwitchboardMember { Contact = contact };
                members.Add(member); db.SwitchboardMembers.Add(member);
            }
            var item = overrides.SingleOrDefault(o => contact.Id != 0 && o.ContactId == contact.Id);
            if (item is null) { item = new SwitchboardOverride { PhoneId = phoneId, Contact = contact }; db.SwitchboardOverrides.Add(item); overrides.Add(item); }
            // Keep existing handset differences explicit; never silently reconcile extensions or Value2.
            item.Extension = contact.OfficeNumber == key.Extension ? null : key.Extension;
            item.Value2 = key.Value2;
            item.CapturedKeyJson = JsonSerializer.Serialize(key);
        }
        profile.Revision = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Imported captured switchboard contacts for PhoneId {PhoneId}.", phoneId);
    }

    public async Task SetMemberAsync(int contactId, bool included, string? sortName, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync(ct);
        if (sortName?.Length > 128) throw new InvalidOperationException("Sort name cannot exceed 128 characters.");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Contacts.AnyAsync(c => c.Id == contactId, ct)) throw new InvalidOperationException("Contact no longer exists.");
        var member = await db.SwitchboardMembers.FindAsync([contactId], ct);
        if (included)
        {
            if (member is null) { member = new() { ContactId = contactId }; db.SwitchboardMembers.Add(member); }
            member.SortName = string.IsNullOrWhiteSpace(sortName) ? null : sortName.Trim();
        }
        else if (member is not null) db.SwitchboardMembers.Remove(member);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveOverrideAsync(int phoneId, int contactId, string? extension, string? value2, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        await PhoneAsync(db, phoneId, ct);
        EnsureIdle(await ProfileAsync(db, phoneId, ct));
        if (!await db.SwitchboardMembers.AnyAsync(c => c.ContactId == contactId, ct)) throw new InvalidOperationException("Select a switchboard contact first.");
        if (extension?.Length > 31 || value2?.Length > 31 || (extension ?? "").Contains('|') || (value2 ?? "").Contains('|'))
            throw new InvalidOperationException("Numbers cannot contain a pipe and must fit within 31 characters.");
        var item = await db.SwitchboardOverrides.FindAsync([phoneId, contactId], ct);
        if (item is null) { item = new() { PhoneId = phoneId, ContactId = contactId }; db.SwitchboardOverrides.Add(item); }
        item.Extension = string.IsNullOrWhiteSpace(extension) ? null : extension.Trim();
        // null inherits the captured Value2; empty explicitly clears it.
        item.Value2 = value2?.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task<SwitchboardDeployment> PreviewAsync(int phoneId, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var phone = await PhoneAsync(db, phoneId, ct);
        var profile = await ProfileAsync(db, phoneId, ct);
        EnsureIdle(profile);
        var members = await db.SwitchboardMembers.AsNoTracking().Include(m => m.Contact).ToListAsync(ct);
        if (members.Count == 0) throw new SwitchboardValidationException(SwitchboardRules.EmptyDirectoryMessage);
        var baseline = SwitchboardLayout.FromJson(profile.BaselineJson);
        var current = await OnPhoneAsync(phone, (client, token) => adapter.ReadAsync(client, token), ct);
        if (current.Fingerprint() != baseline.Fingerprint())
            throw new InvalidOperationException("The handset differs from its capture. Capture it again and review/import its contacts before previewing.");
        var overrides = await db.SwitchboardOverrides.AsNoTracking().Where(o => o.PhoneId == phoneId).ToListAsync(ct);
        // Import each phone before ordering; otherwise its differing numbers could be overwritten.
        foreach (var key in baseline.Keys.Where(k => !SwitchboardRules.LegacyDirectoryReserved(k) && k.Name.Length > 0))
        {
            var member = members.SingleOrDefault(m => SwitchboardRules.NameKey(m.Contact.DisplayName) == SwitchboardRules.NameKey(key.Name));
            if (member is not null && key.Extension != member.Contact.OfficeNumber
                && overrides.All(o => o.ContactId != member.ContactId || o.Extension is null && o.CapturedKeyJson is null))
                throw new InvalidOperationException("A captured extension differs from the master directory. Import this capture or save an explicit phone override before previewing.");
        }
        var target = SwitchboardRules.Generate(phone.IpAddress, baseline, members, overrides);
        return await SavePreviewAsync(db, phone, profile, current, target, ct, directoryHash: HashDirectory(members, overrides));
    }

    public async Task<SwitchboardDeployment> PreviewRestoreAsync(Guid deploymentId, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var saved = await db.SwitchboardDeployments.AsNoTracking().SingleAsync(p => p.Id == deploymentId, ct);
        if (saved.Status is not ("Success" or "Failed" or "Cancelled" or "TimedOut" or "Applying"))
            throw new InvalidOperationException("Only an attempted deployment has a restoration backup.");
        var phone = await PhoneAsync(db, saved.PhoneId, ct);
        if (phone.IpAddress != saved.IpAddress) throw new InvalidOperationException("The phone address changed. The backup cannot be restored to a different handset.");
        var profile = await ProfileAsync(db, phone.Id, ct); EnsureIdle(profile);
        var target = SwitchboardLayout.FromJson(saved.BeforeJson);
        var current = await OnPhoneAsync(phone, (client, token) => adapter.ReadAsync(client, token), ct);
        SwitchboardRules.ValidateGeometry(phone.IpAddress, target);
        return await SavePreviewAsync(db, phone, profile, current, target, ct, "Restoration preview ready");
    }

    private async Task<SwitchboardDeployment> SavePreviewAsync(AppDbContext db, Phone phone, SwitchboardProfile profile,
        SwitchboardLayout current, SwitchboardLayout target, CancellationToken ct, string message = "Preview ready", string? directoryHash = null)
    {
        var preview = new SwitchboardDeployment { PhoneId = phone.Id, IpAddress = phone.IpAddress, ProfileRevision = profile.Revision,
            DirectoryHash = directoryHash ?? await DirectoryHashAsync(db, phone.Id, ct), BeforeJson = current.ToJson(), TargetJson = target.ToJson(), Message = message };
        db.SwitchboardDeployments.Add(preview); await db.SaveChangesAsync(ct); return preview;
    }

    public async Task<SwitchboardDeployment> DeployAsync(Guid previewId, CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var preview = await db.SwitchboardDeployments.SingleAsync(p => p.Id == previewId, ct);
        var phone = await PhoneAsync(db, preview.PhoneId, ct);
        var profile = await ProfileAsync(db, phone.Id, ct);
        if (preview.Status != "Preview" || preview.CreatedAt < DateTime.UtcNow.AddMinutes(-30))
            throw new InvalidOperationException("This preview expired or was already attempted. Generate a new preview.");
        // Retire old automatic F2 corrections without deleting their restoration backups.
        if (preview.Purpose == "TemplateSetup")
            throw new InvalidOperationException("This automatic F2 correction is no longer supported. Review the phone's editable layout and generate a new preview.");
        var sourceRevision = preview.SourcePhoneId is int sourceId
            ? await db.SwitchboardProfiles.Where(p => p.PhoneId == sourceId).Select(p => (Guid?)p.DraftRevision).SingleOrDefaultAsync(ct) : null;
        if (phone.IpAddress != preview.IpAddress || profile.Revision != preview.ProfileRevision
            || (preview.Purpose == "CustomLayout" ? sourceRevision is null || sourceRevision != preview.SourceRevision
                : preview.DirectoryHash != await DirectoryHashAsync(db, phone.Id, ct)))
            throw new InvalidOperationException("The phone or saved layout changed since preview. Generate a new preview.");
        var before = SwitchboardLayout.FromJson(preview.BeforeJson);
        var target = SwitchboardLayout.FromJson(preview.TargetJson);
        if (preview.Purpose == "CustomLayout") SwitchboardEditing.ValidateDraft(phone.IpAddress, before, target);
        SwitchboardRules.ValidateGeometry(phone.IpAddress, target);
        var leaseId = Guid.NewGuid(); var now = DateTime.UtcNow;
        var lease = await db.SwitchboardProfiles.Where(p => p.PhoneId == phone.Id && p.Revision == preview.ProfileRevision && (p.LeaseUntil == null || p.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeaseId, leaseId).SetProperty(p => p.LeaseUntil, now.AddSeconds(options.Value.TimeoutSeconds + 30)), ct);
        if (lease != 1) throw new InvalidOperationException("Another deployment is using this phone. Try again after it finishes.");
        var claimed = false;
        try
        {
            claimed = await db.SwitchboardDeployments.Where(p => p.Id == previewId && p.Status == "Preview")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Applying").SetProperty(p => p.Message, "Delivery started; backup saved."), ct) == 1;
            if (!claimed) throw new InvalidOperationException("This preview was already attempted.");
            preview.Status = "Applying";
            await OnPhoneAsync(phone, async (client, token) =>
            {
                if ((await adapter.ReadAsync(client, token)).Fingerprint() != before.Fingerprint())
                    throw new InvalidOperationException("The handset changed since preview. No keys were written.");
                await adapter.WriteAsync(client, before, target, token); return true;
            }, ct);
            preview.Status = "Success"; preview.Message = "All five pages read back and verified.";
            profile.BaselineJson = target.ToJson(); profile.CapturedAt = DateTime.UtcNow; profile.Revision = Guid.NewGuid();
        }
        catch (OperationCanceledException)
        {
            preview.Status = ct.IsCancellationRequested || application.ApplicationStopping.IsCancellationRequested ? "Cancelled" : "TimedOut";
            preview.Message = "Delivery interrupted. Some pages may have changed; inspect the handset or preview restoration from the backup.";
        }
        catch (TimeoutException)
        {
            preview.Status = "TimedOut";
            preview.Message = "Delivery timed out. Some pages may have changed; inspect the handset or preview restoration from the backup.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Switchboard delivery failed for PhoneId {PhoneId}, DeploymentId {DeploymentId}.", phone.Id, previewId);
            preview.Status = "Failed";
            preview.Message = ex is InvalidOperationException ? ex.Message : "Delivery failed. Some pages may have changed; inspect the handset and saved backup.";
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.CleanupTimeoutSeconds));
            if (claimed)
            {
                preview.FinishedAt = DateTime.UtcNow;
                // Apply metadata through a fresh context so the lease cannot be overwritten by tracked state.
                await using var finish = await factory.CreateDbContextAsync(cleanup.Token);
                await finish.SwitchboardDeployments.Where(p => p.Id == previewId).ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, preview.Status).SetProperty(p => p.Message, preview.Message)
                    .SetProperty(p => p.FinishedAt, preview.FinishedAt), cleanup.Token);
                if (preview.Status == "Success")
                {
                    await finish.SwitchboardProfiles.Where(p => p.PhoneId == phone.Id && p.LeaseId == leaseId).ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.BaselineJson, target.ToJson()).SetProperty(p => p.CapturedAt, DateTime.UtcNow)
                        .SetProperty(p => p.Revision, Guid.NewGuid()), cleanup.Token);
                    if (preview.Purpose != "CustomLayout" || preview.SourcePhoneId != phone.Id)
                        await finish.SwitchboardProfiles.Where(p => p.PhoneId == phone.Id && p.LeaseId == leaseId).ExecuteUpdateAsync(s => s
                            .SetProperty(p => p.DraftJson, (string?)null).SetProperty(p => p.DraftRevision, Guid.NewGuid()), cleanup.Token);
                }
            }
            await using var release = await factory.CreateDbContextAsync(cleanup.Token);
            await release.SwitchboardProfiles.Where(p => p.PhoneId == phone.Id && p.LeaseId == leaseId).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.LeaseId, (Guid?)null).SetProperty(p => p.LeaseUntil, (DateTime?)null), cleanup.Token);
        }
        logger.LogInformation("Switchboard delivery {DeploymentId} for PhoneId {PhoneId} finished {Status}.", previewId, phone.Id, preview.Status);
        return preview;
    }

    private async Task<T> OnPhoneAsync<T>(Phone phone, Func<IFanvilClient, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        using var client = clients.Create(phone.IpAddress, phone.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
        try
        {
            var login = await client.LoginAsync(phone.Username, phone.Password, timeout.Token);
            if (!login.ok) throw new InvalidOperationException("Handset login failed. Check its stored credentials.");
            return await operation(client, timeout.Token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.CleanupTimeoutSeconds));
            try { await client.LogoutAsync(cleanup.Token); }
            catch (Exception ex) { logger.LogDebug(ex, "Switchboard logout did not complete for PhoneId {PhoneId}.", phone.Id); }
        }
    }

    private static async Task<Phone> PhoneAsync(AppDbContext db, int id, CancellationToken ct)
    {
        var phone = await db.Phones.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new InvalidOperationException("Phone no longer exists.");
        if (!phone.Enabled) throw new InvalidOperationException("Enable this phone in the fleet before managing its keys.");
        return phone;
    }
    private static async Task<SwitchboardProfile> ProfileAsync(AppDbContext db, int id, CancellationToken ct) =>
        await db.SwitchboardProfiles.SingleOrDefaultAsync(p => p.PhoneId == id, ct) ?? throw new InvalidOperationException("Capture the handset's current keys first.");
    private static void EnsureIdle(SwitchboardProfile? profile)
    { if (profile?.LeaseUntil > DateTime.UtcNow) throw new InvalidOperationException("A deployment is using this phone. Wait until it finishes."); }
    private static async Task<string> DirectoryHashAsync(AppDbContext db, int phoneId, CancellationToken ct)
    {
        var members = await db.SwitchboardMembers.AsNoTracking().Include(m => m.Contact).ToListAsync(ct);
        var overrides = await db.SwitchboardOverrides.AsNoTracking().Where(o => o.PhoneId == phoneId).ToListAsync(ct);
        return HashDirectory(members, overrides);
    }
    private static string HashDirectory(IEnumerable<SwitchboardMember> directory, IEnumerable<SwitchboardOverride> phoneOverrides) =>
        SwitchboardRules.Hash(JsonSerializer.Serialize(new
        {
            members = directory.OrderBy(m => m.ContactId).Select(m => new { m.ContactId, m.SortName, m.Contact.DisplayName, m.Contact.OfficeNumber }),
            overrides = phoneOverrides.OrderBy(o => o.ContactId).Select(o => new { o.ContactId, o.Extension, o.Value2, o.CapturedKeyJson })
        }));
}
