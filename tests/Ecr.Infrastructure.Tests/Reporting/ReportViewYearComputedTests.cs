// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewYearComputedTests.cs
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// ✎ LAND-YEAR L5: вʼюхи звітності (<c>rpt.v_*_vN</c> і стабільна) для версії з розкладкою YearWithMonthColumns
/// (<c>cfg.TemplateVersion.Layout = 1</c>) віддають ОБЧИСЛЮВАНІ колонки так само, як API (<c>MonthRoutedCellStore</c>,
/// <c>CalculatedCellOverlay</c> L2b): місячна <c>C_Mm</c> — з екземпляра періоду <c>m</c>, не-місячна — з якоря року, власна
/// копія рядка перегляду затінена (застаріле значення «по періодах» не видно), <c>_Text</c> — з того самого джерела.
/// </summary>
/// <remarks>
/// Дані: місячний проєкт 2099 (209901..209912), документ, рядки R1 (число) і R2 (рядковий результат). Формула C_M09 записана
/// в 209909 (R1 = 9, R2 = «TXT9»), у 209903 лежить застаріла власна копія (R1 = 333, R2 = «STALE»). Формула C_M03 — у 209903
/// (3), застаріла копія в 209909 (999). Не-місячна формула CY — у якорі 209901 (1), застаріла копія в 209903 (33).
/// Calculated K_M09: актуальний прогін 209909 (R1 = 90) і 209903 (R1 = 30), матеріалізоване 209909 R2 = 77, копія 209903 R2 = 55.
/// Не-місячний (квартальний) проєкт і версія з Layout = 0 під прапором — власна копія, як до L5.
/// Мутації (див. звіт lane): умова @routed без @rc; Calculated з прогону періоду рядка; без маршрутизації _Text.
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait("Requirement", "ФВ-10.2")]
public sealed class ReportViewYearComputedTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerFixture _sql = SqlServerFixture.WithOwnDatabase("_l5year");
    private readonly List<long> _documents = [];
    private readonly List<int> _versions = [];

    private sealed record Version(int VersionId, int TableId, Dictionary<string, int> Cols);

    private sealed record Row(int Period, string RowKey, decimal? CM03, decimal? CM09, string? CM09Text, decimal? CY, decimal? KM09);

    public Task InitializeAsync() => _sql.InitializeAsync();

    public Task DisposeAsync() => _sql.DisposeAsync();

    [Fact]
    public async Task Рік_формула_місяця_з_екземпляра_власника_застаріла_копія_затінена_Text_теж()
    {
        try
        {
            var (templateId, tpl) = await NewTemplateAsync();
            var v = await AddVersionAsync(templateId);
            await SetLayoutAsync(v.VersionId, 1);
            var doc = await NewDocumentAsync(v.VersionId, PeriodKind.Monthly);
            await SeedYearAsync(doc, v);

            await GenerateAsync(v.VersionId, routing: false);

            foreach (var view in new[] { Sanitize($"v_{tpl}_S_T_v1"), Sanitize($"v_{tpl}_S_T") })
            {
                var rows = await RowsAsync(view, doc);
                var r1 = rows.Single(r => r is { Period: 209903, RowKey: "R1" });
                Assert.Equal(9m, r1.CM09);            // власник - 209909, а не застаріла копія 333
                Assert.Equal(3m, r1.CM03);            // власник - сам 209903
                Assert.Equal(1m, r1.CY);              // не-місячна - якір 209901, а не 33
                var r2 = rows.Single(r => r is { Period: 209903, RowKey: "R2" });
                Assert.Null(r2.CM09);
                Assert.Equal("TXT9", r2.CM09Text);    // _Text - з власника, а не «STALE»
                var r1Sep = rows.Single(r => r is { Period: 209909, RowKey: "R1" });
                Assert.Equal(3m, r1Sep.CM03);         // копія 999 у 209909 затінена
                Assert.Equal(9m, r1Sep.CM09);
                // Місяць без екземпляра-власника комірки: значення немає (копію не підставляємо).
                var r1Jan = rows.Single(r => r is { Period: 209901, RowKey: "R1" });
                Assert.Equal(9m, r1Jan.CM09);
                Assert.Equal(1m, r1Jan.CY);
            }
        }
        finally
        {
            await CleanupAsync();
        }
    }

    /// <summary>
    /// L5b: Calculated K_M09 - число АКТУАЛЬНОГО прогону періоду-власника 209909 (90) у кожному рядку року, а не прогону періоду
    /// перегляду (209903: 30) і не застарілого прогону (900); без числа - матеріалізоване значення власника (77), не копія (55).
    /// </summary>
    [Fact]
    public async Task Рік_Calculated_місяця_з_прогону_періоду_власника()
    {
        try
        {
            var (templateId, tpl) = await NewTemplateAsync();
            var v = await AddVersionAsync(templateId);
            await SetLayoutAsync(v.VersionId, 1);
            var doc = await NewDocumentAsync(v.VersionId, PeriodKind.Monthly);
            var rows = await SeedYearAsync(doc, v);
            await SeedCalcAsync(doc, v, rows);

            await GenerateAsync(v.VersionId, routing: false);

            foreach (var view in new[] { Sanitize($"v_{tpl}_S_T_v1"), Sanitize($"v_{tpl}_S_T") })
            {
                var all = await RowsAsync(view, doc);
                foreach (var period in new[] { 209901, 209903, 209909 })
                {
                    Assert.Equal(90m, all.Single(r => r.Period == period && r.RowKey == "R1").KM09);
                    Assert.Equal(77m, all.Single(r => r.Period == period && r.RowKey == "R2").KM09);
                }
            }
        }
        finally
        {
            await CleanupAsync();
        }
    }

    /// <summary>
    /// L5b (F13 AU): власник маршрутизованих обчислюваних - лише ОРИГІНАЛ аркуша (API: MonthRoutedCellStore OriginalOnly). Копія
    /// аркуша (CopyOrdinal 2) має свої екземпляри 209903/209909 з іншими значеннями того самого RowKey/місяця: C_M09 R1 = 99,
    /// R2 = «ZZZ», матеріалізоване K_M09 R2 = 88, результат актуального прогону 209909 для екземпляра копії R1 = 95. Рядки
    /// оригіналу показують значення власника-оригіналу (9 / «TXT9» / 90 / 77), а не копії; рядки копії не маршрутизуються -
    /// власна копія й прогін свого періоду (209903: 444 / 35; 209909: 99 / 95 / 88).
    /// </summary>
    /// <remarks>
    /// Мутації (локально, не комітиться): прибрати <c>AND oi.SheetCopyId IS NULL</c> у fam або в orv (обидві процедури) -
    /// MAX змішує копію (99 / «ZZZ» / 88), а orv дає розбіжні 90 і 95 одного джерела - NULL; прибрати <c>ti.SheetCopyId IS NULL</c>
    /// з @owner - рядки копії читають власника-оригінал (9 замість 444).
    /// </remarks>
    [Fact]
    [Trait("L5b", "SheetCopy")]
    public async Task Рік_копія_аркуша_не_стає_власником_і_не_маршрутизується()
    {
        try
        {
            var (templateId, tpl) = await NewTemplateAsync();
            var v = await AddVersionAsync(templateId);
            await SetLayoutAsync(v.VersionId, 1);
            var doc = await NewDocumentAsync(v.VersionId, PeriodKind.Monthly);
            var rows = await SeedYearAsync(doc, v);
            var calc = await SeedCalcAsync(doc, v, rows);

            long copyId;
            await using (var db = _sql.CreateContext())
            {
                var sheetDefId = await db.TableDefs.Where(t => t.Id == v.TableId).Select(t => t.SheetDefId).FirstAsync();
                var original = new DocumentSheet(doc, sheetDefId);
                db.DocumentSheets.Add(original);
                await db.SaveChangesAsync();
                var copy = original.CreateCopy(2);
                db.DocumentSheets.Add(copy);
                await db.SaveChangesAsync();
                copyId = copy.Id;
            }

            var copy903 = await InstanceAsync(doc, 209903, v.TableId, copyId);
            var copy909 = await InstanceAsync(doc, 209909, v.TableId, copyId);
            var c9 = v.Cols["C_M09"];
            var k9 = v.Cols["K_M09"];
            await ExecuteAsync($"""
                INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, ValueString, IsCalculated)
                VALUES {Cell(209909, copy909.R1, c9, v.TableId, 99m, null)},
                       {Cell(209909, copy909.R2, c9, v.TableId, null, "ZZZ")},
                       {Cell(209909, copy909.R2, k9, v.TableId, 88m, null)},
                       {Cell(209903, copy903.R1, c9, v.TableId, 444m, null)};
                INSERT INTO calc.CalculationResult
                    (PeriodKey, Id, CalculationRunId, MethodologyVersionId, DocumentId, TableInstanceId, SourceRowKey, OutputCode, Value, UnitId, Kind)
                VALUES (209909, NEXT VALUE FOR calc.CalculationResultSeq, {calc.Run909}, {calc.MethodologyVersionId}, {doc}, {copy909.Instance}, N'R1', N'OUT9', 95, {calc.UnitId}, 0),
                       (209903, NEXT VALUE FOR calc.CalculationResultSeq, {calc.Run903}, {calc.MethodologyVersionId}, {doc}, {copy903.Instance}, N'R1', N'OUT9', 35, {calc.UnitId}, 0);
                """);

            await GenerateAsync(v.VersionId, routing: false);

            foreach (var view in new[] { Sanitize($"v_{tpl}_S_T_v1"), Sanitize($"v_{tpl}_S_T") })
            {
                // Оригінал: власник місяця 9 - екземпляр-ОРИГІНАЛ 209909, копія не змішується (MAX / розбіжність прогону).
                var originals = await RowsAsync(view, doc, copyOrdinal: 1);
                foreach (var period in new[] { 209901, 209903, 209909 })
                {
                    var r1 = originals.Single(r => r.Period == period && r.RowKey == "R1");
                    Assert.Equal(9m, r1.CM09);
                    Assert.Equal(90m, r1.KM09);
                    var r2 = originals.Single(r => r.Period == period && r.RowKey == "R2");
                    Assert.Equal("TXT9", r2.CM09Text);
                    Assert.Equal(77m, r2.KM09);
                }

                // Копія: маршрутизації немає - власна копія комірки і прогін свого періоду для свого екземпляра.
                var copies = await RowsAsync(view, doc, copyOrdinal: 2);
                Assert.Equal(4, copies.Count);
                var c903 = copies.Single(r => r is { Period: 209903, RowKey: "R1" });
                Assert.Equal(444m, c903.CM09);
                Assert.Equal(35m, c903.KM09);
                var c909 = copies.Single(r => r is { Period: 209909, RowKey: "R1" });
                Assert.Equal(99m, c909.CM09);
                Assert.Equal(95m, c909.KM09);
                var c909r2 = copies.Single(r => r is { Period: 209909, RowKey: "R2" });
                Assert.Equal("ZZZ", c909r2.CM09Text);
                Assert.Equal(88m, c909r2.KM09);
            }
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task Не_місячний_проєкт_і_прапор_без_розкладки_рік_власна_копія_як_раніше()
    {
        try
        {
            var (templateId, tpl) = await NewTemplateAsync();
            var v = await AddVersionAsync(templateId);
            await SetLayoutAsync(v.VersionId, 1);
            var quarterly = await NewDocumentAsync(v.VersionId, PeriodKind.Quarterly);
            await SeedCalcAsync(quarterly, v, await SeedYearAsync(quarterly, v));
            var monthly = await NewDocumentAsync(v.VersionId, PeriodKind.Monthly);
            await SeedCalcAsync(monthly, v, await SeedYearAsync(monthly, v));
            var versioned = Sanitize($"v_{tpl}_S_T_v1");

            // Layout = 1, квартальний проєкт: ключ 209903 - не місяць, маршрутизації немає (API: GetDocumentAsync = null).
            await GenerateAsync(v.VersionId, routing: false);
            var q = (await RowsAsync(versioned, quarterly)).Single(r => r is { Period: 209903, RowKey: "R1" });
            Assert.Equal(333m, q.CM09);
            Assert.Equal(33m, q.CY);
            Assert.Equal(30m, q.KM09);   // прогін свого періоду
            var q2 = (await RowsAsync(versioned, quarterly)).Single(r => r is { Period: 209903, RowKey: "R2" });
            Assert.Equal("STALE", q2.CM09Text);
            Assert.Equal(55m, q2.KM09);

            // Layout = 0, спадковий прапор ON: обчислювані НЕ маршрутизуються (лише розкладка версії, як у MonthColumnRouter).
            await SetLayoutAsync(v.VersionId, 0);
            await GenerateAsync(v.VersionId, routing: true);
            foreach (var view in new[] { versioned, Sanitize($"v_{tpl}_S_T") })
            {
                var rows = await RowsAsync(view, monthly);
                var m = rows.Single(r => r is { Period: 209903, RowKey: "R1" });
                Assert.Equal(333m, m.CM09);
                Assert.Equal(33m, m.CY);
                Assert.Equal(30m, m.KM09);
                var m2 = rows.Single(r => r is { Period: 209903, RowKey: "R2" });
                Assert.Equal("STALE", m2.CM09Text);
                Assert.Equal(55m, m2.KM09);
            }
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task Лише_обчислювані_маршрутизовані_вʼюха_має_текст_без_apply_sc()
    {
        try
        {
            var (templateId, tpl) = await NewTemplateAsync();
            var v = await AddVersionAsync(templateId, withInput: false);
            await SetLayoutAsync(v.VersionId, 1);
            var doc = await NewDocumentAsync(v.VersionId, PeriodKind.Monthly);
            await SeedYearAsync(doc, v);

            await GenerateAsync(v.VersionId, routing: false);

            foreach (var view in new[] { Sanitize($"v_{tpl}_S_T_v1"), Sanitize($"v_{tpl}_S_T") })
            {
                var definition = await DefinitionAsync(view);
                Assert.False(string.IsNullOrEmpty(definition));
                Assert.DoesNotContain(") AS sc", definition, StringComparison.Ordinal);
                var r1 = (await RowsAsync(view, doc)).Single(r => r is { Period: 209903, RowKey: "R1" });
                Assert.Equal(9m, r1.CM09);
            }
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ── помічники ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Sanitize(string name) => Regex.Replace(name, "[^A-Za-z0-9_]", "_");

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<(int TemplateId, string Code)> NewTemplateAsync()
    {
        await using var db = _sql.CreateContext();
        var code = "LFY" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var t = new Template(EcrCode.Create(code), Name(code), 1, Now);
        db.Templates.Add(t);
        await db.SaveChangesAsync();
        return (t.Id, code);
    }

    /// <summary>
    /// Опублікована версія: аркуш S, таблиця T (MonthsInColumns): A (число, введена не-місячна; лише з <paramref name="withInput"/>),
    /// формули C_M03, C_M09 (місячні) і CY (не-місячна).
    /// </summary>
    private async Task<Version> AddVersionAsync(int templateId, bool withInput = true)
    {
        await using var db = _sql.CreateContext();
        var version = new TemplateVersion(templateId, "1", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();
        _versions.Add(version.Id);
        var sheet = new SheetDef(version.Id, EcrCode.Create("S"), Name("S"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        var table = new TableDef(sheet.Id, EcrCode.Create("T"), Name("T"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();
        var columns = new List<ColumnDef>();
        if (withInput)
        {
            columns.Add(new ColumnDef(table.Id, EcrCode.Create("A"), Name("A"), 1, CellDataType.Decimal));
        }

        foreach (var (code, month, ordinal, type) in new[]
                 {
                     ("C_M03", (byte?)3, 2, CellDataType.Formula), ("C_M09", (byte?)9, 3, CellDataType.Formula),
                     ("CY", null, 4, CellDataType.Formula), ("K_M09", (byte?)9, 5, CellDataType.Calculated),
                 })
        {
            var column = new ColumnDef(table.Id, EcrCode.Create(code), Name(code), ordinal, type);
            if (month is { } m)
            {
                column.SetMonth(true, m);
            }

            columns.Add(column);
        }

        db.ColumnDefs.AddRange(columns);
        await db.SaveChangesAsync();
        version.Publish(1, Now);
        await db.SaveChangesAsync();
        return new Version(version.Id, table.Id, columns.ToDictionary(c => c.Code, c => c.Id, StringComparer.Ordinal));
    }

    /// <summary>Проєкт 2099 (місячний: 209901..209912; квартальний: 209901..209904) і документ.</summary>
    private async Task<long> NewDocumentAsync(int versionId, PeriodKind kind)
    {
        await using var db = _sql.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var project = new Project(
            EcrCode.Create("PRJ" + tag), Name(tag), new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31),
            versionId, kind, policyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var count = kind == PeriodKind.Monthly ? 12 : 4;
        for (var s = 1; s <= count; s++)
        {
            var key = new PeriodKey(209900 + s);
            var firstMonth = kind == PeriodKind.Monthly ? s : 1 + ((s - 1) * 3);
            var lastMonth = kind == PeriodKind.Monthly ? s : firstMonth + 2;
            db.Periods.Add(new Period(project.Id, key, (byte)s, new DateOnly(2099, firstMonth, 1),
                new DateOnly(2099, lastMonth, DateTime.DaysInMonth(2099, lastMonth))));
        }

        var document = new Document(project.Id, "DOC" + tag, 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        _documents.Add(document.Id);
        return document.Id;
    }

    /// <summary>
    /// Екземпляри 209901, 209903, 209909 (рядки R1, R2). Власники: CY - 209901 (R1 = 1); C_M03 - 209903 (R1 = 3);
    /// C_M09 - 209909 (R1 = 9, R2 = «TXT9»). Застарілі власні копії: 209903 C_M09 (333 / «STALE»), CY (33); 209909 C_M03 (999).
    /// </summary>
    private async Task<Dictionary<int, (long Instance, long R1, long R2)>> SeedYearAsync(long documentId, Version table)
    {
        var rows = new Dictionary<int, (long Instance, long R1, long R2)>();
        foreach (var period in new[] { 209901, 209903, 209909 })
        {
            rows[period] = await InstanceAsync(documentId, period, table.TableId);
        }

        var c3 = table.Cols["C_M03"];
        var c9 = table.Cols["C_M09"];
        var cy = table.Cols["CY"];
        var k9 = table.Cols["K_M09"];
        var cells = new List<string>
        {
            Cell(209901, rows[209901].R1, cy, table.TableId, 1m, null),
            Cell(209903, rows[209903].R1, c3, table.TableId, 3m, null),
            Cell(209909, rows[209909].R1, c9, table.TableId, 9m, null),
            Cell(209909, rows[209909].R2, c9, table.TableId, null, "TXT9"),
            Cell(209903, rows[209903].R1, c9, table.TableId, 333m, null),
            Cell(209903, rows[209903].R2, c9, table.TableId, null, "STALE"),
            Cell(209903, rows[209903].R1, cy, table.TableId, 33m, null),
            Cell(209909, rows[209909].R1, c3, table.TableId, 999m, null),
            // K_M09 (Calculated): матеріалізоване значення власника 209909 (R2 = 77) і застаріла копія 209903 (R2 = 55).
            Cell(209909, rows[209909].R2, k9, table.TableId, 77m, null),
            Cell(209903, rows[209903].R2, k9, table.TableId, 55m, null),
        };
        if (table.Cols.TryGetValue("A", out var a))
        {
            cells.Add($"(209901, {rows[209901].R1}, {a}, {table.TableId}, 7, NULL, 0)");
        }

        await ExecuteAsync($"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, ValueString, IsCalculated)
            VALUES {string.Join(",\n       ", cells)}
            """);
        return rows;
    }

    /// <summary>
    /// Методологія з виходом OUT9, прив'язка {} до K_M09; АКТУАЛЬНІ документні прогони 209909 (R1 = 90, екземпляр 209909) і 209903
    /// (R1 = 30, екземпляр 209903 - число «по періодах», яке вʼюха року показувати не має); застарілий прогін 209909 (R1 = 900).
    /// </summary>
    private async Task<CalcSeed> SeedCalcAsync(long documentId, Version table, Dictionary<int, (long Instance, long R1, long R2)> rows)
    {
        long run903, run909, old909;
        int versionId, unitId;
        await using (var db = _sql.CreateContext())
        {
            var projectId = await db.Documents.Where(d => d.Id == documentId).Select(d => d.ProjectId).FirstAsync();
            var methodology = new Ecr.Domain.Entities.Calculations.Methodology(
                EcrCode.Create("L5Y" + Guid.NewGuid().ToString("N")[..8]), Name("l5"));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            var version = new Ecr.Domain.Entities.Calculations.MethodologyVersion(
                methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();
            versionId = version.Id;
            db.CalculationBindings.Add(new CalculationBinding(table.TableId, table.Cols["K_M09"], methodology.Id, "OUT9", "{}"));
            await db.SaveChangesAsync();

            async Task<long> RunAsync(int period, bool current)
            {
                var run = new Ecr.Domain.Entities.Calculations.CalculationRun(projectId, period, triggeredByUserId: 1, Now, documentId);
                db.CalculationRuns.Add(run);
                await db.SaveChangesAsync();
                run.Complete("Completed", Now, profileJson: null, errorMessage: null);
                if (current)
                {
                    run.MakeCurrent();
                }

                await db.SaveChangesAsync();
                return run.Id;
            }

            old909 = await RunAsync(209909, current: false);
            run909 = await RunAsync(209909, current: true);
            run903 = await RunAsync(209903, current: true);
            unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        }

        await ExecuteAsync($"""
            INSERT INTO calc.CalculationResult
                (PeriodKey, Id, CalculationRunId, MethodologyVersionId, DocumentId, TableInstanceId, SourceRowKey, OutputCode, Value, UnitId, Kind)
            VALUES (209909, NEXT VALUE FOR calc.CalculationResultSeq, {run909}, {versionId}, {documentId}, {rows[209909].Instance}, N'R1', N'OUT9', 90, {unitId}, 0),
                   (209909, NEXT VALUE FOR calc.CalculationResultSeq, {old909}, {versionId}, {documentId}, {rows[209909].Instance}, N'R1', N'OUT9', 900, {unitId}, 0),
                   (209903, NEXT VALUE FOR calc.CalculationResultSeq, {run903}, {versionId}, {documentId}, {rows[209903].Instance}, N'R1', N'OUT9', 30, {unitId}, 0);
            """);
        return new CalcSeed(run903, run909, versionId, unitId);
    }

    /// <summary>Актуальні прогони 209903/209909, версія методології й одиниця з <see cref="SeedCalcAsync"/>.</summary>
    private sealed record CalcSeed(long Run903, long Run909, int MethodologyVersionId, int UnitId);

    private static string Cell(int period, long row, int column, int table, decimal? number, string? text)
        => $"({period}, {row}, {column}, {table}, {(number is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")}, "
           + $"{(text is null ? "NULL" : $"N'{text}'")}, 1)";

    private async Task<(long Instance, long R1, long R2)> InstanceAsync(long documentId, int periodKey, int tableId, long? sheetCopyId = null)
    {
        var loader = new BulkCellLoader(_sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 2, CancellationToken.None);
        var key = new PeriodKey(periodKey);
        await using var db = _sql.CreateContext();
        db.TableInstances.Add(new TableInstance(key, instanceId, documentId, tableId, Now, sheetCopyId));
        db.TableRows.Add(new TableRow(key, rowId, instanceId, RowKey.Create("R1"), 1, Now));
        db.TableRows.Add(new TableRow(key, rowId + 1, instanceId, RowKey.Create("R2"), 2, Now));
        await db.SaveChangesAsync();
        return (instanceId, rowId, rowId + 1);
    }

    private Task SetLayoutAsync(int versionId, int layout)
        => ExecuteAsync($"UPDATE cfg.TemplateVersion SET Layout = {layout} WHERE Id = {versionId}");

    /// <summary>Обидві процедури для версії (як ReportViewGenerator): _vN, потім стабільні.</summary>
    private async Task GenerateAsync(int versionId, bool routing)
    {
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        foreach (var proc in new[] { "rpt.usp_GenerateTemplateViews", "rpt.usp_GenerateStableViews" })
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 300;
            command.CommandText = $"EXEC {proc} @TemplateVersionId = @v, @MonthRouting = @m";
            command.Parameters.AddWithValue("@v", versionId);
            command.Parameters.AddWithValue("@m", routing);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // результат процедури не потрібен
            }
        }
    }

    private async Task<string> DefinitionAsync(string view)
        => (string?)(await QueryAsync($"SELECT OBJECT_DEFINITION(OBJECT_ID(N'rpt.[{view}]', N'V'))"))[0][0] ?? string.Empty;

    /// <summary>Рядки вʼюхи документа; <paramref name="copyOrdinal"/> - лише оригінал (1) або копія аркуша (2…), <c>null</c> - усі.</summary>
    private async Task<List<Row>> RowsAsync(string view, long documentId, short? copyOrdinal = null)
        => [.. (await QueryAsync($"SELECT _PeriodKey, _RowKey, [C_M03], [C_M09], [C_M09_Text], [CY], [K_M09] FROM rpt.[{view}] WHERE _DocumentId = {documentId}"
                                 + (copyOrdinal is { } o ? $" AND _SheetCopyOrdinal = {o}" : string.Empty) + " ORDER BY _PeriodKey, _RowKey"))
            .Select(r => new Row((int)r[0]!, (string)r[1]!, (decimal?)r[2], (decimal?)r[3], (string?)r[4], (decimal?)r[5], (decimal?)r[6]))];

    /// <summary>Свої дані (документи 2099, Layout версій) прибираються після кожного тесту.</summary>
    private async Task CleanupAsync()
    {
        if (_documents.Count > 0)
        {
            var docs = string.Join(",", _documents);
            await ExecuteAsync($"""
                DELETE FROM calc.CalculationResult WHERE DocumentId IN ({docs}) AND PeriodKey BETWEEN 209901 AND 209912;
                DELETE FROM calc.CalculationRun WHERE DocumentId IN ({docs});
                DELETE cv FROM doc.CellValue AS cv
                  JOIN doc.TableRow AS r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
                  JOIN doc.TableInstance AS ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
                 WHERE ti.DocumentId IN ({docs}) AND ti.PeriodKey BETWEEN 209901 AND 209912;
                DELETE r FROM doc.TableRow AS r
                  JOIN doc.TableInstance AS ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
                 WHERE ti.DocumentId IN ({docs}) AND ti.PeriodKey BETWEEN 209901 AND 209912;
                DELETE FROM doc.TableInstance WHERE DocumentId IN ({docs}) AND PeriodKey BETWEEN 209901 AND 209912;
                DELETE FROM doc.DocumentSheet WHERE DocumentId IN ({docs}) AND CopyOfId IS NOT NULL;
                DELETE FROM doc.DocumentSheet WHERE DocumentId IN ({docs});
                """);
            _documents.Clear();
        }

        if (_versions.Count > 0)
        {
            var versions = string.Join(",", _versions);
            await ExecuteAsync($"""
                DELETE b FROM cfg.CalculationBinding AS b
                  JOIN cfg.TableDef AS td ON td.Id = b.TableDefId
                  JOIN cfg.SheetDef AS sd ON sd.Id = td.SheetDefId
                 WHERE sd.TemplateVersionId IN ({versions});
                UPDATE cfg.TemplateVersion SET Layout = 0 WHERE Id IN ({versions});
                """);
            _versions.Clear();
        }
    }

    private async Task<List<object?[]>> QueryAsync(string text)
    {
        var list = new List<object?[]>();
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = text;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object?[reader.FieldCount];
            reader.GetValues(values!);
            list.Add([.. values.Select(v => v is DBNull ? null : v)]);
        }

        return list;
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
}
