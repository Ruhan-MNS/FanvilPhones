using FancilPhones.Application;
using FancilPhones.Infrastructure.Data;
using FancilPhones.Infrastructure.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FancilPhones.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = RequiredConnection(configuration, "ASP_Auth");
        var phones = RequiredConnection(configuration, "MNSGlobalDev");
        if (string.Equals(auth.DataSource, phones.DataSource, StringComparison.OrdinalIgnoreCase)
            && string.Equals(auth.InitialCatalog, phones.InitialCatalog, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ASP_Auth and MNSGlobalDev must use separate databases.");

        services.AddDbContextFactory<AuthDbContext>(options => options.UseSqlServer(auth.ConnectionString));
        services.AddScoped(provider => provider.GetRequiredService<IDbContextFactory<AuthDbContext>>().CreateDbContext());
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlServer(phones.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AppDbContext.Schema)));
        services.AddScoped<IPhoneDirectory, PhoneDirectory>();
        services.AddScoped<IPhoneSyncService, PhoneSyncService>();
        services.AddScoped<IPhoneDiscoveryService, PhoneDiscoveryService>();
        services.AddScoped<LegacyPhoneDataImporter>();
        return services;
    }

    private static SqlConnectionStringBuilder RequiredConnection(IConfiguration configuration, string name)
    {
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Connection string '{name}' is required. Configure ConnectionStrings:{name} in appsettings.json or ConnectionStrings__{name} in the host environment.");
        var connection = new SqlConnectionStringBuilder(value);
        if (string.IsNullOrWhiteSpace(connection.InitialCatalog))
            throw new InvalidOperationException($"Connection string '{name}' must specify a database.");
        return connection;
    }
}
