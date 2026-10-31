// tests/Ecr.Infrastructure.Tests/Integration/RawDataSeekTests.cs
using System.Data;
using System.Xml.Linq;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>
/// <c>D-463</c>: кожен запит читаючої сторони PI — seek, а не скан: <c>ext.RawDataPoint</c> (сторінка, експорт,
/// кількість, кошики графіка — <see cref="RawDataReader"/>) і <c>itg.CollectionCoverage</c> (покриті інтервали,
/// події періоду — <see cref="CellSourceStore"/>).
/// </summary>
/// <remarks>
/// ⛔ Міряється БОЙОВИЙ текст (публічні константи), контроль — той самий читач без предиката сутності, тобто
/// доказ, що середовище здатне показати скан. Шум — 20 000 точок інших тегів і сотні інтервалів іншої сутності:
/// щоб скан мав що читати.
/// <para>
/// ⚠ Перевіряється форма плану (немає <c>Scan</c> по таблиці), а не логічні читання: на малих даних числа читань
/// залежать від статистики, а форма — ні. <c>Key Lookup</c> (<c>Clustered Index Seek</c> з <c>Lookup</c>) —
/// не скан: значення точки лежить у кластері (до R2b, <c>docs/build/perf/R2-rawdatapoint-plan.md</c>).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RawDataSeekTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private static readonly DateTime From = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string, string> RawQueries => new()
    {
        { nameof(RawDataReader.PageSql), RawDataReader.PageSql },
        { nameof(RawDataReader.AscendingSql), RawDataReader.AscendingSql },
        { nameof(RawDataReader.CountSql), RawDataReader.CountSql },
        { nameof(RawDataReader.BucketsSql), RawDataReader.BucketsSql },
    };

    public static TheoryData<string, string> CoverageQueries => new()
    {
        { nameof(RawDataReader.CoveredSql), RawDataReader.CoveredSql },
        { nameof(CellSourceStore.PeriodEventsSql), CellSourceStore.PeriodEventsSql },
    };

    [Theory]
    [MemberData(nameof(RawQueries))]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Точки_ряду_читаються_seekом_а_не_сканом_таблиці(string name, string text)
    {
        var (target, noise) = await ArrangeAsync();

        var fixedOps = await PlanAsync(text, target, "[RawDataPoint]");
        var controlOps = await PlanAsync(
            "SELECT COUNT_BIG(*) FROM ext.RawDataPoint AS p WHERE p.SourcePath = @path AND p.[Timestamp] >= @from;",
            target, "[RawDataPoint]");

        output.WriteLine($"{name}: бойовий — {string.Join(", ", fixedOps)}; без сутності — {string.Join(", ", controlOps)}");

        Assert.Contains(controlOps, op => op.Contains("Scan", StringComparison.Ordinal));
        Assert.NotEmpty(fixedOps);
        Assert.DoesNotContain(fixedOps, op => op.Contains("Scan", StringComparison.Ordinal));
        Assert.True(noise > 0);
    }

    [Theory]
    [MemberData(nameof(CoverageQueries))]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Журнал_покриття_читається_seekом_за_сутністю(string name, string text)
    {
        var (target, _) = await ArrangeAsync();

        var fixedOps = await PlanAsync(text, target, "[CollectionCoverage]");
        var controlOps = await PlanAsync(
            "SELECT TOP (@take) c.Id, c.Details FROM itg.CollectionCoverage AS c WHERE c.PeriodKey = @period AND c.[Status] IS NOT NULL ORDER BY c.Id DESC;",
            target, "[CollectionCoverage]");

        output.WriteLine($"{name}: бойовий — {string.Join(", ", fixedOps)}; без сутності — {string.Join(", ", controlOps)}");

        Assert.Contains(controlOps, op => op.Contains("Scan", StringComparison.Ordinal));
        Assert.NotEmpty(fixedOps);
        Assert.DoesNotContain(fixedOps, op => op.Contains("Scan", StringComparison.Ordinal));
    }

    /// <summary>Сутність-ціль із 2 000 точок і шум: 20 000 точок інших тегів, інтервали й події іншої сутності.</summary>
    private async Task<(Target Target, int Noise)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = builder.CreateContext();
        var tag = $"{Guid.NewGuid():N}"[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Seek{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "seek" }),
            ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var target = new SourceEntity(dataSource.Id, $"T{tag}", RegistrySourceKind.External);
        var other = new SourceEntity(dataSource.Id, $"N{tag}", RegistrySourceKind.External);
        target.Deactivate();
        other.Deactivate();
        db.SourceEntities.AddRange(target, other);
        await db.SaveChangesAsync();

        var run = new CollectionRun(other.Id, From, From.AddDays(1), false, null, From);
        db.CollectionRuns.Add(run);
        await db.SaveChangesAsync();

        for (var i = 0; i < 400; i++)
        {
            db.CollectionCoverages.Add(new CollectionCoverage(other.Id, From.AddHours(i), From.AddHours(i + 1), run.Id));
            db.CollectionCoverages.Add(CollectionCoverage.Skipped(other.Id, 202603, CollectionCoverage.SkippedNoData, "{}", From.AddHours(i)));
        }

        db.CollectionCoverages.Add(new CollectionCoverage(target.Id, From, From.AddDays(1), run.Id));
        db.CollectionCoverages.Add(CollectionCoverage.Skipped(target.Id, 202603, CollectionCoverage.SkippedNoData, "{}", From));
        await db.SaveChangesAsync();

        var path = $"\\\\AF\\Seek\\{tag}|Flow";
        await InsertAsync(other.Id, $"noise-{tag}", run.Id, 10_000);
        await InsertAsync(target.Id, $"noise-{tag}", run.Id, 10_000);
        await InsertAsync(target.Id, path, run.Id, 2_000);

        // ⚠ Статистика після масової вставки — інакше перший план компілюється на порожній гістограмі.
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var stats = connection.CreateCommand();
            stats.CommandText = "UPDATE STATISTICS ext.RawDataPoint; UPDATE STATISTICS itg.CollectionCoverage;";
            stats.CommandTimeout = 120;
            await stats.ExecuteNonQueryAsync();
        }

        return (new Target(target.Id, path), 20_000);
    }

    private async Task InsertAsync(int entityId, string path, long runId, int count)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ext.RawDataPoint (SourceEntityId, SourcePath, [Timestamp], ValueNumeric, ValueString, UnitId, Quality, RetrievedAt, CollectionRunId)
            SELECT @e, @p, DATEADD(MINUTE, t.n, @from), t.n, NULL, NULL, NULL, @from, @run
            FROM (SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) - 1 AS n
                  FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS t;
            """;
        command.Parameters.Add("@e", SqlDbType.Int).Value = entityId;
        command.Parameters.Add("@p", SqlDbType.NVarChar, 400).Value = path;
        command.Parameters.Add("@from", SqlDbType.DateTime2).Value = From;
        command.Parameters.Add("@count", SqlDbType.Int).Value = count;
        command.Parameters.Add("@run", SqlDbType.BigInt).Value = runId;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Фізичні оператори плану по таблиці <paramref name="table"/> (параметри — як у бойовому читачі).</summary>
    private async Task<List<string>> PlanAsync(string text, Target target, string table)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SET STATISTICS XML ON;\n" + text + "\nSET STATISTICS XML OFF;";
        command.Parameters.Add("@take", SqlDbType.Int).Value = 101;
        command.Parameters.Add("@entity", SqlDbType.Int).Value = target.EntityId;
        command.Parameters.Add("@path", SqlDbType.NVarChar, 400).Value = target.Path;
        command.Parameters.Add("@from", SqlDbType.DateTime2, 3).Value = From;
        command.Parameters.Add("@to", SqlDbType.DateTime2, 3).Value = From.AddDays(365);
        command.Parameters.Add("@bucketMs", SqlDbType.BigInt).Value = 3_600_000L;
        command.Parameters.Add("@period", SqlDbType.Int).Value = 202603;

        var plans = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                var isPlan = reader.FieldCount == 1
                             && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal);
                while (await reader.ReadAsync())
                {
                    if (isPlan)
                    {
                        plans.Add(reader.GetString(0));
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        return plans
            .SelectMany(p => XDocument.Parse(p).Descendants(Showplan + "RelOp"))
            .Where(op => op.Elements().Any(e => e.Elements(Showplan + "Object")
                .Any(o => (string?)o.Attribute("Table") == table)))
            .Select(op => (string)op.Attribute("PhysicalOp")!)
            .ToList();
    }

    private sealed record Target(int EntityId, string Path);
}
