// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewFamOwnedCellsTests.cs
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
/// Q-17.perf3 (Land-копія, T9_04 doc 10 + 202601, 368 рядків: ON 2,2-2,4 с проти OFF 0,5-0,8 с): похідна власників <c>fam</c>
/// агрегувала ВСІ комірки всіх 12 екземплярів року (45 520 рядків, 1,96 с CPU з 3,0 с), бо вибір власника стояв лише в CASE
/// кожного з ~200 агрегатів. Тепер у агрегацію йдуть лише комірки, що належать власнику (місячна колонка M{m} - з екземпляра
/// місяця m, введена не-місячна - з якоря року): вхід агрегату fam = «комірки власників», а не «комірки року».
/// </summary>
/// <remarks>
/// Синтетика: кожен з 12 екземплярів має копії ВСІХ 12 місячних і обох не-місячних числових колонок (як Land до консолідації).
/// Власних комірок - рядки x 14, комірок року - рядки x 168. Межа - на ФАКТИЧНИХ рядках входу агрегату з групуванням по RowKey
/// (план <c>SET STATISTICS XML</c>), не на часі: детерміновано за даними. Мутація: старий генератор (умова власника лише в CASE) -
/// вхід 12x, тест червоний. Значення ті самі - перевіряється тут (власник, не копія) і в ReportViewMonthOwnershipTests /
/// ReportViewSliceEquivalenceTests.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportViewFamOwnedCellsTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const int ViewPeriod = 202610;
    private const int Rows = 300;
    private const int OwnedPerRow = 12 + 2;   // 12 місячних (кожна - зі свого місяця) + 2 не-місячні числові (з якоря)

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task З_прапором_похідна_власників_агрегує_лише_комірки_власників_а_не_всі_копії_року()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ViewPeriod, columnCount: 3, rowCount: Rows,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        string versioned, stable, c2;
        var months = new string[13];
        await using (var db = sql.CreateContext())
        {
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
            version.Publish(publishedByUserId: 1, new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync();
            var template = await db.Templates.SingleAsync(t => t.Id == version.TemplateId);
            var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
            versioned = Regex.Replace($"v_{template.Code}_{doc.SheetCode}_{table.Code}_v{version.Version}", "[^A-Za-z0-9_]", "_");
            stable = Regex.Replace($"v_{template.Code}_{doc.SheetCode}_{table.Code}", "[^A-Za-z0-9_]", "_");
            var codes = await db.ColumnDefs.Where(c => c.TableDefId == doc.TableDefId)
                .Select(c => new { c.Id, c.Code, c.IsMonthColumn, c.MonthNumber }).ToListAsync();
            c2 = codes.Single(c => c.Id == doc.ColumnDefIds[1]).Code;
            foreach (var c in codes.Where(c => c.IsMonthColumn))
            {
                months[c.MonthNumber!.Value] = c.Code;
            }
        }

        foreach (var month in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12 })
        {
            await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202600 + month));
        }

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var fill = connection.CreateCommand();
            fill.CommandTimeout = 300;
            // Копії в КОЖНОМУ екземплярі: значення = місяць екземпляра x 1000 + місяць колонки x 10 (не-місячні: + 500) + Ordinal % 7.
            fill.CommandText = """
                INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
                SELECT r.PeriodKey, r.Id, c.Id, @t,
                       (r.PeriodKey % 100) * 1000 + CASE WHEN c.IsMonthColumn = 1 THEN c.MonthNumber * 10 ELSE 500 END + r.Ordinal % 7
                  FROM doc.TableInstance AS ti
                  JOIN doc.TableRow AS r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id
                  JOIN cfg.ColumnDef AS c ON c.TableDefId = @t AND c.DataType <> 0
                 WHERE ti.DocumentId = @d AND ti.TableDefId = @t;
                """;
            fill.Parameters.AddWithValue("@t", doc.TableDefId);
            fill.Parameters.AddWithValue("@d", doc.DocumentId);
            await fill.ExecuteNonQueryAsync();
        }

        await using (var db = sql.CreateContext())
        {
            var router = new MonthColumnRouter(db, new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" }).Build());
            await new ReportViewGenerator(db, monthRouter: router).GenerateAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        foreach (var view in new[] { versioned, stable })
        {
            var query = $"SELECT * FROM rpt.[{view}] WHERE _DocumentId = {doc.DocumentId} AND _PeriodKey = {ViewPeriod}";
            var input = FamAggregateInput(await PlanAsync(query));
            output.WriteLine($"{view}: вхід агрегату fam = {input} (власних комірок {Rows * OwnedPerRow}, усіх копій року {Rows * OwnedPerRow * 12})");
            Assert.True(input > 0, $"{view}: агрегат з групуванням по RowKey не знайдено в плані");
            Assert.True(input <= 2L * Rows * OwnedPerRow, $"{view}: вхід агрегату fam {input} > {2 * Rows * OwnedPerRow} - агрегуються копії, а не лише комірки власників");

            // Значення - власника: M{m} з екземпляра m, не-місячна - з якоря 202601 (а не власна копія 202610).
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT _RowOrdinal, [{c2}], [{months[3]}], [{months[10]}], [{months[12]}] FROM rpt.[{view}] "
                                  + $"WHERE _DocumentId = {doc.DocumentId} AND _PeriodKey = {ViewPeriod}";
            await using var reader = await command.ExecuteReaderAsync();
            var seen = 0;
            while (await reader.ReadAsync())
            {
                var k = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) % 7;
                Assert.Equal(1000m + 500 + k, reader.GetDecimal(1));
                Assert.Equal(3000m + 30 + k, reader.GetDecimal(2));
                Assert.Equal(10000m + 100 + k, reader.GetDecimal(3));
                Assert.Equal(12000m + 120 + k, reader.GetDecimal(4));
                seen++;
            }

            Assert.Equal(Rows, seen);
        }
    }

    private async Task<XDocument> PlanAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        command.CommandText = "SET STATISTICS XML ON; " + query.Replace("SELECT *", "SELECT * INTO #y", StringComparison.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        var last = "";
        do
        {
            while (await reader.ReadAsync())
            {
                if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase))
                {
                    last = reader.GetString(0);
                }
            }
        }
        while (await reader.NextResultAsync());
        return XDocument.Parse(last);
    }

    /// <summary>Найбільший фактичний вхід (рядки дочірнього оператора) агрегату з групуванням по RowKey - це агрегація fam.</summary>
    private static long FamAggregateInput(XDocument plan)
    {
        XNamespace sp = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        long Rows(XElement relOp) => relOp.Elements(sp + "RunTimeInformation").Elements(sp + "RunTimeCountersPerThread")
            .Sum(c => (long?)c.Attribute("ActualRows") ?? 0);
        long best = 0;
        foreach (var op in plan.Descendants(sp + "RelOp")
                     .Where(o => ((string?)o.Attribute("LogicalOp") ?? "").Contains("Aggregate", StringComparison.Ordinal)))
        {
            foreach (var body in op.Elements().Where(e => e.Elements(sp + "RelOp").Any()))
            {
                var keys = body.Elements(sp + "GroupBy").Concat(body.Elements(sp + "HashKeysBuild"))
                    .Descendants(sp + "ColumnReference")
                    .Select(c => (string?)c.Attribute("Column"));
                if (keys.Any(k => k == "RowKey"))
                {
                    best = Math.Max(best, body.Elements(sp + "RelOp").Sum(Rows));
                }
            }
        }

        return best;
    }
}
