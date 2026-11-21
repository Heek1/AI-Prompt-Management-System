using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;

namespace PromptManager.Tests.Infrastructure;

/// <summary>
/// An in-memory SQLite database that lives as long as this object.
/// Real SQLite rather than the EF in-memory provider, because the behaviour
/// under test depends on unique indexes and transactions.
/// </summary>
public sealed class SqliteTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteTestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var db = CreateContext();
        db.Database.Migrate();
    }

    public DbContextOptions<AppDbContext> Options =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

    public AppDbContext CreateContext() => new(Options);

    public void Dispose() => _connection.Dispose();
}
