using Microsoft.EntityFrameworkCore;
using Npgsql;
using PromptManager.Data;
using Testcontainers.PostgreSql;

namespace PromptManager.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL container for the whole test run. The schema is migrated
/// once into a template database; every test database is a copy of it,
/// which takes milliseconds and keeps tests fully isolated.
/// Requires Docker.
/// </summary>
internal static class PostgresServer
{
    private const string Template = "pm_template";

    private static readonly Lazy<Task<string>> AdminConnection = new(StartAsync);

    private static async Task<string> StartAsync()
    {
        // Same major version as compose.yaml.
        var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        // The container is removed by Testcontainers' resource reaper when the
        // test process exits.

        var admin = container.GetConnectionString();
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {Template}", conn);
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(WithDatabase(admin, Template)).Options;
        await using (var db = new AppDbContext(options))
            await db.Database.MigrateAsync();

        // A template cannot be copied while anyone is connected to it.
        NpgsqlConnection.ClearAllPools();
        return admin;
    }

    public static async Task<string> CreateDatabaseAsync()
    {
        var admin = await AdminConnection.Value;
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using var conn = new NpgsqlConnection(admin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name} TEMPLATE {Template}", conn);
        await cmd.ExecuteNonQueryAsync();
        return WithDatabase(admin, name);
    }

    public static async Task DropDatabaseAsync(string connectionString)
    {
        var admin = await AdminConnection.Value;
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await using var conn = new NpgsqlConnection(admin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;
}

/// <summary>A fresh, migrated PostgreSQL database that is dropped on dispose.</summary>
public sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        ConnectionString = PostgresServer.CreateDatabaseAsync().GetAwaiter().GetResult();
    }

    public string ConnectionString { get; }

    public DbContextOptions<AppDbContext> Options =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options;

    public AppDbContext CreateContext() => new(Options);

    // FORCE closes this database's leftover pooled connections.
    public void Dispose() => PostgresServer.DropDatabaseAsync(ConnectionString).GetAwaiter().GetResult();
}
