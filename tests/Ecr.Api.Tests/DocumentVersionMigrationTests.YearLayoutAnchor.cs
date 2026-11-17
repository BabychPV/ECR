// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.YearLayoutAnchor.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ LAND-YEAR L3 (B-static, рішення координатора): перехід «копія на місяць» → «рік» дописує в ЯКІР року (найменший
/// період календаря проєкту в році — його показує річний перегляд) немісячні введені значення, яких у якорі немає, з
/// НАЙПІЗНІШОГО місяця; місяці не згодні — відмова <c>nonMonthConflict</c> з переліком; значення якоря не чіпається.
/// Дефект-першоджерело — Land doc 96: статична T8_00 заповнена лише в 202609..202611, річний перегляд показував її порожньою.
/// </summary>
/// <remarks>
/// Сценарій — той самий <c>ArrangeYearAsync</c> (див. <c>DocumentVersionMigrationTests.YearLayout.cs</c>): C2 — немісячний
/// ввід (якір 209901 = 7, травень = 55), M9 — місячний ввід (вересень = 9). Мутаційний доказ (a): у
/// <c>DocumentVersionMigrationStore.AnchorFillSql</c> вставка в якір із <c>WHERE NOT EXISTS (...)</c> →
/// <c>WHERE 1 = 0 AND NOT EXISTS (...)</c> — застосування падає (кількість ≠ плану, ECR-CELL-0409), тест (a) червоний.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Task", "LAND-YEAR-L3-STATIC")]
    public async Task Порожній_якір_дістає_немісячне_значення_найпізнішого_місяця_і_річний_зріз_його_бачить()
    {
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            // Якір порожній; рядок 1: травень 55 і жовтень 55 (згодні); рядок 2: лише листопад 8. M9 (місячний) — не чіпається.
            await RemoveAnchorC2Async(y).ConfigureAwait(true);
            await PutC2Async(y, 209910, rowIndex: 0, 55m).ConfigureAwait(true);
            await PutC2Async(y, 209911, rowIndex: 1, 8m).ConfigureAwait(true);

            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);

            var dry = await PostYearAsync(client, y, dryRun: true).ConfigureAwait(true);
            var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
            var report = JsonDocument.Parse(dryBody).RootElement;
            Assert.True(report.GetProperty("canApply").GetBoolean(), dryBody);
            Assert.Equal(2, report.GetProperty("nonMonthCopiedToAnchor").GetInt64());
            Assert.Equal(JsonValueKind.Null, report.GetProperty("nonMonthConflicts").ValueKind);

            var applied = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body} {app.ErrorsText}");
            Assert.Equal(2, JsonDocument.Parse(body).RootElement.GetProperty("nonMonthCopiedToAnchor").GetInt64());
            Assert.Equal(y.TargetVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));

            await using (var db = y.Builder.CreateContext())
            {
                var anchorRows = await AnchorRowIdsAsync(db, y).ConfigureAwait(true);
                var row1 = await db.CellValues.AsNoTracking()
                    .SingleAsync(c => c.PeriodKeyValue == YearAnchor && c.TableRowId == anchorRows[0] && c.ColumnDefId == y.TargetC2).ConfigureAwait(true);
                Assert.Equal(55m, row1.ValueNumeric);
                Assert.False(row1.IsCalculated);
                Assert.Equal(y.TargetTableDefId, row1.TableDefId);
                var row2 = await db.CellValues.AsNoTracking()
                    .SingleAsync(c => c.PeriodKeyValue == YearAnchor && c.TableRowId == anchorRows[1] && c.ColumnDefId == y.TargetC2).ConfigureAwait(true);
                Assert.Equal(8m, row2.ValueNumeric);

                // Місячна колонка M9 у якір не копіюється (її власник — вересень), місяці лишились як були.
                Assert.False(await db.CellValues.AsNoTracking()
                    .AnyAsync(c => c.PeriodKeyValue == YearAnchor && c.ColumnDefId == y.TargetM9).ConfigureAwait(true));
                Assert.Equal(55m, (await db.CellValues.AsNoTracking()
                    .SingleAsync(c => c.PeriodKeyValue == 209910 && c.TableRowId == y.Row1ByPeriod[209910] && c.ColumnDefId == y.TargetC2)
                    .ConfigureAwait(true)).ValueNumeric);
            }

            // Справжній шлях читання річної версії: зріз якоря показує C2 = 55.
            await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(true);   // кеш маркерів міграції
            var cells = await ReadYearRowAsync(app, client, y).ConfigureAwait(true);
            Assert.Equal(55m, decimal.Parse(cells.GetProperty(y.C2Code).GetString()!, CultureInfo.InvariantCulture));
        }
        finally
        {
            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Task", "LAND-YEAR-L3-STATIC")]
    public async Task Місяці_не_згодні_при_порожньому_якорі_відмова_nonMonthConflict_з_переліком_і_нічого_не_записано()
    {
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            await RemoveAnchorC2Async(y).ConfigureAwait(true);
            await PutC2Async(y, 209910, rowIndex: 0, 56m).ConfigureAwait(true);   // травень 55 ≠ жовтень 56

            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);

            var dry = await PostYearAsync(client, y, dryRun: true).ConfigureAwait(true);
            var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
            var report = JsonDocument.Parse(dryBody).RootElement;
            Assert.False(report.GetProperty("canApply").GetBoolean());
            Assert.Equal(["nonMonthConflict"], report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()).ToList());
            Assert.Equal(0, report.GetProperty("nonMonthCopiedToAnchor").GetInt64());
            Assert.False(report.GetProperty("nonMonthConflictsTruncated").GetBoolean());
            var conflict = Assert.Single(report.GetProperty("nonMonthConflicts").EnumerateArray());
            Assert.Equal(y.Doc.DocumentId, conflict.GetProperty("documentId").GetInt64());
            Assert.Equal(y.TablePath, $"{conflict.GetProperty("sheetCode").GetString()}.{conflict.GetProperty("tableCode").GetString()}");
            Assert.Equal(y.C2Code, conflict.GetProperty("columnCode").GetString());
            Assert.Equal(y.Row1Key, conflict.GetProperty("rowKey").GetString());
            Assert.Equal(
                [(209905, "55"), (209910, "56")],
                conflict.GetProperty("values").EnumerateArray()
                    .Select(v => (v.GetProperty("periodKey").GetInt32(), v.GetProperty("value").GetString()))
                    .ToList());

            var refused = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            var problem = await AssertProblemAsync(
                refused, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateNonMonthConflict").ConfigureAwait(true);
            Assert.Contains("nonMonthConflict", problem, StringComparison.Ordinal);
            Assert.Contains(y.C2Code, problem, StringComparison.Ordinal);

            // Нічого не записано: версія та сама, якір без C2 (ні на вихідній, ні на цільовій колонці).
            Assert.Equal(y.SourceVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));
            await using var db = y.Builder.CreateContext();
            var anchorRow = y.Row1ByPeriod[YearAnchor];
            var sourceC2 = y.Doc.ColumnDefIds[1];
            Assert.False(await db.CellValues.AsNoTracking()
                .AnyAsync(c => c.PeriodKeyValue == YearAnchor && c.TableRowId == anchorRow
                               && (c.ColumnDefId == sourceC2 || c.ColumnDefId == y.TargetC2)).ConfigureAwait(true));
        }
        finally
        {
            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Task", "LAND-YEAR-L3-STATIC")]
    public async Task Значення_якоря_перемагає_місяці_не_згодні_між_собою_без_відмови_і_без_копій()
    {
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            // Якір 7 лишається; травень 55, жовтень 56 — не згодні між собою, але якір заповнений.
            await PutC2Async(y, 209910, rowIndex: 0, 56m).ConfigureAwait(true);

            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);

            var dry = await PostYearAsync(client, y, dryRun: true).ConfigureAwait(true);
            var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
            var report = JsonDocument.Parse(dryBody).RootElement;
            Assert.True(report.GetProperty("canApply").GetBoolean(), dryBody);
            Assert.Empty(report.GetProperty("refusals").EnumerateArray());
            Assert.Equal(0, report.GetProperty("nonMonthCopiedToAnchor").GetInt64());
            Assert.Equal(JsonValueKind.Null, report.GetProperty("nonMonthConflicts").ValueKind);

            var applied = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body} {app.ErrorsText}");

            await using var db = y.Builder.CreateContext();
            var anchorRow = y.Row1ByPeriod[YearAnchor];
            Assert.Equal(7m, (await db.CellValues.AsNoTracking()
                .SingleAsync(c => c.PeriodKeyValue == YearAnchor && c.TableRowId == anchorRow && c.ColumnDefId == y.TargetC2)
                .ConfigureAwait(true)).ValueNumeric);
        }
        finally
        {
            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }

    // ───────────────────────────── допоміжне (B-static) ─────────────────────────────

    /// <summary>Прибирає значення C2 (вихідна колонка) з рядка 1 якоря.</summary>
    private static async Task RemoveAnchorC2Async(YearScenario y)
    {
        await using var db = y.Builder.CreateContext();
        var sourceC2 = y.Doc.ColumnDefIds[1];
        var anchorRow = y.Row1ByPeriod[YearAnchor];
        var removed = await db.CellValues
            .Where(c => c.PeriodKeyValue == YearAnchor && c.TableRowId == anchorRow && c.ColumnDefId == sourceC2)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        Assert.Equal(1, removed);
    }

    /// <summary>Ставить C2 (вихідна колонка) у рядок <paramref name="rowIndex"/> (порядок за Ordinal) екземпляра періоду.</summary>
    private static async Task PutC2Async(YearScenario y, int period, int rowIndex, decimal value)
    {
        await using var db = y.Builder.CreateContext();
        var instanceId = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == period && r.Id == y.Row1ByPeriod[period]).Select(r => r.TableInstanceId)
            .SingleAsync().ConfigureAwait(false);
        var rowId = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == period && r.TableInstanceId == instanceId && !r.IsDeleted)
            .OrderBy(r => r.Ordinal).ThenBy(r => r.Id).Select(r => r.Id)
            .Skip(rowIndex).FirstAsync().ConfigureAwait(false);
        db.CellValues.Add(new CellValue(
            new CellAddress(new PeriodKey(period), rowId, y.Doc.ColumnDefIds[1]), y.Doc.TableDefId, new CellValueData { ValueNumeric = value }));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Рядки екземпляра якоря за порядком (Ordinal).</summary>
    private static async Task<List<long>> AnchorRowIdsAsync(Ecr.Infrastructure.Persistence.EcrDbContext db, YearScenario y)
    {
        var instanceId = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == YearAnchor && r.Id == y.Row1ByPeriod[YearAnchor]).Select(r => r.TableInstanceId)
            .SingleAsync().ConfigureAwait(false);
        return await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == YearAnchor && r.TableInstanceId == instanceId && !r.IsDeleted)
            .OrderBy(r => r.Ordinal).ThenBy(r => r.Id).Select(r => r.Id)
            .ToListAsync().ConfigureAwait(false);
    }
}
