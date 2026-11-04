using Microsoft.Extensions.Configuration;
using Npgsql;
using TrustVault.Infrastructure.Persistence;

namespace TrustVault.Tests;

public sealed class DatabaseConnectionStringsTests
{
    [Fact]
    public void Render_postgres_url_is_converted_to_secure_npgsql_connection_string()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] =
                    "postgresql://trustvault:p%40ss%3Aword@db.example.com:5432/trustvault?sslmode=disable"
            })
            .Build();

        var connectionString = DatabaseConnectionStrings.Normalize(
            configuration["DATABASE_URL"]!);
        var parsed = new NpgsqlConnectionStringBuilder(connectionString);

        Assert.Equal("db.example.com", parsed.Host);
        Assert.Equal("trustvault", parsed.Database);
        Assert.Equal("trustvault", parsed.Username);
        Assert.Equal("p@ss:word", parsed.Password);
        Assert.Equal(SslMode.Require, parsed.SslMode);
    }

    [Fact]
    public void Native_npgsql_connection_string_is_preserved()
    {
        const string connectionString =
            "Host=localhost;Port=55432;Database=trustvault;Username=trustvault_app;Password=local_only";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TrustVault"] = connectionString
            })
            .Build();

        Assert.Equal(connectionString, DatabaseConnectionStrings.Get(configuration));
    }

    [Fact]
    public void Runtime_connection_does_not_fall_back_to_migrator_credential()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TrustVaultMigrator"] =
                    "Host=localhost;Database=trustvault;Username=trustvault_migrator;Password=migrator_local"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => DatabaseConnectionStrings.Get(configuration));
    }

    [Fact]
    public void Runtime_connection_does_not_fall_back_to_database_url()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] =
                    "postgres://trustvault_runtime:p%40ss%3Aword@db.example.com:5432/trustvault"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => DatabaseConnectionStrings.Get(configuration));
    }

    [Fact]
    public void Migrator_connection_uses_its_explicit_setting()
    {
        const string connectionString =
            "Host=localhost;Database=trustvault;Username=trustvault_migrator;Password=migrator_local";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TrustVaultMigrator"] = connectionString
            })
            .Build();

        Assert.Equal(connectionString, DatabaseConnectionStrings.GetMigrator(configuration));
    }

    [Fact]
    public void Migrator_connection_does_not_fall_back_to_runtime_credential()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TrustVault"] =
                    "Host=localhost;Database=trustvault;Username=trustvault_runtime;Password=runtime_local"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => DatabaseConnectionStrings.GetMigrator(configuration));
    }
}
