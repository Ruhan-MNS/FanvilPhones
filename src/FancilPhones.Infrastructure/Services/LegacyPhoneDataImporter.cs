using FancilPhones.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FancilPhones.Infrastructure.Services;

/// <summary>Explicit, one-time import into an empty phone store. Never imports Identity data.</summary>
public sealed class LegacyPhoneDataImporter(IDbContextFactory<AppDbContext> factory, ILogger<LegacyPhoneDataImporter> logger)
{
    public async Task<(int Phones, int Contacts, int Logs)> ImportAsync(string path, CancellationToken ct = default)
    {
        logger.LogInformation("Legacy phone store import started.");
        var source = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly
        };
        await using var legacy = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(source.ConnectionString).Options);
        // Materialize a consistent source snapshot before opening the destination transaction.
        await using var sourceTransaction = await legacy.Database.BeginTransactionAsync(ct);
        var phones = await legacy.Phones.AsNoTracking().ToListAsync(ct);
        var contacts = await legacy.Contacts.AsNoTracking().ToListAsync(ct);
        var logs = await legacy.SyncRuns.AsNoTracking().ToListAsync(ct);
        await sourceTransaction.CommitAsync(ct);

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (await db.Phones.AnyAsync(ct) || await db.Contacts.AnyAsync(ct) || await db.SyncRuns.AnyAsync(ct))
            throw new InvalidOperationException("SQLite import requires empty phone tables. Existing SQL data was left unchanged.");

        await InsertAsync(db, "Phones", phones, ct);
        await InsertAsync(db, "Contacts", contacts, ct);
        await InsertAsync(db, "SyncRuns", logs, ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Legacy import committed with {PhoneCount} phones, {ContactCount} contacts, and {HistoryCount} activity entries.",
            phones.Count, contacts.Count, logs.Count);
        return (phones.Count, contacts.Count, logs.Count);
    }

    private async Task InsertAsync<T>(AppDbContext db, string table, List<T> rows, CancellationToken ct) where T : class
    {
        var (enable, disable) = table switch
        {
            "Phones" => ("SET IDENTITY_INSERT [FancilPhones].[Phones] ON", "SET IDENTITY_INSERT [FancilPhones].[Phones] OFF"),
            "Contacts" => ("SET IDENTITY_INSERT [FancilPhones].[Contacts] ON", "SET IDENTITY_INSERT [FancilPhones].[Contacts] OFF"),
            "SyncRuns" => ("SET IDENTITY_INSERT [FancilPhones].[SyncRuns] ON", "SET IDENTITY_INSERT [FancilPhones].[SyncRuns] OFF"),
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        await db.Database.ExecuteSqlRawAsync(enable, ct);
        try
        {
            db.Set<T>().AddRange(rows);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await db.Database.ExecuteSqlRawAsync(disable, cleanup.Token); }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Identity insert cleanup did not complete after import cancellation.");
            }
        }
    }
}
