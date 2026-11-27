// tests/Ecr.Infrastructure.Tests/Persistence/RequiredInputsRealDbMeasureTests.cs
using System.Diagnostics;
using System.Globalization;
using Ecr.Application.Documents.RequiredInputs;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ D-577 (огляд M2): перелік порожніх обов'язкових входів (<c>GET …/required-inputs</c>) на СПРАВЖНІЙ базі й розмірі таблиці
/// Land: план (сховища), рядки екземпляра, зріз і сканування — той самий шлях, що в <c>GetRequiredInputsHandler</c>, без
/// заглушок. Вимір — у вивід тесту (і в теку тимчасових файлів); межа щедра — сторож катастрофи, не бенчмарк.
/// </summary>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage7)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "D-577")]
public sealed class RequiredInputsRealDbMeasureTests(SqlServerFixture sql, ITestOutputHelper output)
{
    /// <summary>Рядків у екземплярі: більший за найбільшу таблицю Land за місяць (≈ 15 тис.).</summary>
    private const int Rows = 20_000;

    private static readonly AsyncLocal<bool> Measuring = new();

    [Fact]
    public async Task M2_перелік_входів_20_тис_рядків_на_справжній_базі()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202603, columnCount: 3, rowCount: 1, rowMode: TableRowMode.Dynamic);
        var input = doc.ColumnDefIds[1];
        var required = doc.ColumnDefIds[2];

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 300;
            command.CommandText = """
                INSERT INTO doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, Ordinal, IsDeleted, IsOrphaned, ModifiedAt)
                SELECT @p, NEXT VALUE FOR doc.TableRowSeq, @i, CONCAT(N'M2_', x.n), 100 + x.n, 0, 0, SYSUTCDATETIME()
                  FROM (SELECT TOP (@n) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                          FROM sys.all_objects a CROSS JOIN sys.all_objects b) x;
                -- Кожен рядок розпочато (вхід), кожен другий — з обов'язковим полем.
                INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                SELECT @p, r.Id, @in, @t, 1, 0, 0 FROM doc.TableRow r WHERE r.PeriodKey = @p AND r.TableInstanceId = @i AND r.RowKey LIKE N'M2[_]%';
                INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                SELECT @p, r.Id, @req, @t, 2, 0, 0 FROM doc.TableRow r
                 WHERE r.PeriodKey = @p AND r.TableInstanceId = @i AND r.RowKey LIKE N'M2[_]%' AND r.Ordinal % 2 = 0;
                """;
            command.Parameters.AddWithValue("@p", doc.PeriodKey.Value);
            command.Parameters.AddWithValue("@i", doc.TableInstanceId);
            command.Parameters.AddWithValue("@t", doc.TableDefId);
            command.Parameters.AddWithValue("@in", input);
            command.Parameters.AddWithValue("@req", required);
            command.Parameters.AddWithValue("@n", Rows);
            await command.ExecuteNonQueryAsync();
        }

        await using (var db = builder.CreateContext())
        {
            var methodology = new Methodology(
                EcrCode.Create($"M2_{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            var version = new MethodologyVersion(
                methodology.Id, "1.0", CalculationLevel.Configuration, 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            version.Publish(2, "m2", new DateOnly(2026, 1, 1), testsPassed: true, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();
            db.MethodologyRules.Add(new MethodologyRule(version.Id, EcrCode.Create("ALL"), "{}", 100));
            db.MethodologyRequiredInputs.Add(new MethodologyRequiredInput(version.Id, required, RequiredInputSeverity.Block, hint: null));
            db.CalculationBindings.Add(new CalculationBinding(doc.TableDefId, doc.ColumnDefIds[0], methodology.Id, "tons", "{}"));
            await db.SaveChangesAsync();
        }

        var best = TimeSpan.MaxValue;
        var found = 0;
        var commands = 0;
        for (var round = 0; round < 3; round++)
        {
            await using var db = builder.CreateContext();
            var table = await db.TableDefs.AsNoTracking().Include(t => t.Columns).SingleAsync(t => t.Id == doc.TableDefId);
            var columns = table.Columns.ToDictionary(c => c.Id);
            var scanner = new RequiredInputsScanner(new MethodologyStore(db), new PeriodStore(db));
            var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new SystemClock());
            var cells = new NormalizedCellStore(db);

            using var counter = new SqlClientCommandCounter(new CommandTally(), _ => Measuring.Value);
            Measuring.Value = true;
            var watch = Stopwatch.StartNew();
            var plan = await scanner.PlanAsync(doc.DocumentId, doc.PeriodKey.Value, doc.TemplateVersionId, table, CancellationToken.None);
            var rowIds = await rows.GetRowIdsAsync(doc.TableInstanceId, doc.PeriodKey, CancellationToken.None);
            var slice = await cells.ReadSliceAsync(doc.TableInstanceId, doc.PeriodKey, CancellationToken.None);
            found = RequiredInputsScanner.Scan(plan!, table, columns, slice, rowIds, "en").Count;
            watch.Stop();
            Measuring.Value = false;
            commands = counter.Tally.Snapshot().Total;
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"M2 required-inputs real DB: {Rows} rows, {found} missing, best of 3 {best.TotalMilliseconds:F0} ms, {commands} SQL commands");
        output.WriteLine(line);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "ecr-d577-m2-required-inputs.txt"), line);

        Assert.Equal(Rows / 2, found);
        Assert.InRange(commands, 1, 10);
        Assert.True(best < TimeSpan.FromSeconds(15), line);
    }
}
