using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FancilPhones.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FancilPhones.Tests;

// Explicit opt-in. These checks only read shared databases; ordinary tests use isolated SQLite stores.
public sealed class LiveDatabaseFactAttribute : FactAttribute
{
    public LiveDatabaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FANCILPHONES_TEST_LIVE_DATABASES") != "1")
            Skip = "Set FANCILPHONES_TEST_LIVE_DATABASES=1 to run read-only SQL Server verification.";
    }
}

public sealed class LiveDatabaseTests
{
    private static IConfiguration Configuration => new ConfigurationBuilder()
        .AddUserSecrets<Program>().AddEnvironmentVariables().Build();

    [LiveDatabaseFact]
    public async Task SharedIdentityUserModelCanReadAspAuth()
    {
        await using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlServer(Configuration.GetConnectionString("ASP_Auth")).Options);
        // Materialize the complete standard Identity user model to check column compatibility.
        Assert.NotEmpty(await db.Users.AsNoTracking().Take(1).ToListAsync());
    }

    [LiveDatabaseFact]
    public async Task SqlServerPhoneDataExactlyMatchesLegacySnapshot()
    {
        var path = Environment.GetEnvironmentVariable("FANCILPHONES_LEGACY_DATABASE")
            ?? throw new InvalidOperationException("Set FANCILPHONES_LEGACY_DATABASE to the imported SQLite snapshot.");
        var source = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly };
        await using var legacy = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(source.ConnectionString).Options);
        await using var sql = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(Configuration.GetConnectionString("MNSGlobalDev")).Options);
        Assert.Equal(Digest(await legacy.Phones.AsNoTracking().OrderBy(p => p.Id).ToListAsync()),
            Digest(await sql.Phones.AsNoTracking().OrderBy(p => p.Id).ToListAsync()));
        Assert.Equal(Digest(await legacy.Contacts.AsNoTracking().OrderBy(c => c.Id).ToListAsync()),
            Digest(await sql.Contacts.AsNoTracking().OrderBy(c => c.Id).ToListAsync()));
        Assert.Equal(Digest(await legacy.SyncRuns.AsNoTracking().OrderBy(r => r.Id).ToListAsync()),
            Digest(await sql.SyncRuns.AsNoTracking().OrderBy(r => r.Id).ToListAsync()));
    }

    // Report only digests on mismatch so phone credentials never appear in test output.
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
