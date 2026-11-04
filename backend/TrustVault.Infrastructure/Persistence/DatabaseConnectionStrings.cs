using Microsoft.Extensions.Configuration;
using Npgsql;

namespace TrustVault.Infrastructure.Persistence;

public static class DatabaseConnectionStrings
{
    public static string Get(IConfiguration configuration)
    {
        var value = configuration.GetConnectionString("TrustVault");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:TrustVault must be configured for the API runtime.");
        }

        return Normalize(value);
    }

    public static string GetMigrator(IConfiguration configuration)
    {
        var value = configuration.GetConnectionString("TrustVaultMigrator");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:TrustVaultMigrator must be configured for migrations.");
        }

        return Normalize(value);
    }

    public static string Normalize(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')))
        {
            throw new InvalidOperationException("The PostgreSQL DATABASE_URL is invalid.");
        }

        var separator = uri.UserInfo.IndexOf(':');
        if (separator <= 0 || separator == uri.UserInfo.Length - 1)
        {
            throw new InvalidOperationException(
                "The PostgreSQL DATABASE_URL must include a username and password.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')),
            Username = Uri.UnescapeDataString(uri.UserInfo[..separator]),
            Password = Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]),
            SslMode = SslMode.Require
        }.ConnectionString;
    }
}
