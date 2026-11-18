// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.YearLayoutAnchor.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
            Assert.Equal(0, report.GetProperty("nonMonthAnchorRowsCreated").GetInt64());   // обидва рядки в якорі вже є
            Assert.Equal(0, report.GetProperty("anchorInstancesCreated").GetInt64());     // екземпляр якоря вже є
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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Task", "LAND-YEAR-L3-STATIC")]
    public async Task Якір_без_екземпляра_таблиці_дістає_екземпляр_рядки_й_значення_найпізнішого_місяця()
    {
        // Як Land doc 96: T8_00 немає в 202601..202608, є лише в пізніших місяцях з тими самими RowKey.
        // ✎ (a′): відсутній екземпляр якоря таблиці складу документа створює крок «усі екземпляри якоря» разом із фіксованими
        // рядками шаблону; крок копії рядків уже не створює (nonMonthAnchorRowsCreated = 0), екземпляр — 1.
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            await using (var db = y.Builder.CreateContext())
            {
                var instanceId = y.Doc.TableInstanceId;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE cv FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
                     WHERE r.PeriodKey = {YearAnchor} AND r.TableInstanceId = {instanceId};
                    DELETE cs FROM doc.CellStyle cs JOIN doc.TableRow r ON r.PeriodKey = cs.PeriodKey AND r.Id = cs.TableRowId
                     WHERE r.PeriodKey = {YearAnchor} AND r.TableInstanceId = {instanceId};
                    DELETE fo FROM doc.CellFormulaOverride fo JOIN doc.TableRow r ON r.PeriodKey = fo.PeriodKey AND r.Id = fo.TableRowId
                     WHERE r.PeriodKey = {YearAnchor} AND r.TableInstanceId = {instanceId};
                    DELETE FROM doc.TableRow WHERE PeriodKey = {YearAnchor} AND TableInstanceId = {instanceId};
                    DELETE FROM doc.TableInstance WHERE PeriodKey = {YearAnchor} AND Id = {instanceId};
                    """).ConfigureAwait(true);
            }

            await PutC2Async(y, 209910, rowIndex: 0, 55m).ConfigureAwait(true);   // травень 55 (сценарій) + жовтень 55

            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);

            var dry = await PostYearAsync(client, y, dryRun: true).ConfigureAwait(true);
            var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
            var report = JsonDocument.Parse(dryBody).RootElement;
            Assert.True(report.GetProperty("canApply").GetBoolean(), dryBody);
            Assert.Equal(1, report.GetProperty("nonMonthCopiedToAnchor").GetInt64());
            Assert.Equal(0, report.GetProperty("nonMonthAnchorRowsCreated").GetInt64());   // фіксовані рядки — разом з екземпляром
            Assert.Equal(1, report.GetProperty("anchorInstancesCreated").GetInt64());

            var applied = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body} {app.ErrorsText}");
            Assert.Equal(y.TargetVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));

            long anchorInstance;
            await using (var db = y.Builder.CreateContext())
            {
                anchorInstance = await db.TableInstances.AsNoTracking()
                    .Where(t => t.PeriodKeyValue == YearAnchor && t.DocumentId == y.Doc.DocumentId && t.TableDefId == y.TargetTableDefId)
                    .Select(t => t.Id).SingleAsync().ConfigureAwait(true);
                var anchorRows = await db.TableRows.AsNoTracking()
                    .Where(r => r.PeriodKeyValue == YearAnchor && r.TableInstanceId == anchorInstance && !r.IsDeleted)
                    .Select(r => new { r.Id, r.RowKeyValue, r.RowDefId, r.Ordinal }).ToListAsync().ConfigureAwait(true);
                var monthRows = await db.TableRows.AsNoTracking()
                    .Where(r => r.PeriodKeyValue == 209911 && r.TableInstanceId == db.TableRows
                        .Where(x => x.PeriodKeyValue == 209911 && x.Id == y.Row1ByPeriod[209911]).Select(x => x.TableInstanceId).First())
                    .Select(r => new { r.RowKeyValue, r.RowDefId, r.Ordinal }).ToListAsync().ConfigureAwait(true);
                // ✎ (a′): рядки якоря — фіксовані рядки шаблону цільової таблиці (як шлях відкриття), тож RowDefId — опис рядка
                // цільової версії (рядки місяців у цьому будівнику без RowDefId); ключі й порядок — ті самі, що в місяцях.
                Assert.Equal(
                    monthRows.OrderBy(r => r.RowKeyValue, StringComparer.Ordinal).Select(r => (r.RowKeyValue, r.Ordinal)).ToList(),
                    anchorRows.OrderBy(r => r.RowKeyValue, StringComparer.Ordinal).Select(r => (r.RowKeyValue, r.Ordinal)).ToList());
                Assert.All(anchorRows, r => Assert.NotNull(r.RowDefId));

                var row1 = anchorRows.Single(r => r.RowKeyValue == y.Row1Key).Id;
                Assert.Equal(55m, (await db.CellValues.AsNoTracking()
                    .SingleAsync(c => c.PeriodKeyValue == YearAnchor && c.TableRowId == row1 && c.ColumnDefId == y.TargetC2)
                    .ConfigureAwait(true)).ValueNumeric);
            }

            // Справжній шлях читання річної версії: зріз нового екземпляра якоря показує C2 = 55.
            await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(true);   // кеш маркерів міграції
            using var response = await client.GetAsync(new Uri(
                $"/api/v1/documents/{y.Doc.DocumentId}/tables/{anchorInstance}", UriKind.Relative)).ConfigureAwait(true);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}\n{text}\n{app.ErrorsText}");
            var cells = JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray()
                .First(r => r.GetProperty("rowKey").GetString() == y.Row1Key).GetProperty("cells");
            Assert.Equal(55m, decimal.Parse(cells.GetProperty(y.C2Code).GetString()!, CultureInfo.InvariantCulture));
        }
        finally
        {
            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Task", "LAND-YEAR-L3-STATIC")]
    public async Task Поданий_або_затверджений_якір_відмова_sheetsLocked_і_нічого_не_записано(bool approved)
    {
        // Як T8_00 Land doc 96: окремий аркуш зі СТАТИЧНОЮ таблицею (без місячних колонок; план переносу аркуш не зачіпає —
        // наявна LP-32 його не рахує), таблиці в якорі немає, травень і жовтень V = 55 (згодні). Аркуш якоря (209901)
        // поданий/затверджений. Без замка крок створив би екземпляр, рядок і комірку; із замком — sheetsLocked, жодного запису.
        // Мутаційний доказ: у MigrateDocumentVersionHandler.ClassifyLocked умова `anchorWritten?.Contains(...) == true` →
        // `(... == true && l.PeriodKey < 0)` — поданий якір не зачеплений, перенос проходить і пише в поданий аркуш: червоний.
        (int Sheet, int Table, int Column, int Row) target = default;
        var y = await ArrangeYearAsync(targetMonths: 12, (db, versionId) => target = AddStaticStructure(db, versionId))
            .ConfigureAwait(true);
        try
        {
            var stat = await AddStaticSheetAsync(y, target, [209905, 209910], 55m).ConfigureAwait(true);
            await using (var db = y.Builder.CreateContext())
            {
                var now = new DateTime(2026, 11, 18, 1, 0, 0, DateTimeKind.Utc);
                var state = new Ecr.Domain.Entities.Workflow.ApprovalState(y.Doc.DocumentId, stat.SourceSheetId, YearAnchor);
                state.Submit(1, now);
                if (approved)
                {
                    state.Approve(1, now.AddMinutes(5));
                }

                db.ApprovalStates.Add(state);
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);

            var dry = await PostYearAsync(client, y, dryRun: true).ConfigureAwait(true);
            var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
            var report = JsonDocument.Parse(dryBody).RootElement;
            Assert.False(report.GetProperty("canApply").GetBoolean(), dryBody);
            Assert.Contains("sheetsLocked", report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
            Assert.Equal(1, report.GetProperty("lockedSheetsAffected").GetInt32());

            var refused = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            await AssertProblemAsync(
                refused, HttpStatusCode.Conflict, "ECR-DOC-0409", "err.ECR-DOC-0409.migrateSheetsLocked").ConfigureAwait(true);

            // Нічого не записано: версія та сама, у якорі немає ні екземпляра статичної таблиці (вихідної чи цільової), ні V.
            Assert.Equal(y.SourceVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));
            await using var check = y.Builder.CreateContext();
            Assert.False(await check.TableInstances.AsNoTracking()
                .AnyAsync(t => t.PeriodKeyValue == YearAnchor && t.DocumentId == y.Doc.DocumentId
                               && (t.TableDefId == stat.SourceTableId || t.TableDefId == stat.TargetTableId)).ConfigureAwait(true));
            Assert.False(await check.CellValues.AsNoTracking()
                .AnyAsync(c => c.PeriodKeyValue == YearAnchor && (c.ColumnDefId == stat.SourceColumnId || c.ColumnDefId == stat.TargetColumnId))
                .ConfigureAwait(true));
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
    public async Task Перехід_на_рік_створює_в_якорі_відсутній_екземпляр_статичної_таблиці_з_усіма_фіксованими_рядками()
    {
        // (f) Як S08 Land doc 96: аркуш STATB у складі документа ніколи не відкривали в січні — екземпляра T8B у якорі
        // немає, значень у місяцях теж (крок копії тут нічого не робить). Перехід на рік створює екземпляр з фіксованими
        // рядками, щоб перерахунок року одразу після переносу його заповнив.
        // Мутаційний доказ: у AnchorFillSql `INSERT doc.TableInstance ... FROM @missingInst m` → `... FROM @missingInst m
        // WHERE 1 = 0` — T2 відкочується (екземплярів 0 замість 1, ECR-CELL-0409), тест червоний.
        var y = await ArrangeYearAsync(targetMonths: 12, (db, versionId) => _ = AddStaticStructure(db, versionId))
            .ConfigureAwait(true);
        try
        {
            var target = await StaticTargetAsync(y).ConfigureAwait(true);
            await AddStaticSheetAsync(y, target, [], 0m).ConfigureAwait(true);

            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);

            var dry = await PostYearAsync(client, y, dryRun: true).ConfigureAwait(true);
            var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
            var report = JsonDocument.Parse(dryBody).RootElement;
            Assert.True(report.GetProperty("canApply").GetBoolean(), dryBody);
            Assert.Equal(1, report.GetProperty("anchorInstancesCreated").GetInt64());
            Assert.Equal(0, report.GetProperty("nonMonthAnchorRowsCreated").GetInt64());

            var applied = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body} {app.ErrorsText}");
            Assert.Equal(1, JsonDocument.Parse(body).RootElement.GetProperty("anchorInstancesCreated").GetInt64());
            Assert.Equal(y.TargetVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));

            await using var db = y.Builder.CreateContext();
            var instance = await db.TableInstances.AsNoTracking()
                .SingleAsync(t => t.PeriodKeyValue == YearAnchor && t.DocumentId == y.Doc.DocumentId && t.TableDefId == target.Table)
                .ConfigureAwait(true);
            Assert.Null(instance.SheetCopyId);
            var rows = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == YearAnchor && r.TableInstanceId == instance.Id && !r.IsDeleted)
                .Select(r => new { r.RowKeyValue, r.RowDefId, r.Ordinal }).ToListAsync().ConfigureAwait(true);
            var row = Assert.Single(rows);   // усі (тут — один) живі RowDef таблиці цільової версії
            Assert.Equal((StaticRowKey, (int?)target.Row, 1), (row.RowKeyValue, row.RowDefId, row.Ordinal));
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
    public async Task Крок_якоря_після_застосування_ідемпотентний_нічого_не_створює_вдруге()
    {
        // (g) Після переносу (екземпляр T8B створено) повторний розрахунок кроку тим самим планом — як відновлення переносу
        // чи повторне застосування — дає 0 екземплярів, 0 рядків і 0 комірок.
        var y = await ArrangeYearAsync(targetMonths: 12, (db, versionId) => _ = AddStaticStructure(db, versionId))
            .ConfigureAwait(true);
        try
        {
            var target = await StaticTargetAsync(y).ConfigureAwait(true);
            await AddStaticSheetAsync(y, target, [], 0m).ConfigureAwait(true);

            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);

            // План — тим самим планувальником, що й обробник (до переносу: вихідна версія → річна).
            VersionMigrationPlan plan;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var cache = scope.ServiceProvider.GetRequiredService<IMetadataCache>();
                var from = await cache.GetAsync(y.SourceVersionId, CancellationToken.None).ConfigureAwait(true);
                var to = await cache.GetAsync(y.TargetVersionId, CancellationToken.None).ConfigureAwait(true);
                await using var db0 = y.Builder.CreateContext();
                var usage = await new DocumentVersionMigrationStore(db0).ReadScopeAsync(y.Doc.ProjectId, CancellationToken.None).ConfigureAwait(true);
                plan = VersionMigrationPlanner.Plan(from, to, usage.Cells, usage.HeaderValues, monthColumns: true) with
                {
                    AnchorColumns = [.. to.Sheets.SelectMany(s => s.Tables).SelectMany(t => t.Columns)
                        .Where(c => !c.IsDeleted && !c.IsMonthColumn && !c.IsComputed).Select(c => c.Id).Order()],
                };
            }

            await using (var db1 = y.Builder.CreateContext())
            {
                var before = await new DocumentVersionMigrationStore(db1).ReadAnchorFillAsync(y.Doc.ProjectId, plan, 10, CancellationToken.None)
                    .ConfigureAwait(true);
                Assert.Equal(1, before.InstancesCreated);
            }

            var applied = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body} {app.ErrorsText}");

            await using var db = y.Builder.CreateContext();
            var again = await new DocumentVersionMigrationStore(db).ReadAnchorFillAsync(y.Doc.ProjectId, plan, 10, CancellationToken.None)
                .ConfigureAwait(true);
            Assert.Equal((0L, 0L, 0L), (again.InstancesCreated, again.RowsCreated, again.Copies));
            Assert.Equal(1, await db.TableInstances.AsNoTracking()
                .CountAsync(t => t.PeriodKeyValue == YearAnchor && t.DocumentId == y.Doc.DocumentId && t.TableDefId == target.Table)
                .ConfigureAwait(true));
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
    public async Task Поданий_якір_без_екземпляра_відмова_sheetsLocked_екземпляр_не_створено()
    {
        // (h) Як (f), але аркуш STATB якоря поданий: створення екземпляра — запис у поданий аркуш → sheetsLocked, нічого.
        var y = await ArrangeYearAsync(targetMonths: 12, (db, versionId) => _ = AddStaticStructure(db, versionId))
            .ConfigureAwait(true);
        try
        {
            var target = await StaticTargetAsync(y).ConfigureAwait(true);
            var stat = await AddStaticSheetAsync(y, target, [], 0m).ConfigureAwait(true);
            await using (var db = y.Builder.CreateContext())
            {
                var state = new Ecr.Domain.Entities.Workflow.ApprovalState(y.Doc.DocumentId, stat.SourceSheetId, YearAnchor);
                state.Submit(1, new DateTime(2026, 11, 18, 2, 0, 0, DateTimeKind.Utc));
                db.ApprovalStates.Add(state);
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);

            var dry = await PostYearAsync(client, y, dryRun: true).ConfigureAwait(true);
            var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
            var report = JsonDocument.Parse(dryBody).RootElement;
            Assert.False(report.GetProperty("canApply").GetBoolean(), dryBody);
            Assert.Contains("sheetsLocked", report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
            Assert.Equal(1, report.GetProperty("lockedSheetsAffected").GetInt32());

            var refused = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            await AssertProblemAsync(
                refused, HttpStatusCode.Conflict, "ECR-DOC-0409", "err.ECR-DOC-0409.migrateSheetsLocked").ConfigureAwait(true);

            Assert.Equal(y.SourceVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));
            await using var check = y.Builder.CreateContext();
            Assert.False(await check.TableInstances.AsNoTracking()
                .AnyAsync(t => t.PeriodKeyValue == YearAnchor && t.DocumentId == y.Doc.DocumentId
                               && (t.TableDefId == stat.SourceTableId || t.TableDefId == stat.TargetTableId)).ConfigureAwait(true));
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
    public async Task P3_таблицю_з_прив_язкою_дозволу_рядки_перенос_у_якорі_не_створює()
    {
        // F13 AU P3: T8B цільової версії має активну прив'язку дозволу «рядки» (Mode = 0) — рядки з дозволу планує лише шлях
        // відкриття, тож перенос екземпляра не створює (його створить перше відкриття). Мутація: умову
        // `NOT EXISTS (... cfg.PermitSheetBinding ...)` у @missingInst → `1 = 1` — екземпляр створено, тест червоний.
        var y = await ArrangeYearAsync(targetMonths: 12, (db, versionId) =>
            {
                var t = AddStaticStructure(db, versionId);
                var tag = versionId.ToString(CultureInfo.InvariantCulture);
                var header = new RegistryDef(EcrCode.Create($"PRM_P3H{tag}"), Name("P3 permits"), isTemporal: true);
                var rows = new RegistryDef(EcrCode.Create($"PRM_P3R{tag}"), Name("P3 rows"), isTemporal: false);
                db.RegistryDefs.AddRange(header, rows);
                db.SaveChanges();
                var type = new PermitTypeDef(EcrCode.Create($"P3T{tag}"), Name("P3 type"), PermitCategory.Air, header.Id);
                db.PermitTypeDefs.Add(type);
                db.SaveChanges();
                var typeTable = new PermitTypeTable(type.Id, EcrCode.Create($"P3TT{tag}"), Name("P3 table"), 1, rows.Id);
                db.PermitTypeTables.Add(typeTable);
                db.SaveChanges();
                db.PermitSheetBindings.Add(new PermitSheetBinding(
                    EcrCode.Create($"PMB{tag}"), versionId, t.Table, type.Id, typeTable.Id,
                    PermitBindingMode.Rows, PermitSelectionRule.ByFacilityPeriodEnd, null));
                db.SaveChanges();
            })
            .ConfigureAwait(true);
        try
        {
            var target = await StaticTargetAsync(y).ConfigureAwait(true);
            await AddStaticSheetAsync(y, target, [], 0m).ConfigureAwait(true);

            var (dry, applied, app) = await DryAndApplyAsync(y).ConfigureAwait(true);
            using (app)
            {
                Assert.Equal(0, dry.GetProperty("anchorInstancesCreated").GetInt64());
                Assert.Equal(0, applied.GetProperty("anchorInstancesCreated").GetInt64());
            }

            Assert.Equal(0, await AnchorInstanceCountAsync(y, target.Table).ConfigureAwait(true));
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
    public async Task P4_заархівований_якір_без_гарячих_екземплярів_перенос_нічого_не_створює()
    {
        // F13 AU P4 (F-13): у якорі документа немає жодного гарячого екземпляра, але є arc.TableInstance — рік заархівовано,
        // перенос не фабрикує нових порожніх екземплярів. Мутація: `NOT (NOT EXISTS (... doc.TableInstance h ...` →
        // `NOT (1 = 0 AND ...` — екземпляр таблиці сценарію створено в якорі, тест червоний. Немісячних значень у місяцях
        // немає (C2 травня прибрано), щоб крок копії не створював нічого сам.
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            await ArchiveAnchorAsync(y, removeC2: true).ConfigureAwait(true);

            var (dry, applied, app) = await DryAndApplyAsync(y).ConfigureAwait(true);
            using (app)
            {
                Assert.Equal(0, dry.GetProperty("anchorInstancesCreated").GetInt64());
                Assert.Equal(0, applied.GetProperty("anchorInstancesCreated").GetInt64());
            }

            await using var check = y.Builder.CreateContext();
            Assert.False(await check.TableInstances.AsNoTracking()
                .AnyAsync(t => t.PeriodKeyValue == YearAnchor && t.DocumentId == y.Doc.DocumentId).ConfigureAwait(true));
        }
        finally
        {
            await UnarchiveAnchorAsync(y).ConfigureAwait(true);
            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Task", "LAND-YEAR-L3-STATIC")]
    public async Task P4b_заархівований_якір_з_немісячними_значеннями_крок_копії_пропускає_документ_B69()
    {
        // ⛔ B69 «архів незмінний»: як P4, але в місяцях є що копіювати (C2 = 55 у травні й жовтні). Крок копії мусить
        // пропустити документ цілком (0 екземплярів, 0 рядків, 0 комірок) і сказати про це (nonMonthSkippedArchived = 1);
        // версія перемикається. Мутація: прибрати `DELETE mi FROM @monthInst mi WHERE EXISTS (@archivedDocs ...)` —
        // крок копії створює екземпляр, рядки й комірку в заархівованому якорі, тест червоний.
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            await ArchiveAnchorAsync(y, removeC2: false).ConfigureAwait(true);
            await PutC2Async(y, 209910, rowIndex: 0, 55m).ConfigureAwait(true);

            var (dry, applied, app) = await DryAndApplyAsync(y).ConfigureAwait(true);
            using (app)
            {
                foreach (var report in new[] { dry, applied })
                {
                    Assert.Equal(0, report.GetProperty("nonMonthCopiedToAnchor").GetInt64());
                    Assert.Equal(0, report.GetProperty("nonMonthAnchorRowsCreated").GetInt64());
                    Assert.Equal(0, report.GetProperty("anchorInstancesCreated").GetInt64());
                    Assert.Equal(1, report.GetProperty("nonMonthSkippedArchived").GetInt64());
                }
            }

            // Версія перемкнулась (DryAndApplyAsync), а в якорі документа — жодного гарячого екземпляра, рядка чи комірки.
            await using var check = y.Builder.CreateContext();
            Assert.False(await check.TableInstances.AsNoTracking()
                .AnyAsync(t => t.PeriodKeyValue == YearAnchor && t.DocumentId == y.Doc.DocumentId).ConfigureAwait(true));
            Assert.Equal(0, await check.Database
                .SqlQuery<int>($"""
                    SELECT COUNT(*) AS Value FROM doc.TableRow r
                    WHERE r.PeriodKey = {YearAnchor}
                      AND NOT EXISTS (SELECT 1 FROM doc.TableInstance ti WHERE ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId)
                    """).SingleAsync().ConfigureAwait(true));
            Assert.False(await check.CellValues.AsNoTracking()
                .AnyAsync(c => c.PeriodKeyValue == YearAnchor && (c.ColumnDefId == y.TargetC2 || c.ColumnDefId == y.Doc.ColumnDefIds[1]))
                .ConfigureAwait(true));
        }
        finally
        {
            await UnarchiveAnchorAsync(y).ConfigureAwait(true);
            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// «Архівує» якір документа (F-13): прибирає гарячий екземпляр таблиці якоря з рядками й комірками і кладе рядок у
    /// arc.TableInstance; <paramref name="removeC2"/> — ще й усі значення C2 (крок копії не має джерела).
    /// </summary>
    private static async Task ArchiveAnchorAsync(YearScenario y, bool removeC2)
    {
        var instanceId = y.Doc.TableInstanceId;
        await using var db = y.Builder.CreateContext();
        if (removeC2)
        {
            var sourceC2 = y.Doc.ColumnDefIds[1];
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM doc.CellValue WHERE ColumnDefId = {sourceC2};").ConfigureAwait(false);
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE cv FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
             WHERE r.PeriodKey = {YearAnchor} AND r.TableInstanceId = {instanceId};
            DELETE cs FROM doc.CellStyle cs JOIN doc.TableRow r ON r.PeriodKey = cs.PeriodKey AND r.Id = cs.TableRowId
             WHERE r.PeriodKey = {YearAnchor} AND r.TableInstanceId = {instanceId};
            DELETE fo FROM doc.CellFormulaOverride fo JOIN doc.TableRow r ON r.PeriodKey = fo.PeriodKey AND r.Id = fo.TableRowId
             WHERE r.PeriodKey = {YearAnchor} AND r.TableInstanceId = {instanceId};
            DELETE FROM doc.TableRow WHERE PeriodKey = {YearAnchor} AND TableInstanceId = {instanceId};
            DELETE FROM doc.TableInstance WHERE PeriodKey = {YearAnchor} AND Id = {instanceId};
            INSERT arc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
            VALUES ({YearAnchor}, {instanceId}, {y.Doc.DocumentId}, {y.Doc.TableDefId}, SYSUTCDATETIME(), SYSUTCDATETIME());
            """).ConfigureAwait(false);
        Assert.False(await db.TableInstances.AsNoTracking()
            .AnyAsync(t => t.PeriodKeyValue == YearAnchor && t.DocumentId == y.Doc.DocumentId).ConfigureAwait(false));
    }

    /// <summary>Прибирає рядок arc.TableInstance якоря, покладений <see cref="ArchiveAnchorAsync"/>.</summary>
    private static async Task UnarchiveAnchorAsync(YearScenario y)
    {
        await using var db = y.Builder.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM arc.TableInstance WHERE PeriodKey = {YearAnchor} AND DocumentId = {y.Doc.DocumentId}").ConfigureAwait(false);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Task", "LAND-YEAR-L3-STATIC")]
    public async Task P5_копія_аркуша_дістає_в_якорі_власний_екземпляр_поруч_з_оригіналом()
    {
        // F13 AU P5 (RS): STATB у документі — оригінал і копія; у якорі обидва без екземпляра T8B. Перенос створює ДВА:
        // оригінал (SheetCopyId NULL) і копію (SheetCopyId = Id рядка складу копії). Мутація: `CASE WHEN s.CopyOfId IS NULL
        // THEN 0 ELSE s.Id END` → `0` — копія злипається з оригіналом (1 екземпляр), тест червоний.
        var y = await ArrangeYearAsync(targetMonths: 12, (db, versionId) => _ = AddStaticStructure(db, versionId))
            .ConfigureAwait(true);
        try
        {
            var target = await StaticTargetAsync(y).ConfigureAwait(true);
            var stat = await AddStaticSheetAsync(y, target, [], 0m).ConfigureAwait(true);
            long copyId;
            await using (var db = y.Builder.CreateContext())
            {
                var original = await db.DocumentSheets
                    .SingleAsync(s => s.DocumentId == y.Doc.DocumentId && s.SheetDefId == stat.SourceSheetId && s.CopyOfId == null)
                    .ConfigureAwait(true);
                var copy = original.CreateCopy(2);
                db.DocumentSheets.Add(copy);
                await db.SaveChangesAsync().ConfigureAwait(true);
                copyId = copy.Id;
            }

            var (dry, applied, app) = await DryAndApplyAsync(y).ConfigureAwait(true);
            using (app)
            {
                Assert.Equal(2, dry.GetProperty("anchorInstancesCreated").GetInt64());
                Assert.Equal(2, applied.GetProperty("anchorInstancesCreated").GetInt64());
            }

            await using var check = y.Builder.CreateContext();
            var copies = await check.TableInstances.AsNoTracking()
                .Where(t => t.PeriodKeyValue == YearAnchor && t.DocumentId == y.Doc.DocumentId && t.TableDefId == target.Table)
                .Select(t => t.SheetCopyId).ToListAsync().ConfigureAwait(true);
            Assert.Equal(new long?[] { null, copyId }, copies.OrderBy(c => c ?? 0).ToArray());
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
    public async Task P9_динамічна_таблиця_зі_значенням_без_екземпляра_якоря_рівно_один_екземпляр_і_лічильники_як_у_плані()
    {
        // F13 AU P9: динамічна T8B (без описів рядків), травень і жовтень рядок D1 з VB = 55, у якорі екземпляра немає. Його
        // створює крок (a′), а рядок D1 — крок копії; @needInst не мусить рахувати екземпляр удруге. Мутація: фільтр
        // `NOT EXISTS (@missingInst ...)` у @needInst → `1 = 0` — сухий прогін каже 2 екземпляри, застосування — 1:
        // 409 ECR-CELL-0409, тест червоний.
        var y = await ArrangeYearAsync(targetMonths: 12, (db, versionId) => _ = AddStaticStructure(db, versionId, dynamic: true))
            .ConfigureAwait(true);
        try
        {
            var target = await StaticTargetAsync(y).ConfigureAwait(true);
            await AddStaticSheetAsync(y, target, [209905, 209910], 55m, dynamic: true).ConfigureAwait(true);

            var (dry, applied, app) = await DryAndApplyAsync(y).ConfigureAwait(true);
            using (app)
            {
                foreach (var report in new[] { dry, applied })
                {
                    Assert.Equal(1, report.GetProperty("anchorInstancesCreated").GetInt64());
                    Assert.Equal(1, report.GetProperty("nonMonthAnchorRowsCreated").GetInt64());
                    Assert.Equal(1, report.GetProperty("nonMonthCopiedToAnchor").GetInt64());
                }
            }

            Assert.Equal(1, await AnchorInstanceCountAsync(y, target.Table).ConfigureAwait(true));
            await using var check = y.Builder.CreateContext();
            var instance = await check.TableInstances.AsNoTracking()
                .Where(t => t.PeriodKeyValue == YearAnchor && t.DocumentId == y.Doc.DocumentId && t.TableDefId == target.Table)
                .Select(t => t.Id).SingleAsync().ConfigureAwait(true);
            var row = await check.TableRows.AsNoTracking()
                .SingleAsync(r => r.PeriodKeyValue == YearAnchor && r.TableInstanceId == instance && !r.IsDeleted).ConfigureAwait(true);
            Assert.Equal(DynamicRowKey, row.RowKeyValue);
            Assert.Equal(55m, (await check.CellValues.AsNoTracking()
                .SingleAsync(c => c.PeriodKeyValue == YearAnchor && c.TableRowId == row.Id && c.ColumnDefId == target.Column)
                .ConfigureAwait(true)).ValueNumeric);
        }
        finally
        {
            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }

    // ───────────────────────────── допоміжне (B-static) ─────────────────────────────

    /// <summary>Сухий прогін і перенос (обидва 200); звіти й застосунок (викликач звільняє).</summary>
    private async Task<(JsonElement Dry, JsonElement Applied, EcrApiFactory App)> DryAndApplyAsync(YearScenario y)
    {
        var app = CreateYearApp();
        using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(false);
        var dry = await PostYearAsync(client, y, dryRun: true).ConfigureAwait(false);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
        var report = JsonDocument.Parse(dryBody).RootElement.Clone();
        Assert.True(report.GetProperty("canApply").GetBoolean(), dryBody);

        var applied = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(false);
        var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body} {app.ErrorsText}");
        Assert.Equal(y.TargetVersionId, await YearProjectVersionAsync(y).ConfigureAwait(false));
        return (report, JsonDocument.Parse(body).RootElement.Clone(), app);
    }

    /// <summary>Скільки екземплярів таблиці <paramref name="tableDefId"/> документа в якорі.</summary>
    private static async Task<int> AnchorInstanceCountAsync(YearScenario y, int tableDefId)
    {
        await using var db = y.Builder.CreateContext();
        return await db.TableInstances.AsNoTracking()
            .CountAsync(t => t.PeriodKeyValue == YearAnchor && t.DocumentId == y.Doc.DocumentId && t.TableDefId == tableDefId)
            .ConfigureAwait(false);
    }

    /// <summary>Ідентифікатори статичного аркуша STATB цільової версії (додано до публікації в ArrangeYearAsync).</summary>
    private static async Task<(int Sheet, int Table, int Column, int Row)> StaticTargetAsync(YearScenario y)
    {
        await using var db = y.Builder.CreateContext();
        var sheet = await db.SheetDefs.AsNoTracking()
            .Where(s => s.TemplateVersionId == y.TargetVersionId && s.Code == "STATB").Select(s => s.Id).SingleAsync().ConfigureAwait(false);
        var table = await db.TableDefs.AsNoTracking().Where(t => t.SheetDefId == sheet).Select(t => t.Id).SingleAsync().ConfigureAwait(false);
        var column = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == table).Select(c => c.Id).SingleAsync().ConfigureAwait(false);
        var row = await db.RowDefs.AsNoTracking().Where(r => r.TableDefId == table).Select(r => r.Id).SingleOrDefaultAsync().ConfigureAwait(false);
        return (sheet, table, column, row);
    }

    /// <summary>Ідентифікатори статичного аркуша (e): аркуш/таблиця/колонка вихідної й цільової версій.</summary>
    private sealed record StaticSheet(int SourceSheetId, int SourceTableId, int SourceColumnId, int TargetTableId, int TargetColumnId);

    /// <summary>
    /// Статичний аркуш (e): аркуш STATB зі статичною фіксованою таблицею T8B (колонка VB, рядок RSB1) у версії
    /// <paramref name="versionId"/> — однакові коди в обох версіях (план переносить 1:1 і аркуш не зачіпає).
    /// </summary>
    private static (int Sheet, int Table, int Column, int Row) AddStaticStructure(
        Ecr.Infrastructure.Persistence.EcrDbContext db, int versionId, bool dynamic = false)
    {
        var sheet = new SheetDef(versionId, EcrCode.Create("STATB"), Name("Static B"), 9);
        db.SheetDefs.Add(sheet);
        db.SaveChanges();
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T8B"), Name("T8 B"), 1, TableLayoutKind.Static, dynamic ? TableRowMode.Dynamic : TableRowMode.Fixed);
        db.TableDefs.Add(table);
        db.SaveChanges();
        var column = new ColumnDef(table.Id, EcrCode.Create("VB"), Name("V"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column);
        RowDef? row = null;
        if (!dynamic)
        {
            // Динамічна таблиця (P9) описів рядків не має: рядки заводить оператор.
            row = new RowDef(table.Id, RowKey.Create(StaticRowKey), 1, Name("Row S1"), RowKind.Item);
            db.RowDefs.Add(row);
        }

        db.SaveChanges();
        return (sheet.Id, table.Id, column.Id, row?.Id ?? 0);
    }

    private const string StaticRowKey = "RSB1";

    /// <summary>Ключ рядка динамічної T8B (P9) — без опису рядка.</summary>
    private const string DynamicRowKey = "D1";

    /// <summary>
    /// Додає статичний аркуш у вихідну версію (ціль — <paramref name="target"/>, додана до публікації), вмикає його в
    /// документ і кладе VB = <paramref name="value"/> у екземпляри періодів <paramref name="periods"/> (якір — без екземпляра).
    /// </summary>
    private static async Task<StaticSheet> AddStaticSheetAsync(
        YearScenario y, (int Sheet, int Table, int Column, int Row) target, int[] periods, decimal value, bool dynamic = false)
    {
        await using var db = y.Builder.CreateContext();
        var source = AddStaticStructure(db, y.SourceVersionId, dynamic);

        var document = await db.Documents.Include(d => d.Sheets).SingleAsync(d => d.Id == y.Doc.DocumentId).ConfigureAwait(false);
        document.IncludeSheet(source.Sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var rowKey = dynamic ? DynamicRowKey : StaticRowKey;
        int? rowDefId = dynamic ? null : source.Row;
        foreach (var period in periods)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @i bigint = NEXT VALUE FOR doc.TableInstanceSeq;
                DECLARE @r bigint = NEXT VALUE FOR doc.TableRowSeq;
                INSERT doc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt, SheetCopyId)
                VALUES ({period}, @i, {y.Doc.DocumentId}, {source.Table}, SYSUTCDATETIME(), SYSUTCDATETIME(), NULL);
                INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, IsOrphaned, ModifiedAt)
                VALUES ({period}, @r, @i, {rowKey}, {rowDefId}, 1, 0, 0, SYSUTCDATETIME());
                INSERT doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                VALUES ({period}, @r, {source.Column}, {source.Table}, {value}, 0, 0);
                """).ConfigureAwait(false);
        }

        return new StaticSheet(source.Sheet, source.Table, source.Column, target.Table, target.Column);
    }

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
