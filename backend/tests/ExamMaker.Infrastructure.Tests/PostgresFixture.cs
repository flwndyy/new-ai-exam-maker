using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ExamMaker.Infrastructure.Tests;

// CI always supplies this variable. Local model-only runs explicitly skip database tests.
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("A2_TEST_PG_ADMIN"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
        {
            Skip = "Set A2_TEST_PG_ADMIN to a disposable PostgreSQL server with CREATEDB permission.";
        }
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string database = "exammaker_a2_test_" + Guid.NewGuid().ToString("N");
    private string? adminConnection;
    private bool created;
    public string ConnectionString { get; private set; } = string.Empty;

    public ExamMakerDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ExamMakerDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        adminConnection = Environment.GetEnvironmentVariable("A2_TEST_PG_ADMIN");
        if (string.IsNullOrWhiteSpace(adminConnection))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            {
                throw new InvalidOperationException("CI must configure A2_TEST_PG_ADMIN; database tests must run.");
            }
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database, Pooling = false };
        ConnectionString = builder.ConnectionString;
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        // The identifier is generated locally and never comes from user input.
        await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
        await command.ExecuteNonQueryAsync();
        created = true;
        try
        {
            await using var context = CreateContext();
            // Exercise an existing A2 installation before applying the review fixes.
            await context.GetService<IMigrator>().MigrateAsync("20261119063637_CompleteA2Schema");
            await context.Database.MigrateAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (!created) return;
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
        await command.ExecuteNonQueryAsync();
        created = false;
    }
}
