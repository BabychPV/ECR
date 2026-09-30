// tests/Ecr.Api.Tests/Health/DatabaseHealthRcsiOffTests.cs

using System.Net;
using System.Text.Json;
using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// D-102 / ФВ-7.9: вимкнений RCSI — це Critical (Unhealthy), а не «жовтий» стан:
/// <c>/health/ready</c> відповідає 503, а опис називає скрипт <c>Sql/06-rcsi.sql</c>.
/// </summary>
/// <remarks>
/// ⚠ База з RCSI OFF — тимчасова (як у <c>DbJobQueueRcsiTests</c>): вимкнути RCSI на
/// базі фікстури означало б <c>ROLLBACK IMMEDIATE</c> по з'єднаннях сусідніх тестів.
/// Перевірка <c>db</c> питає <c>sys.databases</c> живим запитом і до RCSI лише
/// читає системні представлення, тож схема в тимчасовій базі не потрібна.
///
/// Мутація: у <c>DatabaseHealthCheck</c> замінити <c>Unhealthy</c> для RCSI на
/// <c>Degraded</c> — обидва тести червоні; прибрати <c>[Sql/06-rcsi.sql]</c> з
/// опису — червоні обидва теж.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage7)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "D-102")]
public sealed class DatabaseHealthRcsiOffTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Перевірка_db_без_RCSI_Unhealthy_і_називає_скрипт()
    {
        await using var temp = await RcsiOffDatabase.CreateAsync(sql);
        await using var db = temp.CreateContext();

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc));

        // Проба «застаріла»: каже, що RCSI УВІМКНЕНО. Вирішує живий запит до бази.
        var probe = Substitute.For<ISqlCapabilities>();
        probe.IsReadCommittedSnapshotOn.Returns(true);

        var check = new DatabaseHealthCheck(
            probe,
            db,
            clock,
            Substitute.For<IUiStringCatalog>(),
            Substitute.For<ICurrentUser>(),
            DataProtectionKeyProtection.Unprotected);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration("db", Substitute.For<IHealthCheck>(), null, null),
            },
            CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(false, result.Data["rcsi"]);
        Assert.Contains("RCSI", result.Description, StringComparison.Ordinal);
        Assert.Contains("Sql/06-rcsi.sql", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ready_відповідає_503_коли_база_без_RCSI()
    {
        await using var temp = await RcsiOffDatabase.CreateAsync(sql);
        await using var badDb = temp.CreateContext();

        using var host = new EcrApiFactory(sql);
        using var app = host.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.Configure<HealthCheckServiceOptions>(options =>
            {
                // Той самий DatabaseHealthCheck, що й у проді, але над базою без RCSI:
                // все решту (jobs, sources, worker, tzdata) стенд лишає справжнім.
                var registration = options.Registrations.Single(r => r.Name == "db");
                options.Registrations.Remove(registration);
                options.Registrations.Add(new HealthCheckRegistration(
                    "db",
                    sp => ActivatorUtilities.CreateInstance<DatabaseHealthCheck>(sp, badDb),
                    registration.FailureStatus,
                    registration.Tags));
            })));
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"{response.StatusCode}: {body}");

        var report = JsonDocument.Parse(body).RootElement;
        Assert.Equal("Unhealthy", report.GetProperty("status").GetString());

        var dbCheck = report.GetProperty("checks")
            .EnumerateArray()
            .Single(c => string.Equals(c.GetProperty("name").GetString(), "db", StringComparison.Ordinal));
        Assert.Equal("Unhealthy", dbCheck.GetProperty("status").GetString());
        Assert.Contains("Sql/06-rcsi.sql", dbCheck.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    /// <summary>Порожня база з <c>READ_COMMITTED_SNAPSHOT OFF</c>, що знімається після тесту.</summary>
    private sealed class RcsiOffDatabase : IAsyncDisposable
    {
        private readonly string _name;
        private readonly string _master;
        private readonly string _connectionString;

        private RcsiOffDatabase(string name, string master, string connectionString)
        {
            _name = name;
            _master = master;
            _connectionString = connectionString;
        }

        public static async Task<RcsiOffDatabase> CreateAsync(SqlServerFixture sql)
        {
            var name = $"EcrTest_HealthRcsiOff_{Guid.NewGuid():N}"[..40];
            var master = new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
            var target = new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = name }.ConnectionString;

            await ExecAsync(
                master,
                $"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT OFF;");

            var temp = new RcsiOffDatabase(name, master, target);

            // Страховка від хибного «зеленого»: тест має справді стояти на базі без RCSI.
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT CAST(is_read_committed_snapshot_on AS int) FROM sys.databases WHERE name = DB_NAME();";
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));

            return temp;
        }

        public EcrDbContext CreateContext()
            => new(EfWarningGuard.Apply(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(_connectionString)).Options);

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecAsync(_master, $"""
                IF DB_ID(N'{_name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_name}];
                END
                """);
        }

        private static async Task ExecAsync(string connectionString, string text)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = text;
            await command.ExecuteNonQueryAsync();
        }
    }
}
