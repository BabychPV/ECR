// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewSlotMapTests.cs
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Q-17.slotmap (P3, вʼюхи ON &lt;= 1,2x OFF): READ MODEL слотів <c>rpt.ViewSlot</c> - вʼюхи з маршрутизацією місяців читають
/// мапу (вʼюха, ColumnDefId, слот, Mo) замість inline VALUES і IN-списків. Тут закріплено: (1) мапу веде генератор - рядок на
/// кожен Id колонки у її слоті, Mo лише для маршрутизованої; повторна генерація її не чіпає; зміна шаблону її переписує; вʼюха
/// OFF рядків не має; (2) пропуск на старті (<c>@OnlyIfChanged</c>) не пропускає вʼюху, мапу якої хтось стер; (3) apply власної
/// обчисленої (<c>sc</c>) агрегує лише обчислені комірки МАРШРУТИЗОВАНИХ колонок, а не всі Formula/Calculated рядка.
/// Власна база: повна генерація пише відбиток і чистить мапу глобально.
/// </summary>
/// <remarks>
/// Мутації (лише локально, з оновленням часу <c>05-rpt-views.sql</c> перед збіркою): (а) прибрати <c>AND m.Mo IS NOT NULL</c> з
/// apply <c>sc</c> обох процедур - тест (3) червоний (вхід скалярних агрегатів +1 200); (б) прибрати умову залежності від
/// <c>rpt.ViewSlot</c> з умови пропуску - тест (2) червоний (Skipped, мапи немає, значення NULL); (в) прибрати
/// <c>DELETE ... WHERE ViewName = @view</c> перед INSERT мапи - тест (1) червоний (PK або зайвий рядок після зміни шаблону).
/// Рівність значень ON старий/новий - ReportViewMonthOwnershipTests, ReportViewSliceEquivalenceTests, ReportViewFamOwnedCellsTests
/// (на фікстурі) і EXCEPT в обидва боки на Land-копії (docs/build/ADR-Q17-READ-MODEL.md, Q-17.slotmap).
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait("Requirement", "ФВ-10.2")]
public sealed class ReportViewSlotMapTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
    private const int ViewPeriod = 202603;

    private readonly SqlServerFixture _sql = SqlServerFixture.WithOwnDatabase("_slotmap");

    public Task InitializeAsync() => _sql.InitializeAsync();

    public Task DisposeAsync() => _sql.DisposeAsync();

    [Fact]
    public async Task Мапу_слотів_веде_генератор_повтор_не_чіпає_зміна_шаблону_переписує_OFF_прибирає()
    {
        var builder = new TestDocumentBuilder(_sql.ConnectionString);
        var doc = await builder.BuildAsync(ViewPeriod, columnCount: 3, rowCount: 2, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
        await PublishAsync(doc.TemplateVersionId);
        var (versioned, stable) = await ViewNamesAsync(doc);

        Assert.DoesNotContain("Skipped", (await RunAsync(onlyIfChanged: false, monthRouting: true)).Template);

        // Очікувана мапа: кожна колонка - свій слот за порядком Id; Mo: місячна - її місяць, введена не-місячна - 0 (якір).
        var expected = await ExpectedMapAsync(doc.TableDefId);
        foreach (var view in new[] { versioned, stable })
        {
            Assert.Equal(expected, await MapAsync(view));
            Assert.True(await ReadsMapAsync(view), $"{view}: вʼюха ON не читає rpt.ViewSlot");
        }

        // Повтор без змін: Unchanged, мапа та сама.
        var again = await RunAsync(onlyIfChanged: false, monthRouting: true);
        Assert.DoesNotContain("Altered", again.Template);
        Assert.DoesNotContain("Altered", again.Stable);
        Assert.Equal(expected, await MapAsync(versioned));

        // Зміна шаблону (м'яке видалення місячної колонки M5): мапа переписана - без рядка M5, слоти зсунулися.
        await ExecuteAsync($"UPDATE cfg.ColumnDef SET IsDeleted = 1 WHERE Id = {doc.MonthColumnDefIds[4]}");
        var changed = await RunAsync(onlyIfChanged: true, monthRouting: true);
        Assert.Contains("Altered", changed.Template);
        var expectedAfter = await ExpectedMapAsync(doc.TableDefId);
        Assert.DoesNotContain(expectedAfter, r => r.ColumnDefId == doc.MonthColumnDefIds[4]);
        Assert.Equal(expectedAfter, await MapAsync(versioned));
        Assert.Equal(expectedAfter, await MapAsync(stable));

        // OFF: текст як до Q-17 (мапу не читає), рядків мапи цих вʼюх немає.
        await RunAsync(onlyIfChanged: false, monthRouting: false);
        foreach (var view in new[] { versioned, stable })
        {
            Assert.Empty(await MapAsync(view));
            Assert.False(await ReadsMapAsync(view), $"{view}: вʼюха OFF читає rpt.ViewSlot");
        }
    }

    [Fact]
    public async Task Старт_не_пропускає_вʼюху_мапу_якої_стерли_і_відновлює_її()
    {
        var builder = new TestDocumentBuilder(_sql.ConnectionString);
        var doc = await builder.BuildAsync(ViewPeriod, columnCount: 3, rowCount: 2, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
        await PublishAsync(doc.TemplateVersionId);
        var (versioned, stable) = await ViewNamesAsync(doc);
        await ExecuteAsync($"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
            VALUES ({ViewPeriod}, {doc.RowIds[0]}, {doc.MonthColumnDefIds[2]}, {doc.TableDefId}, 33.5)
            """);

        await RunAsync(onlyIfChanged: true, monthRouting: true);
        Assert.Equal(["Skipped"], (await RunAsync(onlyIfChanged: true, monthRouting: true)).Template);
        var expected = await MapAsync(versioned);
        var m3 = await ScalarAsync<string>($"SELECT Code FROM cfg.ColumnDef WHERE Id = {doc.MonthColumnDefIds[2]}");
        var query = $"SELECT [{m3}] FROM rpt.[{versioned}] WHERE _RowId = {doc.RowIds[0]}";
        Assert.Equal(33.5m, await ScalarAsync<decimal>(query));

        // Хтось стер мапу вʼюхи: відбиток той самий, але пропуск дав би вʼюху з NULL - генеруємо й відновлюємо.
        await ExecuteAsync($"DELETE FROM rpt.ViewSlot WHERE ViewName IN (N'{versioned}', N'{stable}')");
        var after = await RunAsync(onlyIfChanged: true, monthRouting: true);
        Assert.DoesNotContain("Skipped", after.Template);
        Assert.DoesNotContain("Skipped", after.Stable);
        Assert.Equal(expected, await MapAsync(versioned));
        Assert.NotEmpty(await MapAsync(stable));
        Assert.Equal(33.5m, await ScalarAsync<decimal>(query));
    }

    [Fact]
    public async Task Власна_обчислена_агрегує_лише_обчислені_комірки_маршрутизованих_колонок()
    {
        const int rows = 300;
        const int formulaColumns = 4;
        var builder = new TestDocumentBuilder(_sql.ConnectionString);
        var doc = await builder.BuildAsync(ViewPeriod, columnCount: 2 + formulaColumns, rowCount: rows,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
        // До публікації: колонки 3..6 - Formula (обчислені, не маршрутизуються). Їхні комірки IsCalculated = 1 - як на Land
        // (половина комірок екземпляра - обчислені Formula/Calculated, серед маршрутизованих - жодної).
        var formula = string.Join(",", doc.ColumnDefIds.Skip(2).Take(formulaColumns));
        await ExecuteAsync($"UPDATE cfg.ColumnDef SET DataType = {(int)CellDataType.Formula} WHERE Id IN ({formula})");
        await PublishAsync(doc.TemplateVersionId);
        var (versioned, stable) = await ViewNamesAsync(doc);
        await ExecuteAsync($"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated)
            SELECT r.PeriodKey, r.Id, c.Id, c.TableDefId, r.Ordinal, CASE WHEN c.Id IN ({formula}) THEN 1 ELSE 0 END
              FROM doc.TableRow AS r JOIN cfg.ColumnDef AS c ON c.TableDefId = {doc.TableDefId} AND c.DataType <> 0
             WHERE r.PeriodKey = {ViewPeriod} AND r.TableInstanceId = {doc.TableInstanceId};
            """);
        await RunAsync(onlyIfChanged: false, monthRouting: true);

        var ownCells = await ScalarAsync<int>($"""
            SELECT COUNT(*) FROM doc.CellValue AS x JOIN doc.TableRow AS r ON r.PeriodKey = x.PeriodKey AND r.Id = x.TableRowId
             WHERE r.PeriodKey = {ViewPeriod} AND r.TableInstanceId = {doc.TableInstanceId}
            """);
        foreach (var view in new[] { versioned, stable })
        {
            var input = ScalarAggregateInput(await PlanAsync($"SELECT * FROM rpt.[{view}] WHERE _DocumentId = {doc.DocumentId} AND _PeriodKey = {ViewPeriod}"));
            output.WriteLine($"{view}: вхід скалярних агрегатів {input}, власних комірок {ownCells}, обчислених Formula {rows * formulaColumns}");
            // pc агрегує власні комірки рядка (ownCells), sc - лише обчислені маршрутизованих (тут 0), назва аркуша - <= 1 на рядок.
            Assert.True(input >= ownCells, $"{view}: вхід {input} < власних комірок {ownCells} - pc не знайдено");
            Assert.True(input <= ownCells + 2L * rows,
                $"{view}: вхід скалярних агрегатів {input} > {ownCells + 2 * rows} - sc агрегує обчислені комірки НЕмаршрутизованих колонок");
        }
    }

    private async Task<List<(int ColumnDefId, int Slot, int? Mo)>> ExpectedMapAsync(int tableDefId)
    {
        var list = new List<(int, int, int?)>();
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, CAST(ROW_NUMBER() OVER (ORDER BY Id) AS int),
                   CASE WHEN DataType IN (6, 8) THEN NULL WHEN IsMonthColumn = 1 THEN CAST(MonthNumber AS int) ELSE 0 END
              FROM cfg.ColumnDef WHERE TableDefId = {tableDefId} AND IsDeleted = 0 ORDER BY Id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }

        return list;
    }

    private async Task<List<(int ColumnDefId, int Slot, int? Mo)>> MapAsync(string view)
    {
        var list = new List<(int, int, int?)>();
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ColumnDefId, Slot, Mo FROM rpt.ViewSlot WHERE ViewName = @v ORDER BY ColumnDefId, Slot";
        command.Parameters.AddWithValue("@v", view);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }

        return list;
    }

    private Task<bool> ReadsMapAsync(string view) => ScalarAsync<bool>($"""
        SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.sql_expression_dependencies
                                       WHERE referencing_id = OBJECT_ID(N'rpt.[{view}]', N'V') AND referenced_id = OBJECT_ID(N'rpt.ViewSlot', N'U'))
                         THEN 1 ELSE 0 END AS bit)
        """);

    private async Task PublishAsync(int templateVersionId)
    {
        await using var db = _sql.CreateContext();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == templateVersionId);
        version.Publish(publishedByUserId: 1, Now);
        await db.SaveChangesAsync();
    }

    private async Task<(string Versioned, string Stable)> ViewNamesAsync(TestDocument doc)
    {
        await using var db = _sql.CreateContext();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        var template = await db.Templates.SingleAsync(t => t.Id == version.TemplateId);
        var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
        return (Regex.Replace($"v_{template.Code}_{doc.SheetCode}_{table.Code}_v{version.Version}", "[^A-Za-z0-9_]", "_"),
                Regex.Replace($"v_{template.Code}_{doc.SheetCode}_{table.Code}", "[^A-Za-z0-9_]", "_"));
    }

    private async Task<(List<string> Template, List<string> Stable)> RunAsync(bool onlyIfChanged, bool monthRouting)
    {
        var template = await ActionsAsync(
            "EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId = NULL, @MonthRouting = @r, @OnlyIfChanged = @o", 2, onlyIfChanged, monthRouting);
        var stable = await ActionsAsync(
            "EXEC rpt.usp_GenerateStableViews @TemplateVersionId = NULL, @MonthRouting = @r, @OnlyIfChanged = @o", 1, onlyIfChanged, monthRouting);
        return (template, stable);
    }

    private async Task<List<string>> ActionsAsync(string text, int actionColumn, bool onlyIfChanged, bool monthRouting)
    {
        var result = new List<string>();
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = text;
        command.Parameters.AddWithValue("@o", onlyIfChanged);
        command.Parameters.AddWithValue("@r", monthRouting);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(actionColumn));
        }

        return result;
    }

    private async Task<T> ScalarAsync<T>(string text)
    {
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<XDocument> PlanAsync(string query)
    {
        await using var connection = new SqlConnection(_sql.ConnectionString);
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

    /// <summary>Сума фактичних рядків входу всіх агрегатів БЕЗ групування (скалярні агрегати apply pc / sc / назви аркуша).</summary>
    private static long ScalarAggregateInput(XDocument plan)
    {
        XNamespace sp = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        long Rows(XElement relOp) => relOp.Elements(sp + "RunTimeInformation").Elements(sp + "RunTimeCountersPerThread")
            .Sum(c => (long?)c.Attribute("ActualRows") ?? 0);
        long total = 0;
        foreach (var op in plan.Descendants(sp + "RelOp")
                     .Where(o => ((string?)o.Attribute("LogicalOp") ?? "").Contains("Aggregate", StringComparison.Ordinal)))
        {
            foreach (var body in op.Elements().Where(e => e.Elements(sp + "RelOp").Any()))
            {
                if (!body.Elements(sp + "GroupBy").Any() && !body.Elements(sp + "HashKeysBuild").Any())
                {
                    total += body.Elements(sp + "RelOp").Sum(Rows);
                }
            }
        }

        return total;
    }
}
