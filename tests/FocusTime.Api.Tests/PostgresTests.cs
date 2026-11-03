using System.Net;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FocusTime.Api.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION")))
            Skip = "Set TEST_DATABASE_CONNECTION to a PostgreSQL test administrator connection with CREATEDB permission.";
    }
}

public sealed class PostgresTests
{
    [PostgresFact]
    [Trait("Category", "PostgreSQL")]
    public async Task Migration_AppliesFromEmptyDatabase_AndRepeatsWithoutLosingData()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        Assert.NotEmpty(await context.Database.GetPendingMigrationsAsync());
        await context.Database.MigrateAsync();
        context.Users.Add(User("first@example.test"));
        await context.SaveChangesAsync();
        await context.Database.MigrateAsync();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Single(await context.Users.ToListAsync());
    }

    [PostgresFact]
    [Trait("Category", "PostgreSQL")]
    public async Task NormalizedEmail_IsUniqueAtDatabaseBoundary()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        context.Users.Add(User("first@example.test"));
        await context.SaveChangesAsync();
        var duplicate = User("FIRST@example.test");
        duplicate.UserName = "different-name";
        duplicate.NormalizedUserName = "DIFFERENT-NAME";
        context.Users.Add(duplicate);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [PostgresFact]
    [Trait("Category", "PostgreSQL")]
    public async Task Readiness_RequiresAppliedMigrations_WhileLivenessStaysHealthy()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.HasPendingModelChanges());
    }

    private static AppUser User(string email) => new()
    {
        Id = Guid.NewGuid(), Email = email, NormalizedEmail = email.ToUpperInvariant(),
        UserName = email, NormalizedUserName = email.ToUpperInvariant()
    };
}

internal sealed class TestDatabase(string adminConnection, string name, string connectionString) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;

    public static async Task<TestDatabase> CreateAsync()
    {
        var configured = Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION")
            ?? throw new InvalidOperationException("PostgreSQL integration test connection is missing.");
        var admin = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
        var name = "focustime_test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        var test = new NpgsqlConnectionStringBuilder(configured) { Database = name, Pooling = false };
        return new TestDatabase(admin.ConnectionString, name, test.ConnectionString);
    }

    public AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(ConnectionString, postgres => postgres.MigrationsHistoryTable("__ef_migrations_history", "public"))
        .Options);

    public async ValueTask DisposeAsync()
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^focustime_test_[a-f0-9]{32}$"))
            throw new InvalidOperationException("Refusing to remove a database outside the generated test namespace.");
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
