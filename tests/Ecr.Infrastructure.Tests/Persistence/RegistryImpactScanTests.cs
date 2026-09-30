// tests/Ecr.Infrastructure.Tests/Persistence/RegistryImpactScanTests.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Вимір впливу довідника (<c>RegistryImpactStore</c>) іде до <c>calc.CalculationResult</c> за
/// <c>MethodologyVersionId</c> без фільтра періоду: читання не мають рости від ЧУЖИХ рядків.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ: прибрати <c>IX_CalculationResult_Version</c> з <c>07-partition-tables.sql</c> —
/// запит сканує всі партиції (~сотні сторінок на 40 000 чужих рядків), тест червоний. Замір на 500 тис.
/// рядків: <c>docs/build/perf/impact-query-2026-10-01.md</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryImpactScanTests(SqlServerFixture sql)
{
    private const int ForeignRows = 40_000;
    private const int Slack = 6;
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вимір_впливу_не_читає_чужих_результатів()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);

        var registry = new RegistryDef(EcrCode.Create($"IS{_tag}"), Text("Registry"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        var target = new Methodology(EcrCode.Create($"IT_{_tag}"), Text("target"));
        var foreign = new Methodology(EcrCode.Create($"IF_{_tag}"), Text("foreign"));
        db.Methodologies.AddRange(target, foreign);
        await db.SaveChangesAsync();

        var targetVersion = new MethodologyVersion(target.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        var foreignVersion = new MethodologyVersion(foreign.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.AddRange(targetVersion, foreignVersion);
        await db.SaveChangesAsync();

        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(targetVersion.Id, "F1", registry.Id, "X"));
        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, null, Now, document.DocumentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        run.Complete("Succeeded", Now, null, null);
        run.MakeCurrent();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        await new CalculationResultStore(db, new TestClock(Now)).WriteResultsAsync(
            run.Id,
            [new CalculationOutput(
                document.DocumentId, "R-1",
                [new CalculationOutputValue(targetVersion.Id, null, "tons", 1m, unitId)],
                [])],
            CancellationToken.None);
        await db.SaveChangesAsync();

        // Поведінка запиту не змінилась (один документ), а читання шляху доступу «версія → результати»
        // міряються окремо: на малій тестовій базі оптимізатор може вести повний запит від документа
        // (seek по `IX_CalculationResult_Lookup`), тож лише доступ за версією прив'язаний до індексу однозначно.
        Assert.Single(await new RegistryImpactStore(db).ListImpactedAsync(registry.Id, 100, CancellationToken.None));
        var before = await ReadsAsync(targetVersion.Id);
        Assert.Equal(1, before.Rows);

        await InsertForeignRowsAsync(run.Id, foreignVersion.Id, document.DocumentId, document.PeriodKey.Value, unitId);

        await ReadsAsync(targetVersion.Id); // розігрів: оновлення статистики після масової вставки
        var after = await ReadsAsync(targetVersion.Id);

        Assert.Equal(1, after.Rows);
        Assert.True(
            after.Reads <= before.Reads + Slack,
            $"Доступ за версією методології: читань {before.Reads} -> {after.Reads} після {ForeignRows} чужих результатів.");
        Assert.Single(await new RegistryImpactStore(db).ListImpactedAsync(registry.Id, 100, CancellationToken.None));
    }

    /// <summary>Доступ запиту виміру до результатів і його логічні читання (<c>sys.dm_exec_sessions.logical_reads</c>).</summary>
    private async Task<(int Rows, long Reads)> ReadsAsync(int versionId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        async Task<long> SessionReadsAsync()
        {
            await using var q = connection.CreateCommand();
            q.CommandText = "SELECT logical_reads FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
            return Convert.ToInt64(await q.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        var start = await SessionReadsAsync();
        var rows = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DocumentId, CalculationRunId, PeriodKey
                FROM calc.CalculationResult
                WHERE MethodologyVersionId = @v
                """;
            command.Parameters.AddWithValue("@v", versionId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows++;
            }
        }

        return (rows, await SessionReadsAsync() - start);
    }

    private async Task InsertForeignRowsAsync(long runId, int versionId, long documentId, int periodKey, int unitId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        // Id — з SEQUENCE (діапазоном: NEXT VALUE FOR несумісний з TOP).
        command.CommandText = """
            DECLARE @first sql_variant;
            EXEC sys.sp_sequence_get_range @sequence_name = N'calc.CalculationResultSeq',
                 @range_size = @n, @range_first_value = @first OUTPUT;
            INSERT calc.CalculationResult (Id, PeriodKey, CalculationRunId, MethodologyVersionId, DocumentId,
                                           SourceRowKey, OutputCode, Value, UnitId, Kind)
            SELECT TOP (@n) CONVERT(bigint, @first) + ROW_NUMBER() OVER (ORDER BY (SELECT 1)) - 1,
                   @period, @run, @version, @doc, NULL, N'FOREIGN', 1, @unit, 0
            FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
            """;
        command.Parameters.AddWithValue("@n", ForeignRows);
        command.Parameters.AddWithValue("@period", periodKey);
        command.Parameters.AddWithValue("@run", runId);
        command.Parameters.AddWithValue("@version", versionId);
        command.Parameters.AddWithValue("@doc", documentId);
        command.Parameters.AddWithValue("@unit", unitId);
        await command.ExecuteNonQueryAsync();
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
