// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewStableTests.cs
using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// RPT-STABLE (Part A): вʼюхи БЕЗ версії <c>rpt.v_&lt;Шаблон&gt;_&lt;Аркуш&gt;_&lt;Таблиця&gt;</c> по всіх опублікованих версіях
/// (<c>rpt.usp_GenerateStableViews</c>): колонки — обʼєднання за кодом і ніколи не зникають, <c>_TemplateVersion</c> — ПО РЯДКУ
/// з версії власного TableDef, вʼюхи <c>_vN</c> не зачеплені. Перевіряється РОЗГОРНУТА вʼюха на живому SQL Server.
/// </summary>
/// <remarks>
/// Мутації (докази): (1) у <c>05-rpt-views.sql</c> в <c>rpt.usp_GenerateStableViews</c> брати колонки лише останньої версії
/// (<c>WHERE x.GroupId = @g AND cd.IsDeleted = 0</c> + <c>AND x.TemplateVersionId = (SELECT MAX(...))</c>) — червоніє сценарій
/// («колонка не зникає» і «нова колонка у v2»); (2) <c>_TemplateVersion</c> брати з версії проєкту (стала) замість <c>CASE ti.TableDefId</c> —
/// червоніє ряд версій 1,1,2,2.
/// </remarks>
[Collection("SqlServer")]
public sealed partial class ReportViewStableTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

    private sealed record TableSpec(string Sheet, string Table, (string Code, CellDataType Type)[] Columns);

    private sealed record BuiltTable(int VersionId, int SheetId, int TableId, Dictionary<string, int> Cols);

    private sealed record Project(int ProjectId, long DocumentId);

    // ── сценарій людини 29.10 ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task Стабільна_вʼюха_веде_один_ряд_через_версії_обʼєднує_колонки_і_бере_версію_по_рядку()
    {
        var (templateId, tpl) = await NewTemplateAsync();
        string[] ab = ["A", "B"];

        // v1: A (текст), B (число). Проєкт P1: періоди 202609-202610 на TableDef v1, дані є.
        var v1 = (await AddVersionAsync(templateId, "1", [new("S", "T", [("A", CellDataType.String), ("B", CellDataType.Decimal)])]))["S/T"];
        var p1 = await NewProjectAsync(v1.VersionId, 202609, 202610, 202611, 202612);
        await SeedAsync(p1, 202609, v1, ("R1", 1, new() { ["A"] = "a9", ["B"] = 9m }));
        await SeedAsync(p1, 202610, v1, ("R1", 1, new() { ["A"] = "a10", ["B"] = 10m }));

        await GenerateAsync(v1.VersionId);
        var stable = StableName(tpl, "S", "T");
        var v1View = VersionedName(tpl, "S", "T", "1");

        // Крок 1: лише v1 - стабільна вʼюха = v1; службові колонки + _TemplateVersion + A, B.
        Assert.Equal(
            ["_ProjectId", "_ProjectCode", "_DocumentId", "_DocumentKey", "_PeriodKey", "_Status", "_RowId", "_RowKey", "_RowOrdinal",
             "_TemplateVersion", "A", "B"],
            await ColumnNamesAsync(stable));
        var v1Definition = await DefinitionAsync(v1View);

        // Крок 2: v2 (+колонка C) опублікована ДО переносу: старі рядки на місці, C = NULL, версія 1.
        var v2 = (await AddVersionAsync(templateId, "2",
            [new("S", "T", [("A", CellDataType.String), ("B", CellDataType.Decimal), ("C", CellDataType.Decimal)])]))["S/T"];
        await GenerateAsync(v2.VersionId);
        Assert.Equal(["A", "B", "C"], (await ColumnNamesAsync(stable)).Skip(10).ToList());
        Assert.Equal(
            [(202609, "1", "a9", 9m, (decimal?)null), (202610, "1", "a10", 10m, null)],
            (await RowsAsync(stable, p1.ProjectId, "_PeriodKey", "_TemplateVersion", "A", "B", "C"))
                .Select(r => ((int)r[0]!, (string)r[1]!, (string?)r[2], (decimal?)r[3], (decimal?)r[4])).ToList());

        // Крок 3: ОДИН проєкт - періоди 202611-202612 на TableDef v2 (засіяно напряму): безперервний ряд, версії 1,1,2,2,
        // C порожня лише в перших двох.
        await SeedAsync(p1, 202611, v2, ("R1", 1, new() { ["A"] = "a11", ["B"] = 11m, ["C"] = 111m }));
        await SeedAsync(p1, 202612, v2, ("R1", 1, new() { ["A"] = "a12", ["B"] = 12m, ["C"] = 112m }));
        var series = (await RowsAsync(stable, p1.ProjectId, "_PeriodKey", "_TemplateVersion", "C"))
            .Select(r => ((int)r[0]!, (string)r[1]!, (decimal?)r[2])).ToList();
        Assert.Equal([(202609, "1", (decimal?)null), (202610, "1", null), (202611, "2", 111m), (202612, "2", 112m)], series);

        // Мішані проєкти: P2 цілком на v2 - версія 2 по всіх його рядках, не залежить від Project.TemplateVersionId.
        var p2 = await NewProjectAsync(v2.VersionId, 202609);
        await SeedAsync(p2, 202609, v2, ("R1", 1, new() { ["A"] = "x", ["B"] = 1m, ["C"] = 2m }));
        Assert.Equal(["2"], (await RowsAsync(stable, p2.ProjectId, "_TemplateVersion")).Select(r => (string)r[0]!).ToList());

        // SSRS-подібні запити: групування за версією, фільтр за версією, діапазон періодів, SELECT *.
        Assert.Equal(
            [("1", 2, 19m), ("2", 3, 24m)],
            (await ScalarRowsAsync(
                $"SELECT _TemplateVersion, COUNT(*), SUM(B) FROM rpt.[{stable}] WHERE _ProjectId IN ({p1.ProjectId}, {p2.ProjectId}) "
                + "GROUP BY _TemplateVersion ORDER BY _TemplateVersion"))
                .Select(r => ((string)r[0]!, (int)r[1]!, (decimal)r[2]!)).ToList());
        Assert.Equal(
            1,
            (int)(await ScalarRowsAsync(
                $"SELECT COUNT(*) FROM rpt.[{stable}] WHERE _ProjectId = {p1.ProjectId} AND _TemplateVersion = N'2' AND _PeriodKey BETWEEN 202611 AND 202611"))[0][0]!);
        Assert.Equal(
            ab.Length + 11,
            (await ScalarRowsAsync($"SELECT TOP 1 * FROM rpt.[{stable}]"))[0].Length);

        // Крок 4: v3 ПРИБИРАЄ B і додає аркуш S2: стара колонка B лишається (NULL для нових рядків), для S2 зʼявляється своя вʼюха,
        // вʼюха T не втрачає нічого.
        var v3 = await AddVersionAsync(templateId, "3",
        [
            new("S", "T", [("A", CellDataType.String), ("C", CellDataType.Decimal)]),
            new("S2", "U", [("Q", CellDataType.Decimal)]),
        ]);
        var p3 = await NewProjectAsync(v3["S/T"].VersionId, 202609);
        await SeedAsync(p3, 202609, v3["S/T"], ("R1", 1, new() { ["A"] = "z", ["C"] = 5m }));
        await GenerateAsync(v3["S/T"].VersionId);
        Assert.Equal(["A", "B", "C"], (await ColumnNamesAsync(stable)).Skip(10).ToList());   // B не зникла
        var row3 = (await RowsAsync(stable, p3.ProjectId, "_TemplateVersion", "A", "B", "C"))[0];
        Assert.Equal(("3", "z", (decimal?)null, (decimal?)5m), ((string)row3[0]!, (string)row3[1]!, (decimal?)row3[2], (decimal?)row3[3]));
        Assert.Equal(["Q"], (await ColumnNamesAsync(StableName(tpl, "S2", "U"))).Skip(10).ToList());
        Assert.Equal(4, (await RowsAsync(stable, p1.ProjectId, "_PeriodKey")).Count);   // ряд P1 не змінився

        // Обовʼязково: вʼюхи _vN не зачеплені (текст байт-у-байт той самий, колонки ті самі, рядки ті самі).
        Assert.Equal(v1Definition, await DefinitionAsync(v1View));
        Assert.Equal(["A", "B"], (await ColumnNamesAsync(v1View)).Skip(9).ToList());
        Assert.Equal(2, (await RowsAsync(v1View, p1.ProjectId, "_PeriodKey")).Count);

        // Ідемпотентність: повторний запуск - усе Unchanged, нічого не перебудовано.
        var again = await ExecStableAsync(v3["S/T"].VersionId);
        Assert.NotEmpty(again);
        Assert.All(again, r => Assert.Equal("Unchanged", r.Action));
    }

    // ── конфлікт типу, перейменування, імена ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task Конфлікт_типу_колонки_між_версіями_дає_nvarchar_і_попередження()
    {
        var (templateId, tpl) = await NewTemplateAsync();
        var v1 = (await AddVersionAsync(templateId, "1", [new("S", "T", [("X", CellDataType.String), ("Y", CellDataType.Decimal)])]))["S/T"];
        var v2 = (await AddVersionAsync(templateId, "2", [new("S", "T", [("X", CellDataType.Decimal), ("Y", CellDataType.Int)])]))["S/T"];
        var p = await NewProjectAsync(v1.VersionId, 202609, 202610);
        await SeedAsync(p, 202609, v1, ("R1", 1, new() { ["X"] = "abc", ["Y"] = 1.5m }));
        await SeedAsync(p, 202610, v2, ("R1", 1, new() { ["X"] = 12.5m, ["Y"] = 2m }));

        var result = await ExecStableAsync(v2.VersionId);
        var stable = StableName(tpl, "S", "T");

        var types = await ColumnTypesAsync(stable);
        Assert.Equal("nvarchar", types["X"]);     // string vs number - конфлікт
        Assert.Equal("decimal", types["Y"]);      // Decimal vs Int - одна родина (число), не конфлікт
        var rows = (await RowsAsync(stable, p.ProjectId, "_PeriodKey", "X", "Y")).Select(r => ((int)r[0]!, (string?)r[1], (decimal?)r[2])).ToList();
        Assert.Equal("abc", rows[0].Item2);
        Assert.StartsWith("12.5", rows[1].Item2, StringComparison.Ordinal);
        Assert.Equal(2m, rows[1].Item3);

        var warning = Assert.Single(result, r => r.View == stable).Warning;
        Assert.NotNull(warning);
        Assert.Contains("X", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Y", warning!, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task Перейменована_колонка_без_мапи_дає_дві_колонки_а_дані_кожної_версії_у_своїй()
    {
        var (templateId, tpl) = await NewTemplateAsync();
        var v1 = (await AddVersionAsync(templateId, "1", [new("S", "T", [("Old", CellDataType.Decimal)])]))["S/T"];
        var v2 = (await AddVersionAsync(templateId, "2", [new("S", "T", [("New", CellDataType.Decimal)])]))["S/T"];
        var p = await NewProjectAsync(v1.VersionId, 202609, 202610);
        await SeedAsync(p, 202609, v1, ("R1", 1, new() { ["Old"] = 1m }));
        await SeedAsync(p, 202610, v2, ("R1", 1, new() { ["New"] = 2m }));
        await ExecStableAsync(v2.VersionId);

        var stable = StableName(tpl, "S", "T");
        Assert.Equal(["Old", "New"], (await ColumnNamesAsync(stable)).Skip(10).ToList());
        Assert.Equal(
            [((decimal?)1m, (decimal?)null), (null, 2m)],
            (await RowsAsync(stable, p.ProjectId, "Old", "New")).Select(r => ((decimal?)r[0], (decimal?)r[1])).ToList());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task Стабільне_імʼя_що_збігається_з_імʼям_vN_вʼюхи_відмовляє_а_не_переписує_її()
    {
        // Таблиця `T_v1` дає стабільне імʼя v_<Tpl>_S_T_v1 - те саме, що _vN вʼюха таблиці `T` версії `1`.
        var (templateId, tpl) = await NewTemplateAsync();
        var built = await AddVersionAsync(templateId, "1",
        [
            new("S", "T", [("A", CellDataType.Decimal)]),
            new("S", "T_v1", [("A", CellDataType.Decimal)]),
        ]);
        var version = built["S/T"].VersionId;
        await GenerateViaProcedureAsync(version);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecStableAsync(version));
        Assert.Equal(50409, error.Number);
        Assert.Contains($"v_{tpl}_S_T_v1", error.Message, StringComparison.Ordinal);

        // Колізійне стабільне імʼя НЕ перезаписало _vN вʼюху (вона лишилась зі своєю позначкою генератора _vN)...
        Assert.Contains("usp_GenerateTemplateViews", await DefinitionAsync($"v_{tpl}_S_T_v1"), StringComparison.Ordinal);
        // ...а некліднена таблиця стабільну вʼюху отримала.
        Assert.NotEmpty(await ColumnNamesAsync(StableName(tpl, "S", "T")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task Широка_таблиця_понад_250_колонок_має_стабільну_вʼюху_а_понад_1000_ні()
    {
        var (templateId, tpl) = await NewTemplateAsync();
        var cols = Enumerable.Range(1, 300).Select(i => ($"C{i}", CellDataType.Decimal)).ToArray();
        var built = (await AddVersionAsync(templateId, "1", [new("S", "T", cols)]))["S/T"];
        var p = await NewProjectAsync(built.VersionId, 202609);
        await SeedAsync(p, 202609, built, ("R1", 1, new() { ["C1"] = 1m, ["C300"] = 300m }));

        await ExecStableAsync(built.VersionId);
        var stable = StableName(tpl, "S", "T");
        Assert.Equal(300, (await ColumnNamesAsync(stable)).Count - 10);
        var row = (await RowsAsync(stable, p.ProjectId, "C1", "C2", "C300"))[0];
        Assert.Equal((1m, (decimal?)null, 300m), ((decimal)row[0]!, (decimal?)row[1], (decimal)row[2]!));

        // _vN для 300 колонок НЕ генерується (ліміт 250 таблиць у запиті) - стабільна читає ту саму таблицю одним проходом.
        await Assert.ThrowsAsync<SqlException>(() => GenerateViaProcedureAsync(built.VersionId));

        var (templateId2, tpl2) = await NewTemplateAsync();
        var huge = Enumerable.Range(1, 1001).Select(i => ($"H{i}", CellDataType.Decimal)).ToArray();
        var hugeBuilt = (await AddVersionAsync(templateId2, "1", [new("S", "T", huge)]))["S/T"];
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecStableAsync(hugeBuilt.VersionId));
        Assert.Equal(50422, error.Number);
        Assert.Contains("1001", error.Message, StringComparison.Ordinal);
        Assert.Empty(await ColumnNamesAsync(StableName(tpl2, "S", "T")));
    }

    // ── паритет зі старими вʼюхами ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task Стабільна_вʼюха_віддає_ті_самі_значення_що_вʼюха_vN_і_з_маршрутизацією_місяців(bool routing)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(202610, columnCount: 3, rowCount: 2, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
        await using (var db = sql.CreateContext())
        {
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
            version.Publish(1, Now);
            await db.SaveChangesAsync();
        }

        var other = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202611));
        string[] codes;
        string tpl, table;
        await using (var db = sql.CreateContext())
        {
            codes = await db.ColumnDefs.Where(c => c.TableDefId == doc.TableDefId).OrderBy(c => c.Id).Select(c => c.Code).ToArrayAsync();
            tpl = await db.TemplateVersions.Where(v => v.Id == doc.TemplateVersionId).Join(db.Templates, v => v.TemplateId, t => t.Id, (v, t) => t.Code).SingleAsync();
            table = await db.TableDefs.Where(t => t.Id == doc.TableDefId).Select(t => t.Code).SingleAsync();
        }

        // значення: власні копії в 202610, значення місяця M11 в екземплярі 202611 (власник), текст і число.
        await InsertCellsAsync(doc.TableDefId, 202610, doc.RowIds[0], (doc.ColumnDefIds[0], "txt"), (doc.ColumnDefIds[1], 4.5m), (doc.ColumnDefIds[2], 6m));
        await InsertCellsAsync(doc.TableDefId, 202610, doc.RowIds[1], (doc.ColumnDefIds[1], 7m));
        await InsertCellsAsync(doc.TableDefId, 202611, other.RowIds[0], (doc.MonthColumnDefIds[10], 11m), (doc.ColumnDefIds[1], 99m));

        await GenerateViaProcedureAsync(doc.TemplateVersionId, routing);
        await ExecStableAsync(doc.TemplateVersionId, routing);

        var oldView = VersionedName(tpl, doc.SheetCode, table, $"1.0.0.{doc.SheetCode["SHEET".Length..]}");
        var stable = StableName(tpl, doc.SheetCode, table);
        var select = string.Join(", ", codes.Select(c => $"[{c}]"));
        var oldRows = await ScalarRowsAsync($"SELECT _DocumentId, _PeriodKey, _RowKey, _Status, {select} FROM rpt.[{oldView}] ORDER BY _PeriodKey, _RowKey");
        var newRows = await ScalarRowsAsync($"SELECT _DocumentId, _PeriodKey, _RowKey, _Status, {select} FROM rpt.[{stable}] ORDER BY _PeriodKey, _RowKey");
        Assert.NotEmpty(oldRows);
        Assert.Equal(oldRows.Count, newRows.Count);
        for (var i = 0; i < oldRows.Count; i++)
        {
            Assert.Equal(oldRows[i], newRows[i]);
        }

        if (routing)
        {
            // доказ, що маршрутизація справді діє в стабільній: M11 рядка 202610 береться з екземпляра власника 202611.
            var m11 = (await ScalarRowsAsync($"SELECT [{codes[3 + 10]}] FROM rpt.[{stable}] WHERE _PeriodKey = 202610 AND _RowOrdinal = 1"))[0][0];
            Assert.Equal(11m, m11);
        }
    }

    // ── продуктивність (синтетика, чесно про масштаб) ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Синтетика: 1 версія, 40 колонок, N рядків × 40 щільних комірок. Порівнюються логічні читання й час: стабільна
    /// вʼюха проти <c>_v1</c> (план DoD: не повільніше +20 %). Числа - у виводі тесту; це НЕ масштаб Land.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task Стабільна_вʼюха_на_синтетичній_таблиці_не_читає_більше_ніж_vN_плюс_20_відсотків()
    {
        const int rows = 6000;
        const int columns = 40;
        var (templateId, tpl) = await NewTemplateAsync();
        var cols = Enumerable.Range(1, columns).Select(i => ($"C{i}", CellDataType.Decimal)).ToArray();
        var built = (await AddVersionAsync(templateId, "1", [new("S", "T", cols)]))["S/T"];
        var p = await NewProjectAsync(built.VersionId, 202609);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var firstRow = await loader.ReserveIdsAsync("doc.TableRowSeq", rows, CancellationToken.None);
        await using (var db = sql.CreateContext())
        {
            var key = new PeriodKey(202609);
            db.TableInstances.Add(new TableInstance(key, instanceId, p.DocumentId, built.TableId, Now));
            for (var i = 0; i < rows; i++)
            {
                db.TableRows.Add(new TableRow(key, firstRow + i, instanceId, RowKey.Create($"R{i + 1}"), i + 1, Now));
            }

            await db.SaveChangesAsync();
        }

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 300;
            command.CommandText = """
                INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
                SELECT r.PeriodKey, r.Id, c.Id, @t, CAST(r.Ordinal AS decimal(18,4)) + c.Ordinal
                  FROM doc.TableRow AS r CROSS JOIN cfg.ColumnDef AS c
                 WHERE r.PeriodKey = 202609 AND r.TableInstanceId = @i AND c.TableDefId = @t;
                """;
            command.Parameters.AddWithValue("@t", built.TableId);
            command.Parameters.AddWithValue("@i", instanceId);
            await command.ExecuteNonQueryAsync();
        }

        await ExecStableAsync(built.VersionId);
        await GenerateViaProcedureAsync(built.VersionId);
        var stable = StableName(tpl, "S", "T");
        var old = VersionedName(tpl, "S", "T", "1");
        var sum = string.Join(" + ", Enumerable.Range(1, columns).Select(i => $"ISNULL(C{i}, 0)"));

        // Прогрів + 3 заміри, медіана.
        var oldStats = await MeasureAsync($"SELECT SUM({sum}) FROM rpt.[{old}] WHERE _ProjectId = {p.ProjectId}");
        var newStats = await MeasureAsync($"SELECT SUM({sum}) FROM rpt.[{stable}] WHERE _ProjectId = {p.ProjectId}");
        Assert.Equal(oldStats.Value, newStats.Value);
        Console.WriteLine($"PERF rows={rows} cols={columns} cells={rows * columns}: _v1 reads={oldStats.Reads} ms={oldStats.Ms}; stable reads={newStats.Reads} ms={newStats.Ms}");
        Assert.True(newStats.Reads <= oldStats.Reads * 1.2, $"stable reads {newStats.Reads} > 1.2 x _v1 reads {oldStats.Reads}");
    }

    // ── помічники ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<(int TemplateId, string Code)> NewTemplateAsync()
    {
        await using var db = sql.CreateContext();
        var code = "STB" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var t = new Template(EcrCode.Create(code), Name(code), 1, Now);
        db.Templates.Add(t);
        await db.SaveChangesAsync();
        return (t.Id, code);
    }

    /// <summary>Нова версія шаблону з аркушами/таблицями/колонками; публікується одразу.</summary>
    private async Task<Dictionary<string, BuiltTable>> AddVersionAsync(int templateId, string versionText, TableSpec[] tables)
    {
        await using var db = sql.CreateContext();
        var version = new TemplateVersion(templateId, versionText, 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheets = new Dictionary<string, SheetDef>();
        var result = new Dictionary<string, BuiltTable>();
        var ordinal = 0;
        foreach (var spec in tables)
        {
            if (!sheets.TryGetValue(spec.Sheet, out var sheet))
            {
                sheet = new SheetDef(version.Id, EcrCode.Create(spec.Sheet), Name(spec.Sheet), sheets.Count + 1);
                db.SheetDefs.Add(sheet);
                await db.SaveChangesAsync();
                sheets[spec.Sheet] = sheet;
            }

            var table = new TableDef(sheet.Id, EcrCode.Create(spec.Table), Name(spec.Table), ++ordinal,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            db.TableDefs.Add(table);
            await db.SaveChangesAsync();

            var defs = spec.Columns.Select((c, i) => new ColumnDef(table.Id, EcrCode.Create(c.Code), Name(c.Code), i + 1, c.Type)).ToList();
            db.ColumnDefs.AddRange(defs);
            await db.SaveChangesAsync();
            result[$"{spec.Sheet}/{spec.Table}"] = new BuiltTable(version.Id, sheet.Id, table.Id, defs.ToDictionary(c => c.Code, c => c.Id, StringComparer.Ordinal));
        }

        version.Publish(1, Now);
        await db.SaveChangesAsync();
        return result;
    }

    private async Task<Project> NewProjectAsync(int versionId, params int[] periods)
    {
        await using var db = sql.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var project = new Ecr.Domain.Entities.Documents.Project(
            EcrCode.Create("PRJ" + tag), Name(tag), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            versionId, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        foreach (var period in periods)
        {
            var key = new PeriodKey(period);
            db.Periods.Add(new Period(project.Id, key, (byte)key.Sequence, new DateOnly(key.Year, key.Sequence, 1),
                new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence))));
        }

        var document = new Document(project.Id, "DOC" + tag, 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return new Project(project.Id, document.Id);
    }

    /// <summary>Екземпляр таблиці за період з рядками й значеннями за кодами колонок.</summary>
    private async Task SeedAsync(Project project, int periodKey, BuiltTable table,
        params (string RowKey, int Ordinal, Dictionary<string, object> Values)[] rows)
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var firstRow = await loader.ReserveIdsAsync("doc.TableRowSeq", rows.Length, CancellationToken.None);
        var key = new PeriodKey(periodKey);
        await using (var db = sql.CreateContext())
        {
            db.TableInstances.Add(new TableInstance(key, instanceId, project.DocumentId, table.TableId, Now));
            for (var i = 0; i < rows.Length; i++)
            {
                db.TableRows.Add(new TableRow(key, firstRow + i, instanceId, RowKey.Create(rows[i].RowKey), rows[i].Ordinal, Now));
            }

            await db.SaveChangesAsync();
        }

        for (var i = 0; i < rows.Length; i++)
        {
            await InsertCellsAsync(table.TableId, periodKey, firstRow + i,
                [.. rows[i].Values.Select(v => (table.Cols[v.Key], v.Value))]);
        }
    }

    private async Task InsertCellsAsync(int tableId, int periodKey, long rowId, params (int ColumnId, object Value)[] cells)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        foreach (var (columnId, value) in cells)
        {
            var target = value is string ? "ValueString" : "ValueNumeric";
            await using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, {target}) "
                                  + $"VALUES ({periodKey}, {rowId}, {columnId}, {tableId}, @v)";
            command.Parameters.AddWithValue("@v", value);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task GenerateAsync(int versionId)
    {
        await using var db = sql.CreateContext();
        await new ReportViewGenerator(db).GenerateAsync(versionId, CancellationToken.None);
    }

    private async Task GenerateViaProcedureAsync(int versionId, bool routing = false)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC rpt.usp_GenerateTemplateViews @v, @m";
        command.Parameters.AddWithValue("@v", versionId);
        command.Parameters.AddWithValue("@m", routing);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<(string View, string Action, string? Warning)>> ExecStableAsync(int versionId, bool routing = false)
    {
        var result = new List<(string, string, string?)>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC rpt.usp_GenerateStableViews @v, @m";
        command.CommandTimeout = 300;
        command.Parameters.AddWithValue("@v", versionId);
        command.Parameters.AddWithValue("@m", routing);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return result;
    }

    private async Task<List<object?[]>> RowsAsync(string view, int projectId, params string[] columns)
        => await ScalarRowsAsync(
            $"SELECT {string.Join(", ", columns.Select(c => $"[{c}]"))} FROM rpt.[{view}] WHERE _ProjectId = {projectId} ORDER BY _PeriodKey, _RowKey");

    private async Task<List<object?[]>> ScalarRowsAsync(string text)
    {
        var list = new List<object?[]>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object?[reader.FieldCount];
            reader.GetValues(values!);
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = values[i] is DBNull ? null : values[i];
            }

            list.Add(values);
        }

        return list;
    }

    private async Task<List<string>> ColumnNamesAsync(string view)
        => (await ScalarRowsAsync(
            $"SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'rpt.{view}', N'V') ORDER BY c.column_id"))
            .Select(r => (string)r[0]!).ToList();

    private async Task<Dictionary<string, string>> ColumnTypesAsync(string view)
        => (await ScalarRowsAsync(
            $"SELECT c.name, TYPE_NAME(c.user_type_id) FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'rpt.{view}', N'V')"))
            .ToDictionary(r => (string)r[0]!, r => (string)r[1]!, StringComparer.Ordinal);

    private async Task<string> DefinitionAsync(string view)
        => (string)(await ScalarRowsAsync($"SELECT OBJECT_DEFINITION(OBJECT_ID(N'rpt.{view}', N'V'))"))[0][0]!;

    private async Task<(object? Value, long Reads, long Ms)> MeasureAsync(string text)
    {
        var best = (Value: (object?)null, Reads: long.MaxValue, Ms: long.MaxValue);
        for (var attempt = 0; attempt < 4; attempt++)   // первая - прогрів
        {
            long reads = 0;
            await using var connection = new SqlConnection(sql.ConnectionString);
            connection.InfoMessage += (_, e) =>
            {
                foreach (var m in LogicalReads().Matches(e.Message).Cast<Match>())
                {
                    reads += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            };
            await connection.OpenAsync();
            await using (var set = connection.CreateCommand())
            {
                set.CommandText = "SET STATISTICS IO ON";
                await set.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.CommandText = text;
            command.CommandTimeout = 300;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var value = await command.ExecuteScalarAsync();
            clock.Stop();
            if (attempt > 0 && reads <= best.Reads)
            {
                best = (value, reads, Math.Min(best.Ms, clock.ElapsedMilliseconds));
            }
        }

        return best;
    }

    private static string StableName(string tpl, string sheet, string table) => Sanitise($"v_{tpl}_{sheet}_{table}");

    private static string VersionedName(string tpl, string sheet, string table, string version) => Sanitise($"v_{tpl}_{sheet}_{table}_v{version}");

    private static string Sanitise(string name) => NotIdentifier().Replace(name, "_");

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotIdentifier();

    [GeneratedRegex(@"logical reads (\d+)")]
    private static partial Regex LogicalReads();
}
