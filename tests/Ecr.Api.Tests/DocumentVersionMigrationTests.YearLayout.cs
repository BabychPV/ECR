// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.YearLayout.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ LAND-YEAR L3: перенос місячного проєкту з версії «копія на місяць» (<c>PerMonthCopies</c>) на версію «рік, місяці в
/// колонках» (<c>YearWithMonthColumns</c>) — як Land doc 96 з 1.0.16 на 1.0.17. Справжній HTTP, справжній SQL Server,
/// читання назад — справжнім шляхом застосунку (<c>GET /documents/{id}/tables/{anchor}</c>, маршрутизоване сховище).
/// </summary>
/// <remarks>
/// <para>
/// Документ 2099: календар 209901..209912 (усі Closed — перерахунок їх не пише, тож видно лише те, що переніс перенос),
/// екземпляр на кожен місяць. Вихідна таблиця «місяці в колонках»: C1 (текст), C2 (немісячний ввід, якір 7, копія
/// 209905 = 55), M1..M12 (місячний ввід; M9 у 209909 = 9), F (немісячна формула, «змінна» — у місяці m значення m×10),
/// S (селектор «поточного місяця», m×100). Цільова: C1, C2, M1..M12, F_M01..F_M12 (місячні формули з ручним значенням),
/// без F і S; псевдоніми S → M<i>m</i> (як будівник Land, етап YearMonths).
/// </para>
/// <para>
/// Мутаційні докази (кожна збирається і червонить свій тест): (1) у <c>DocumentVersionMigrationStore.RangeCommandText</c>
/// <c>mm.m = cv.PeriodKey % 100</c> → <c>mm.m = 1</c> — значення 209909 не в F_M09 (тест «на місці» і «повтор»); (2) у
/// <c>VersionMigrationPlanner.TryMapMonthColumns</c> <c>if (values &gt; 0)</c> → <c>if (values &lt; 0)</c> — відмова без
/// переліку (тест «відмова»); (3) у DELETE комірок прибрати <c>mm.n = cv.ColumnDefId OR</c> — перенесене видаляється
/// (тест «повтор» і «на місці»); (4) у <c>MigrateDocumentVersionHandler.ResolveAsync</c> право <c>Permission</c> →
/// <c>"Document.View"</c> — редактор документа дістає 200 (тест пари доступу).
/// </para>
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private const string YearRoutingFlag = "ECR_Collection__MonthColumnRouting";
    private const int YearAnchor = 209901;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Task", "LAND-YEAR-L3")]
    public async Task Копія_на_місяць_на_рік_переносить_C_періоду_в_C_Mmm_на_місці_і_річний_зріз_бачить_усі_місяці()
    {
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);

            // ── Сухий прогін: 12 значень F переїде в місячні колонки, 12 значень селектора — перерахується, втрат немає.
            var dry = await PostYearAsync(client, y, dryRun: true).ConfigureAwait(true);
            var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
            var report = JsonDocument.Parse(dryBody).RootElement;
            Assert.True(report.GetProperty("canApply").GetBoolean(), dryBody);
            Assert.Equal(0, report.GetProperty("lostValues").GetInt64());
            Assert.Equal(12, report.GetProperty("monthTransferredValues").GetInt64());
            Assert.Equal(12, report.GetProperty("recomputedValues").GetInt64());
            Assert.Equal(12 + 2 + 1, report.GetProperty("transferredValues").GetInt64());   // F + C2 (дві копії) + M9
            Assert.Equal(JsonValueKind.Null, report.GetProperty("monthColumnsUnmapped").ValueKind);
            var kinds = report.GetProperty("items").EnumerateArray()
                .Select(i => $"{i.GetProperty("kind").GetString()}:{i.GetProperty("path").GetString()}").ToList();
            Assert.Contains($"{VersionMigrationPlanner.MonthColumnsKind}:{y.TablePath}.{y.FCode}", kinds);
            Assert.Contains($"{VersionMigrationPlanner.RecomputedKind}:{y.TablePath}.{y.SCode}", kinds);
            Assert.Equal(y.SourceVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));   // сухий прогін нічого не міняє

            // ── Перенос.
            var applied = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body} {app.ErrorsText}");
            Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("applied").GetBoolean());
            Assert.Equal(y.TargetVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));

            await AssertMovedInPlaceAsync(y).ConfigureAwait(true);

            // ── Справжній шлях читання річної версії: зріз ЯКОРЯ показує F_Mmm кожного місяця від власника (екземпляр m).
            await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(true);   // кеш маркерів міграції
            var cells = await ReadYearRowAsync(app, client, y).ConfigureAwait(true);
            for (var m = 1; m <= 12; m++)
            {
                var code = y.FMonthCodes[m];
                Assert.True(cells.TryGetProperty(code, out var cell), $"{code}: у річному зрізі немає значення. {cells.GetRawText()}");
                Assert.Equal(m * 10m, decimal.Parse(cell.GetString()!, CultureInfo.InvariantCulture));
            }

            // Немісячний ввід — значення якоря (7), копія 209905 (55) у річному перегляді не з'являється.
            Assert.Equal(7m, decimal.Parse(cells.GetProperty(y.C2Code).GetString()!, CultureInfo.InvariantCulture));
            Assert.False(cells.TryGetProperty(y.FCode, out _), "колонки F у річній версії немає");

            // ── Журнал безпеки несе мапу місяців (план §d.3): o = F, m, n = F_Mmm.
            await using (var db = y.Builder.CreateContext())
            {
                var details = await db.Database
                    .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentVersionMigrated' AND ChangedByUserId = {y.UserId}")
                    .ToListAsync().ConfigureAwait(true);
                var map = JsonDocument.Parse(Assert.Single(details)).RootElement.GetProperty("monthColumns").EnumerateArray()
                    .Select(e => (O: e.GetProperty("o").GetInt32(), M: e.GetProperty("m").GetInt32(), N: e.GetProperty("n").GetInt32()))
                    .ToList();
                Assert.Equal(Enumerable.Range(1, 12).Select(m => (y.SourceF, m, y.TargetFMonths[m])).ToList(), map);
            }

            // ── Перерахунок року поставлено завжди (закриті місяці він не пише — їхні значення лишились перенесеними).
            Assert.True(await YearRecalcQueuedAsync(y).ConfigureAwait(true) >= 1, "перерахунок після переносу на рік не поставлено");
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
    [Trait("Task", "LAND-YEAR-L3")]
    public async Task Змінна_колонка_без_місячної_цілі_відмовляє_з_переліком_і_нічого_не_змінює()
    {
        // Цільова версія має лише F_M01..F_M11: значенню грудня місця немає.
        var y = await ArrangeYearAsync(targetMonths: 11).ConfigureAwait(true);
        try
        {
            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);

            var dry = await PostYearAsync(client, y, dryRun: true).ConfigureAwait(true);
            var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody}");
            var report = JsonDocument.Parse(dryBody).RootElement;
            Assert.False(report.GetProperty("canApply").GetBoolean());
            Assert.Contains("dataLoss", report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
            Assert.Equal(12, report.GetProperty("lostValues").GetInt64());
            Assert.Equal(0, report.GetProperty("monthTransferredValues").GetInt64());
            Assert.Equal([$"{y.TablePath}.{y.FCode}"],
                report.GetProperty("monthColumnsUnmapped").EnumerateArray().Select(p => p.GetString()).ToList());

            var refused = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            var problem = await AssertProblemAsync(
                refused, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateDataLoss").ConfigureAwait(true);
            Assert.Contains($"{y.TablePath}.{y.FCode}", problem, StringComparison.Ordinal);
            Assert.Contains("monthColumnsUnmapped", problem, StringComparison.Ordinal);

            // Нічого не змінено: версія, значення F на місці.
            Assert.Equal(y.SourceVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));
            await using var db = y.Builder.CreateContext();
            Assert.Equal(12, await db.CellValues.AsNoTracking().CountAsync(c => c.ColumnDefId == y.SourceF).ConfigureAwait(true));
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
    [Trait("Task", "LAND-YEAR-L3")]
    public async Task Перенос_на_рік_адміністратору_шаблону_200_редактору_документа_403()
    {
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            // Редактор документа: Document.View/Create і навіть Manage на проєкт, але без Template.Edit.
            var editor = await AddYearUserAsync(y, ["Document.View", "Document.Create"], GrantLevel.Manage).ConfigureAwait(true);

            using var app = CreateYearApp();
            using var admin = await SignedInAsync(app, y.UserName).ConfigureAwait(true);
            using var editorClient = await SignedInAsync(app, editor).ConfigureAwait(true);

            // Allow: адміністратор шаблону — звіт.
            var allowed = await PostYearAsync(admin, y, dryRun: true).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

            // Deny: редактор документа — 403 і на сухий прогін, і на перенос; версія не змінилась.
            foreach (var dryRun in new[] { true, false })
            {
                var denied = await PostYearAsync(editorClient, y, dryRun).ConfigureAwait(true);
                var text = await denied.Content.ReadAsStringAsync().ConfigureAwait(true);
                Assert.True(denied.StatusCode == HttpStatusCode.Forbidden, $"dryRun={dryRun}: {denied.StatusCode}: {text}");
            }

            Assert.Equal(y.SourceVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));
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
    [Trait("Task", "LAND-YEAR-L3")]
    public async Task Повтор_важкої_частини_переносу_на_рік_не_змінює_жодного_рядка()
    {
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            using var app = CreateYearApp();

            // План — тим самим планувальником, що й обробник (знімки метаданих застосунку, підрахунок сховища).
            VersionMigrationPlan plan;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var cache = scope.ServiceProvider.GetRequiredService<IMetadataCache>();
                var from = await cache.GetAsync(y.SourceVersionId, CancellationToken.None).ConfigureAwait(true);
                var to = await cache.GetAsync(y.TargetVersionId, CancellationToken.None).ConfigureAwait(true);
                await using var db0 = y.Builder.CreateContext();
                var usage = await new DocumentVersionMigrationStore(db0).ReadScopeAsync(y.Doc.ProjectId, CancellationToken.None).ConfigureAwait(true);
                plan = VersionMigrationPlanner.Plan(from, to, usage.Cells, usage.HeaderValues, monthColumns: true);
            }

            Assert.Equal(12, plan.MonthColumns.Count);
            Assert.Equal(0, plan.LostValues);

            await using (var db = y.Builder.CreateContext())
            await using (var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true))
            {
                await new DocumentVersionMigrationStore(db)
                    .BeginMigrationAsync(y.Doc.ProjectId, y.TargetVersionId, plan, y.UserId, CancellationToken.None).ConfigureAwait(true);
                await tx.CommitAsync().ConfigureAwait(true);
            }

            async Task<long> MoveAsync()
            {
                var stats = new List<MigrationRangeStat>();
                await using var db = y.Builder.CreateContext();
                var store = new DocumentVersionMigrationStore(db) { RangeObserver = stats.Add };
                await store.MoveDataAsync(y.Doc.ProjectId, y.TargetVersionId, plan, null, CancellationToken.None).ConfigureAwait(true);
                return stats.Where(s => s.Kind == "cells").Sum(s => s.Rows);
            }

            var first = await MoveAsync().ConfigureAwait(true);
            Assert.True(first > 0, "перший прогін мусив перенести значення");

            // Повтор з нуля (watermark стерто): предикати мусять самі виключити вже перенесене.
            await using (var db = y.Builder.CreateContext())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE doc.Project
                    SET    ExternalSettingsJson = JSON_MODIFY(JSON_MODIFY(ExternalSettingsJson, '$.wmCellsPeriod', NULL), '$.wmCellsHi', NULL)
                    WHERE  Id = {y.Doc.ProjectId}
                    """).ConfigureAwait(true);
            }

            Assert.Equal(0, await MoveAsync().ConfigureAwait(true));

            await using (var db = y.Builder.CreateContext())
            await using (var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true))
            {
                await new DocumentVersionMigrationStore(db)
                    .CompleteAsync(y.Doc.ProjectId, y.TargetVersionId, plan, null, null, CancellationToken.None).ConfigureAwait(true);
                await tx.CommitAsync().ConfigureAwait(true);
            }

            Assert.Equal(y.TargetVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));
            await AssertMovedInPlaceAsync(y).ConfigureAwait(true);
        }
        finally
        {
            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    /// <summary>Перевірка в базі: значення, оформлення й ручне значення F кожного місяця — у F_Mmm ТОГО САМОГО екземпляра.</summary>
    private async Task AssertMovedInPlaceAsync(YearScenario y)
    {
        await using var db = y.Builder.CreateContext();

        Assert.Equal(0, await db.CellValues.AsNoTracking().CountAsync(c => c.ColumnDefId == y.SourceF || c.ColumnDefId == y.SourceS).ConfigureAwait(true));
        for (var m = 1; m <= 12; m++)
        {
            var period = (2099 * 100) + m;
            var rowId = y.Row1ByPeriod[period];
            var cell = await db.CellValues.AsNoTracking()
                .SingleAsync(c => c.PeriodKeyValue == period && c.TableRowId == rowId && c.ColumnDefId == y.TargetFMonths[m]).ConfigureAwait(true);
            Assert.Equal(m * 10m, cell.ValueNumeric);
            Assert.Equal(y.TargetTableDefId, cell.TableDefId);

            // Інші місяці в цьому екземплярі місця не мають: значення лише в колонці СВОГО місяця.
            var others = y.TargetFMonths.Where(p => p.Key != m).Select(p => p.Value).ToList();
            Assert.False(await db.CellValues.AsNoTracking()
                .AnyAsync(c => c.PeriodKeyValue == period && c.TableRowId == rowId && others.Contains(c.ColumnDefId)).ConfigureAwait(true));
        }

        // Закритий місяць (209903): значення пережило перенос без перерахунку, місяць лишився закритим.
        var march = await db.Periods.AsNoTracking()
            .SingleAsync(p => p.ProjectId == y.Doc.ProjectId && p.PeriodKeyValue == 209903).ConfigureAwait(true);
        Assert.Equal(PeriodState.Closed, march.State);

        // Оформлення й ручне значення 209909 — на F_M09 того самого рядка.
        var september = y.Row1ByPeriod[209909];
        var style = await db.CellStyles.AsNoTracking().SingleAsync(c => c.PeriodKeyValue == 209909 && c.TableRowId == september).ConfigureAwait(true);
        Assert.Equal(y.TargetFMonths[9], style.ColumnDefId);
        var manual = await db.CellFormulaOverrides.AsNoTracking().SingleAsync(c => c.PeriodKeyValue == 209909 && c.TableRowId == september).ConfigureAwait(true);
        Assert.Equal(y.TargetFMonths[9], manual.ColumnDefId);

        // Місячний ввід (1:1) і немісячний ввід лишились там, де були (M9 у 209909, C2 у якорі).
        Assert.Equal(9m, (await db.CellValues.AsNoTracking()
            .SingleAsync(c => c.PeriodKeyValue == 209909 && c.TableRowId == september && c.ColumnDefId == y.TargetM9).ConfigureAwait(true)).ValueNumeric);
        var anchorRow = y.Row1ByPeriod[YearAnchor];
        Assert.Equal(7m, (await db.CellValues.AsNoTracking()
            .SingleAsync(c => c.PeriodKeyValue == YearAnchor && c.TableRowId == anchorRow && c.ColumnDefId == y.TargetC2).ConfigureAwait(true)).ValueNumeric);
    }

    private EcrApiFactory CreateYearApp()
    {
        Environment.SetEnvironmentVariable(YearRoutingFlag, "false");
        try
        {
            return new EcrApiFactory(sql);
        }
        finally
        {
            Environment.SetEnvironmentVariable(YearRoutingFlag, null);
        }
    }

    private static Task<HttpResponseMessage> PostYearAsync(HttpClient client, YearScenario y, bool dryRun)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{y.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new { targetVersionId = y.TargetVersionId, mode = "Safe", dryRun });

    private static async Task<JsonElement> ReadYearRowAsync(EcrApiFactory app, HttpClient client, YearScenario y)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{y.Doc.DocumentId}/tables/{y.Doc.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}\n{text}\n{app.ErrorsText}");

        return JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("rowKey").GetString() == y.Row1Key).GetProperty("cells").Clone();
    }

    private async Task<int> YearProjectVersionAsync(YearScenario y)
    {
        await using var db = y.Builder.CreateContext();
        return await db.Projects.AsNoTracking().Where(p => p.Id == y.Doc.ProjectId).Select(p => p.TemplateVersionId)
            .SingleAsync().ConfigureAwait(false);
    }

    private async Task<int> YearRecalcQueuedAsync(YearScenario y)
    {
        var target = "%~" + MigrationRecalculation.TargetOf(y.Doc.ProjectId) + "~%";
        await using var db = y.Builder.CreateContext();
        return await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM itg.JobProgress WHERE JobCode LIKE N'%IRecalculationJob' AND JobId LIKE {target}")
            .SingleAsync().ConfigureAwait(false);
    }

    /// <summary>B121: річна розкладка в спільній базі вмикала б роутер усім наступним хостам — повертаємо її назад.</summary>
    private static async Task CleanupYearAsync(YearScenario y)
    {
        await using var db = y.Builder.CreateContext();
        await YearLayoutCleanup.ResetAsync(db, [y.TargetVersionId]).ConfigureAwait(false);
    }

    private sealed record YearScenario(
        TestDocumentBuilder Builder,
        TestDocument Doc,
        string UserName,
        int UserId,
        int SourceVersionId,
        int TargetVersionId,
        int TargetTableDefId,
        string TablePath,
        string FCode,
        string SCode,
        string C2Code,
        int SourceF,
        int SourceS,
        int TargetC2,
        int TargetM9,
        IReadOnlyDictionary<int, int> TargetFMonths,
        IReadOnlyDictionary<int, string> FMonthCodes,
        IReadOnlyDictionary<int, long> Row1ByPeriod,
        string Row1Key);

    /// <summary>Сценарій L3 (див. опис класу); <paramref name="targetMonths"/> — скільки місячних F_Mmm має ціль.</summary>
    private async Task<YearScenario> ArrangeYearAsync(int targetMonths)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            YearAnchor, columnCount: 2, rowCount: 2, rowMode: TableRowMode.Fixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);
        var tag = doc.SheetCode["SHEET".Length..];
        var now = new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);

        var instances = new Dictionary<int, (long Instance, long Row1)> { [YearAnchor] = (doc.TableInstanceId, doc.RowIds[0]) };
        for (var m = 2; m <= 12; m++)
        {
            var added = await builder.AddPeriodInstanceAsync(doc, new PeriodKey((2099 * 100) + m)).ConfigureAwait(false);
            instances[added.PeriodKey.Value] = (added.TableInstanceId, added.RowIds[0]);
        }

        await using var db = builder.CreateContext();

        var document = await db.Documents.Include(d => d.Sheets).SingleAsync(d => d.Id == doc.DocumentId).ConfigureAwait(false);
        document.IncludeSheet(doc.SheetDefId);

        var tableCode = await db.TableDefs.AsNoTracking().Where(t => t.Id == doc.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);
        var fCode = $"F_{tag}";
        var sCode = $"S_{tag}";
        var sourceF = new ColumnDef(doc.TableDefId, EcrCode.Create(fCode), Name("F varying"), 50, CellDataType.Formula);
        var sourceS = new ColumnDef(doc.TableDefId, EcrCode.Create(sCode), Name("S selector"), 51, CellDataType.Formula);
        db.ColumnDefs.AddRange(sourceF, sourceS);

        foreach (var period in await db.Periods.Where(p => p.ProjectId == doc.ProjectId).ToListAsync().ConfigureAwait(false))
        {
            period.AdvanceTo(PeriodState.Closed, now);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        var c2 = doc.ColumnDefIds[1];
        var m9 = doc.MonthColumnDefIds[8];
        foreach (var (period, place) in instances)
        {
            var key = new PeriodKey(period);
            var m = period % 100;
            db.CellValues.Add(new CellValue(
                new CellAddress(key, place.Row1, sourceF.Id), doc.TableDefId, new CellValueData { ValueNumeric = m * 10m, IsCalculated = true }));
            db.CellValues.Add(new CellValue(
                new CellAddress(key, place.Row1, sourceS.Id), doc.TableDefId, new CellValueData { ValueNumeric = m * 100m, IsCalculated = true }));
        }

        // Немісячний ввід: якір 7, копія в травні 55 (тінь D-67); місячний ввід M9 у вересні.
        db.CellValues.Add(new CellValue(new CellAddress(new PeriodKey(YearAnchor), instances[YearAnchor].Row1, c2), doc.TableDefId, new CellValueData { ValueNumeric = 7m }));
        db.CellValues.Add(new CellValue(new CellAddress(new PeriodKey(209905), instances[209905].Row1, c2), doc.TableDefId, new CellValueData { ValueNumeric = 55m }));
        db.CellValues.Add(new CellValue(new CellAddress(new PeriodKey(209909), instances[209909].Row1, m9), doc.TableDefId, new CellValueData { ValueNumeric = 9m }));

        // Оформлення й ручне значення змінної формули у вересні.
        var style = new CellStyle(new CellAddress(new PeriodKey(209909), instances[209909].Row1, sourceF.Id), 1, now);
        style.Apply("#FFEE00", null, true, 1, now);
        db.CellStyles.Add(style);
        db.CellFormulaOverrides.Add(new CellFormulaOverride(
            new CellAddress(new PeriodKey(209909), instances[209909].Row1, sourceF.Id), instances[209909].Instance, "manual", 1, now));
        await db.SaveChangesAsync().ConfigureAwait(false);

        // ── Цільова версія: рік, місяці в колонках ──
        var version = new TemplateVersion(doc.TemplateId, $"2.0.0.{tag}", 1, now, doc.TemplateVersionId);
        Assert.True(version.SetLayout(TemplateLayout.YearWithMonthColumns));
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var sheet = new SheetDef(version.Id, EcrCode.Create(doc.SheetCode), Name($"Sheet {tag} year"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var table = new TableDef(
            sheet.Id, EcrCode.Create(tableCode), Name($"Table {tag} year"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var targetC1 = new ColumnDef(table.Id, EcrCode.Create($"C1_{tag}"), Name("C1"), 1, CellDataType.String);
        var targetC2 = new ColumnDef(table.Id, EcrCode.Create($"C2_{tag}"), Name("C2"), 2, CellDataType.Decimal);
        db.ColumnDefs.AddRange(targetC1, targetC2);
        var targetM = new Dictionary<int, ColumnDef>();
        for (var m = 1; m <= 12; m++)
        {
            var column = new ColumnDef(table.Id, EcrCode.Create($"M{m}_{tag}"), Name($"Month {m}"), 2 + m, CellDataType.Decimal);
            column.SetMonth(true, (byte)m);
            targetM[m] = column;
            db.ColumnDefs.Add(column);
        }

        var targetF = new Dictionary<int, ColumnDef>();
        for (var m = 1; m <= targetMonths; m++)
        {
            var column = new ColumnDef(
                table.Id, EcrCode.Create(string.Create(CultureInfo.InvariantCulture, $"{fCode}_M{m:00}")), Name($"F {m}"), 20 + m, CellDataType.Formula);
            column.SetMonth(true, (byte)m);
            targetF[m] = column;
            db.ColumnDefs.Add(column);
        }

        for (var i = 1; i <= 2; i++)
        {
            db.RowDefs.Add(new RowDef(table.Id, RowKey.Create($"R{i}_{tag}"), i, Name($"Row {i}"), RowKind.Item));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var column in targetF.Values)
        {
            // Місячна формула з дозволом ручного значення: ручне значення вересня переїжджає як ручне (D-461).
            var formula = new FormulaDef(table.Id, FormulaScope.Column, "0", ExpressionDialect.Template);
            formula.AssignColumn(column.Id);
            formula.SetAllowManualOverride(true);
            db.FormulaDefs.Add(formula);
        }

        // Селектор S зникає, його ім'я — псевдонім аргументу на місячний ввід (як етап YearMonths будівника Land).
        for (var m = 1; m <= 12; m++)
        {
            db.MonthInputAliases.Add(new MonthInputAlias(table.Id, sCode, (byte)m, targetM[m].Id));
        }

        version.Publish(1, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var userName = await AddYearUserAsync(
                builder, doc.ProjectId, [MigrateDocumentVersionHandler.Permission, "Document.View"], GrantLevel.Manage)
            .ConfigureAwait(false);
        var userId = await db.Users.AsNoTracking().Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync().ConfigureAwait(false);
        var row1Key = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);

        return new YearScenario(
            builder, doc, userName, userId, doc.TemplateVersionId, version.Id, table.Id,
            $"{doc.SheetCode}.{tableCode}", fCode, sCode, $"C2_{tag}",
            sourceF.Id, sourceS.Id, targetC2.Id, targetM[9].Id,
            targetF.ToDictionary(p => p.Key, p => p.Value.Id),
            targetF.ToDictionary(p => p.Key, p => p.Value.Code),
            instances.ToDictionary(p => p.Key, p => p.Value.Row1),
            row1Key);
    }

    private static Task<string> AddYearUserAsync(YearScenario y, string[] permissions, GrantLevel grant)
        => AddYearUserAsync(y.Builder, y.Doc.ProjectId, permissions, grant);

    private static async Task<string> AddYearUserAsync(TestDocumentBuilder builder, int projectId, string[] permissions, GrantLevel grant)
    {
        var userName = $"l3y_{Guid.NewGuid():N}"[..20];
        await using var db = builder.CreateContext();
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"L3Y{Guid.NewGuid():N}"[..14]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "L3 year migration" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, grant));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return userName;
    }
}
