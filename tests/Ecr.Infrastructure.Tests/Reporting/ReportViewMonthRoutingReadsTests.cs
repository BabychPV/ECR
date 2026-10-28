// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewMonthRoutingReadsTests.cs
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Q-17 / P2 (Аудит-2, T3_00 Land: 96 місячних колонок, 23 400 рядків, ON: холодний 232 с): вʼюха з прапором ON
/// не має робити звернень до <c>doc.CellValue</c> «колонка x рядок» (231 seek на кожен рядок результату, ~5,4 млн
/// виконань), а читати комірки власника ОДНИМ діапазоном ключа на (рядок, власник): логічні читання ростуть
/// лінійно по рядках і не залежать від числа місячних колонок.
/// </summary>
/// <remarks>
/// Межа - у логічних читаннях ВСІЄЇ вʼюхи на рядок результату (<c>SET STATISTICS IO</c>), а не в часі: читання
/// детерміновані за даними й планом (час на CI плаває). Межа стабільна до дрейфу плану: вона в ~2,6 раза вища за виміряне
/// «після» і в ~2,8 раза нижча за найкраще виміряне «до» (старий текст SQL з IN-списком колонок на місяць: seek на колонку і
/// seek TableInstance на місяць), тож зміна оцінки кардинальності її не зачепить, а повернення seek-ів - зачепить.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportViewMonthRoutingReadsTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
    private const int ViewPeriod = 202610;
    private const int Rows = 2000;
    private const int MonthGroups = 8;   // 8 колонок на місяць x 12 = 96 місячних колонок (як T3_00)

    private static readonly Regex Reads = new(@"logical reads (\d+)", RegexOptions.Compiled);
    private static readonly Regex Cpu = new(@"CPU time = (\d+) ms", RegexOptions.Compiled);

    private async Task<(TestDocument Doc, string View)> BuildAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ViewPeriod, columnCount: 2, rowCount: Rows,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        await using var db = sql.CreateContext();
        var ordinal = 9000;
        for (var g = 2; g <= MonthGroups; g++)
        {
            for (var m = 1; m <= 12; m++)
            {
                var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"G{g}M{m}" + Guid.NewGuid().ToString("N")[..6]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = $"G{g}M{m}" }), ordinal++, CellDataType.Decimal);
                column.SetMonth(true, (byte)m);
                db.ColumnDefs.Add(column);
            }
        }

        await db.SaveChangesAsync();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        version.Publish(publishedByUserId: 1, Now);
        await db.SaveChangesAsync();
        var template = await db.Templates.SingleAsync(t => t.Id == version.TemplateId);
        var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
        var view = Regex.Replace($"v_{template.Code}_{doc.SheetCode}_{table.Code}_v{version.Version}", "[^A-Za-z0-9_]", "_");

        foreach (var month in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12 })
        {
            await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202600 + month));
        }

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        // По 2 заповнені колонки на місяць-власник (як ~20 комірок на рядок у Land), 2 немісячні - на якорі; власна копія
        // перегляду - для місяця 10.
        command.CommandText = """
            ;WITH mc AS (
                SELECT c.Id, c.MonthNumber, ROW_NUMBER() OVER (PARTITION BY c.MonthNumber ORDER BY c.Id) AS rn
                  FROM cfg.ColumnDef AS c WHERE c.TableDefId = @t AND c.IsMonthColumn = 1)
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
            SELECT r.PeriodKey, r.Id, mc.Id, @t, CAST(r.Ordinal % 97 AS decimal(18, 4)) + mc.rn + mc.MonthNumber / 100.0
              FROM doc.TableInstance AS ti
              JOIN doc.TableRow AS r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id
              JOIN mc ON r.PeriodKey = 202600 + mc.MonthNumber AND mc.rn <= 2
             WHERE ti.DocumentId = @d AND ti.TableDefId = @t;

            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
            SELECT r.PeriodKey, r.Id, c.Id, @t, CAST(r.Ordinal % 89 AS decimal(18, 4)) + (r.PeriodKey % 100)
              FROM doc.TableInstance AS ti
              JOIN doc.TableRow AS r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id
              JOIN cfg.ColumnDef AS c ON c.TableDefId = @t AND c.IsMonthColumn = 0 AND c.DataType <> 0
             WHERE ti.DocumentId = @d AND ti.TableDefId = @t AND r.PeriodKey IN (202601, @vp);
            """;
        command.Parameters.AddWithValue("@t", doc.TableDefId);
        command.Parameters.AddWithValue("@d", doc.DocumentId);
        command.Parameters.AddWithValue("@vp", ViewPeriod);
        await command.ExecuteNonQueryAsync();
        return (doc, view);
    }

    private async Task GenerateAsync(int templateVersionId)
    {
        await using var db = sql.CreateContext();
        var router = new MonthColumnRouter(
            db,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
                .Build());
        await new ReportViewGenerator(db, monthRouter: router).GenerateAsync(templateVersionId, CancellationToken.None);
    }

    private async Task<(long Reads, long CpuMs, int Rows)> MeasureAsync(TestDocument doc, string view)
    {
        long reads = 0, cpu = 0;
        await using var connection = new SqlConnection(sql.ConnectionString);
        connection.InfoMessage += (_, e) =>
        {
            foreach (Match m in Reads.Matches(e.Message))
            {
                reads += long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            foreach (Match m in Cpu.Matches(e.Message))
            {
                cpu += long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        };
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        command.CommandText = $"""
            SET STATISTICS IO ON; SET STATISTICS TIME ON;
            IF OBJECT_ID('tempdb..#x') IS NOT NULL DROP TABLE #x;
            SELECT * INTO #x FROM rpt.[{view}] WHERE _DocumentId = @d AND _PeriodKey = {ViewPeriod};
            SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
            SELECT COUNT(*) FROM #x;
            """;
        command.Parameters.AddWithValue("@d", doc.DocumentId);
        var count = (int)(await command.ExecuteScalarAsync())!;
        return (reads, cpu, count);
    }

    /// <summary>Відбиток вмісту вʼюхи (значення за _RowOrdinal і порядком колонок; без Id/ключів, що різні між прогонами).</summary>
    private async Task<string> ContentHashAsync(TestDocument doc, string view)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        command.CommandText = $"SELECT * FROM rpt.[{view}] WHERE _DocumentId = @d AND _PeriodKey = {ViewPeriod} ORDER BY _RowOrdinal";
        command.Parameters.AddWithValue("@d", doc.DocumentId);
        await using var reader = await command.ExecuteReaderAsync();
        var text = new System.Text.StringBuilder();
        while (await reader.ReadAsync())
        {
            text.Append(reader.GetValue(reader.GetOrdinal("_RowOrdinal"))).Append('|');
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (!reader.GetName(i).StartsWith('_'))
                {
                    text.Append(reader.IsDBNull(i) ? "~" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append(';');
                }
            }

            text.Append('\n');
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Вʼюха_ON_з_96_місячними_колонками_читає_комірки_лінійно_по_рядках()
    {
        var (doc, view) = await BuildAsync();
        await GenerateAsync(doc.TemplateVersionId);

        // Прогрів (компіляція плану), потім вимір.
        await MeasureAsync(doc, view);
        var (reads, cpu, rows) = await MeasureAsync(doc, view);
        var perRow = (double)reads / rows;
        output.WriteLine($"contentHash={await ContentHashAsync(doc, view)}");
        output.WriteLine($"rows={rows} logicalReads={reads} perRow={perRow:F1} cpuMs={cpu}");

        Assert.Equal(Rows, rows);
        // Межа - див. remarks: БЕЗ seek-ів на колонку читання на рядок = десятки, з ними - сотні й більше.
        Assert.True(perRow <= MaxReadsPerRow, $"логічних читань на рядок {perRow:F1} > {MaxReadsPerRow} (усього {reads})");
    }

    // Виміряно на цій синтетиці: старий текст (IN-список колонок + seek TableInstance на місяць) - 278,8 читань на рядок;
    // новий - 36,7..38,5 (діапазон: ізольований прогін і прогін у наборі з «застарілою» статистикою, де старий текст дає 224..395). Межа 100: з запасом над новим (дрейф плану не червонить) і нижче старого (регрес червонить).
    private const double MaxReadsPerRow = 100;
}
