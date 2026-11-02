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

        var connectionString = DatabaseConnectionStrings.Get(configuration);
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
}
