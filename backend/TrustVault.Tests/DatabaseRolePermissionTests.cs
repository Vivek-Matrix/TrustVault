using Microsoft.Extensions.Configuration;
using Npgsql;

namespace TrustVault.Tests;

public sealed class DatabaseRolePermissionTests
{
    [Fact]
    public async Task Runtime_role_is_not_privileged_or_an_owner()
    {
        await using var connection = await OpenRuntimeConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT role.rolname,
                   role.rolcanlogin,
                   role.rolsuper,
                   role.rolcreatedb,
                   role.rolcreaterole,
                   role.rolreplication,
                   role.rolbypassrls,
                   EXISTS (
                       SELECT 1
                       FROM pg_auth_members membership
                       WHERE membership.member = role.oid) AS has_role_membership,
                   EXISTS (
                       SELECT 1
                       FROM pg_class relation
                       JOIN pg_namespace schema ON schema.oid = relation.relnamespace
                       WHERE schema.nspname = 'public'
                         AND relation.relowner = role.oid) AS owns_public_relation,
                   (SELECT owner.rolname
                    FROM pg_class relation
                    JOIN pg_roles owner ON owner.oid = relation.relowner
                    WHERE relation.oid = 'public.audit_events'::regclass) AS audit_table_owner,
                   (SELECT owner.rolname
                    FROM pg_proc function
                    JOIN pg_roles owner ON owner.oid = function.proowner
                    WHERE function.oid = 'public.prevent_audit_event_mutation()'::regprocedure)
                       AS audit_trigger_function_owner
            FROM pg_roles role
            WHERE role.rolname = current_user;
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("trustvault_runtime", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.False(reader.GetBoolean(8));
        Assert.Equal("trustvault_migrator", reader.GetString(9));
        Assert.Equal("trustvault_migrator", reader.GetString(10));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Runtime_role_cannot_create_schema_objects()
    {
        await AssertPermissionDeniedAsync(
            "CREATE TABLE public.trustvault_runtime_permission_probe (id integer);");
    }

    [Theory]
    [InlineData("UPDATE public.audit_events SET \"Action\" = \"Action\";")]
    [InlineData("DELETE FROM public.audit_events;")]
    [InlineData("TRUNCATE TABLE public.audit_events;")]
    public async Task Runtime_role_cannot_mutate_or_truncate_audit_events(string sql)
    {
        await AssertPermissionDeniedAsync(sql);
    }

    private static async Task AssertPermissionDeniedAsync(string sql)
    {
        await using var connection = await OpenRuntimeConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        await transaction.RollbackAsync();
    }

    private static async Task<NpgsqlConnection> OpenRuntimeConnectionAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
        var connection = new NpgsqlConnection(
            TrustVault.Infrastructure.Persistence.DatabaseConnectionStrings.Get(configuration));
        await connection.OpenAsync();
        return connection;
    }
}
