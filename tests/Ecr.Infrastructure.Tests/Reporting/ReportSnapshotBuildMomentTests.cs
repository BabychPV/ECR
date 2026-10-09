// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotBuildMomentTests.cs
using System.Data.Common;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// R6-X7 / X7-04: момент «побудовано станом на» і прогін походження зрізу фіксуються
/// ДО читання джерела.
/// </summary>
/// <remarks>
/// ⛔ Прогін перемикається РІВНО в мить, коли будівник читає джерело (перехоплювач
/// команди з міткою <see cref="ReportSnapshotBuilder.AggregateTag"/>), а годинник у ту
/// саму мить іде вперед — так у тесті відтворюється «агрегація тривала, і посеред неї
/// актуальним став новий прогін». Детерміновано: жодних паралельних потоків і таймерів.
/// <para>
/// Мутаційний доказ: повернути <c>clock.UtcNow</c> у конструктор зрізу ПІСЛЯ
/// <c>AggregateAsync</c> — червоніють твердження про <c>BuiltAt</c> і <c>IsStale</c>;
/// повернути <c>CurrentRunAsync</c> після агрегації — червоніє твердження про
/// <c>CalculationRunId</c>. Тести CI, локально не запускались.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportSnapshotBuildMomentTests(SqlServerFixture sql)
{
    private static readonly DateTime Start = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.5")]
    public async Task Прогін_що_перемкнувся_під_час_агрегації_старить_зріз()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        var versionId = await PublishedVersionAsync(chain);

        var clock = new TestClock(Start);
        var switcher = new RunSwitchOnAggregate(
            sql.ConnectionString, document.ProjectId, document.PeriodKey.Value, clock,
            finishedAt: Start.AddMinutes(30), clockAfter: Start.AddHours(1));

        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(switcher)
            .Options);

        using var memory = new MemoryCache(new MemoryCacheOptions());
        var builder = new ReportSnapshotBuilder(db, clock, memory);

        var snapshotId = await builder.BuildAsync(
            versionId, document.ProjectId, document.PeriodKey, parametersJson: null, CancellationToken.None);

        Assert.NotNull(switcher.RunId);

        var stored = await db.ReportSnapshots.AsNoTracking().SingleAsync(s => s.Id == snapshotId);

        // Момент ДО читання джерела, а не після: агрегація бачила стан на свій початок.
        Assert.Equal(Start, stored.BuiltAt);

        // Числа нового прогону в зріз не потрапили — і походження на нього не вказує.
        Assert.NotEqual(switcher.RunId, stored.CalculationRunId);

        var list = await builder.ListAsync(
            document.ProjectId, document.PeriodKey.Value, visibleProjectIds: null, CancellationToken.None);
        Assert.True(
            Assert.Single(list, s => s.Id == snapshotId).IsStale,
            "Прогін, що став актуальним посеред агрегації, мав зістарити зріз (ФВ-10.5).");
    }

    /// <summary>Опублікована версія звіту з двома числовими колонками.</summary>
    internal static async Task<int> PublishedVersionAsync(TestDocumentBuilder chain, int? reportDefId = null)
    {
        await using var db = chain.CreateContext();

        if (reportDefId is null)
        {
            var def = new ReportDef(
                EcrCode.Create($"RBM{Guid.NewGuid().ToString("N")[..8]}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Build moment test" }),
                isRegulatory: true);
            db.ReportDefs.Add(def);
            await db.SaveChangesAsync();
            reportDefId = def.Id;
        }

        var version = new ReportVersion(
            reportDefId.Value,
            $"1.{Guid.NewGuid().ToString("N")[..4]}",
            """[{"code":"DocumentId","kind":"number"},{"code":"Value","kind":"number"}]""",
            """{"rowSource":"CalculationResults"}""",
            Start);
        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync();
        return version.Id;
    }

    /// <summary>
    /// На ПЕРШОМУ читанні джерела зрізу робить актуальним новий прогін проєкту (окремим
    /// з'єднанням, закомічено) і переводить годинник уперед.
    /// </summary>
    private sealed class RunSwitchOnAggregate(
        string connectionString, int projectId, int periodKey, TestClock clock, DateTime finishedAt, DateTime clockAfter)
        : DbCommandInterceptor
    {
        public long? RunId { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (RunId is null && command.CommandText.Contains(ReportSnapshotBuilder.AggregateTag, StringComparison.Ordinal))
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT calc.CalculationRun (ProjectId, PeriodKey, Status, StartedAt, FinishedAt, ErrorMessage, DocumentId)
                    OUTPUT INSERTED.Id
                    VALUES (@p, @k, N'Current', @started, @finished, NULL, NULL);
                    """;
                insert.Parameters.AddWithValue("@p", projectId);
                insert.Parameters.AddWithValue("@k", periodKey);
                insert.Parameters.AddWithValue("@started", finishedAt.AddMinutes(-5));
                insert.Parameters.AddWithValue("@finished", finishedAt);
                RunId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);

                clock.Set(clockAfter);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
