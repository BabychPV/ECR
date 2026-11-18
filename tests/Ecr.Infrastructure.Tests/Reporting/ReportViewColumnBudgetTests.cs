// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewColumnBudgetTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// B6 L8 / B170 (Аудит-2 17.11; людина 18.11 ~00:35Z «У всіх річних», D-547): річна Land T9_07 (590 колонок, 448 обчислюваних)
/// із супутниками <c>&lt;Код&gt;_Text</c> давала 1051 колонку -> SQL Server 4505 на CREATE VIEW, і ця помилка ОБРИВАЛА генерацію
/// решти вʼюх версії (41 з 83); стабільна вʼюха так само падала й лишалась старою.
/// Тепер: (1) у версії з річною розкладкою (Layout = 1) МІСЯЧНА Formula/Calculated колонка ніколи не має супутника _Text - у
/// версійній і стабільній вʼюсі; немісячні супутники лишаються; місячна розкладка (Layout = 0) - без змін; (2) запобіжник: таблиця
/// понад бюджет (колонки + супутники + 24 службові > 1024; у стабільній - перевірка 1000 рахує й супутники) дає 50422 НАПРИКІНЦІ,
/// інші таблиці генеруються.
/// </summary>
/// <remarks>
/// Мутації (05-rpt-views.sql): версійна умова <c>NOT (@rc = 1 AND @isMonth = 1)</c> -> <c>(1 = 1)</c> - тест 1 червоний; стабільна
/// <c>NOT (@isMonth = 1 AND @lastRc = 1)</c> -> <c>(1 = 1)</c> - тест 3 червоний; версійна умова -> <c>(@isMonth = 0)</c> (без
/// розкладки) - тест 2 червоний; версійний бюджет <c>IF @limCols + @limText + 24 &gt; 1024</c> -> <c>IF 1 = 0</c> - тест 4 червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportViewColumnBudgetTests(SqlServerFixture sql) : IAsyncLifetime
{
    /// <summary>
    /// Широкі таблиці цього класу - сотні Calculated/Formula колонок без прив'язок і формул у ОПУБЛІКОВАНИХ версіях. Лишені в
    /// спільній базі колекції SqlServer, вони стають знахідками <c>UNBOUND_CALCULATED_COLUMN</c> / <c>UNSOURCED_FORMULA_COLUMN</c>
    /// задачі <c>ConsistencyCheckJob</c> і ламають чужий тест (batch-1118-1: <c>ConsistencyCheckJobWriteTests</c> - 3 MERGE
    /// пакети по 250 замість 1). Тому після кожного тесту колонки широкої таблиці мʼяко видаляються (правила їх пропускають).
    /// </summary>
    private readonly List<int> _wideTables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = sql.CreateContext();
        foreach (var tableId in _wideTables)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.ColumnDef SET IsDeleted = 1, DeletedAt = SYSUTCDATETIME() WHERE TableDefId = {tableId} AND IsDeleted = 0");
        }
    }

    private sealed record Built(TestDocument Doc, int TemplateId, string WideView, string NarrowView, string WideStable,
        string NarrowStable, string FormulaCode, string FirstMonthCode);

    /// <param name="monthCalculated">Скільки місячних Calculated колонок.</param>
    /// <param name="plainNonMonth">Скільки ще введених немісячних колонок (Decimal) - щоб таблиця не вмістилась і без супутників.</param>
    /// <param name="yearLayout">Версія з річною розкладкою (Layout = 1) чи місячною (0).</param>
    /// <param name="monthFormula">Скільки місячних Formula колонок (без Calculated таблиця йде гілкою без rv - <c>@select</c>).</param>
    /// <param name="nonMonthFormula">Скільки немісячних Formula колонок (кожна має супутник _Text).</param>
    private async Task<Built> BuildAsync(int monthCalculated = 12, int plainNonMonth = 0, bool yearLayout = true,
        int monthFormula = 0, int nonMonthFormula = 0)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        // Як T9_07 річної Land: MonthsInColumns (вʼюхи зі слотами; без них раніше діє ліміт 250 таблиць у запиті).
        var doc = await builder.BuildAsync(202601, columnCount: 2, rowCount: 1, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
        _wideTables.Add(doc.TableDefId);

        await using var db = sql.CreateContext();
        var formula = new ColumnDef(doc.TableDefId, EcrCode.Create("FRM" + Guid.NewGuid().ToString("N")[..8]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Formula" }), 9999, CellDataType.Formula);
        db.ColumnDefs.Add(formula);
        string? first = null;
        for (var i = 0; i < monthCalculated; i++)
        {
            var c = new ColumnDef(doc.TableDefId, EcrCode.Create($"CM{i:000}_M{i % 12 + 1:00}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "calc month" }), 1000 + i, CellDataType.Calculated);
            c.SetMonth(true, (byte)(i % 12 + 1));
            first ??= c.Code;
            db.ColumnDefs.Add(c);
        }

        for (var i = 0; i < monthFormula; i++)
        {
            var c = new ColumnDef(doc.TableDefId, EcrCode.Create($"FM{i:000}_M{i % 12 + 1:00}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "formula month" }), 5000 + i, CellDataType.Formula);
            c.SetMonth(true, (byte)(i % 12 + 1));
            first ??= c.Code;
            db.ColumnDefs.Add(c);
        }

        for (var i = 0; i < nonMonthFormula; i++)
        {
            db.ColumnDefs.Add(new ColumnDef(doc.TableDefId, EcrCode.Create($"NF{i:0000}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "formula" }), 6000 + i, CellDataType.Formula));
        }

        for (var i = 0; i < plainNonMonth; i++)
        {
            db.ColumnDefs.Add(new ColumnDef(doc.TableDefId, EcrCode.Create($"NM{i:0000}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "plain" }), 3000 + i, CellDataType.Decimal));
        }

        // Друга, вузька таблиця тієї ж версії: її вʼюхи мусять зʼявитися попри збій широкої.
        var narrow = new TableDef(doc.SheetDefId, EcrCode.Create("NRW" + Guid.NewGuid().ToString("N")[..6]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Narrow" }), 2000, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(narrow);
        await db.SaveChangesAsync();
        db.ColumnDefs.Add(new ColumnDef(narrow.Id, EcrCode.Create("NV1"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Value" }), 1, CellDataType.Decimal));
        await db.SaveChangesAsync();

        var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        version.Publish(publishedByUserId: 1, new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();
        if (yearLayout)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cfg.TemplateVersion SET Layout = 1 WHERE Id = {doc.TemplateVersionId}");
        }

        var template = await db.Templates.SingleAsync(t => t.Id == version.TemplateId);
        var wide = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
        static string Name(string s) => System.Text.RegularExpressions.Regex.Replace(s, "[^A-Za-z0-9_]", "_");
        var suffix = Name($"_v{version.Version}");
        var wideStable = Name($"v_{template.Code}_{doc.SheetCode}_{wide.Code}");
        var narrowStable = Name($"v_{template.Code}_{doc.SheetCode}_{narrow.Code}");
        return new Built(doc, template.Id, wideStable + suffix, narrowStable + suffix, wideStable, narrowStable, formula.Code, first!);
    }

    private async Task<List<string>> ColumnsAsync(string view)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.name FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(N'rpt.' + QUOTENAME(@v), N'V')";
        command.Parameters.AddWithValue("@v", view);
        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static void AssertNoMonthText(List<string> columns, Built b, string which)
    {
        Assert.True(columns.Count > 0, $"{which}: вʼюху не створено");
        Assert.Contains(b.FirstMonthCode, columns);                      // числова місячна колонка є
        Assert.Contains(b.FormulaCode + "_Text", columns);               // супутник немісячної - є
        Assert.DoesNotContain(columns, c => (c.StartsWith("CM", StringComparison.Ordinal) || c.StartsWith("FM", StringComparison.Ordinal))
                                            && c.EndsWith("_Text", StringComparison.Ordinal));
    }

    // ── F13 AU 18.11 (B170, R1/R4/R6/R7): гілки, яких тести вище не торкались ─────────────────────────────────────────────────

    [Fact]
    public async Task Year_layout_formula_only_table_takes_the_non_rv_branch_and_has_no_month_Text()
    {
        var b = await BuildAsync(monthCalculated: 0, monthFormula: 12);   // без Calculated - текст вʼюхи з @select, не @selectRv
        await using var db = sql.CreateContext();

        await new ReportViewGenerator(db).GenerateVersionViewsAsync(b.Doc.TemplateVersionId, CancellationToken.None);

        AssertNoMonthText(await ColumnsAsync(b.WideView), b, "версійна без rv");
    }

    [Fact]
    public async Task Year_layout_table_fits_1024_only_without_month_Text_and_both_views_are_created()
    {
        // 535 колонок + 520 місячних супутників + 24 > 1024; без місячних (D-547) - 535 + 1 + 24 - вміщується.
        var b = await BuildAsync(monthCalculated: 520);
        await using var db = sql.CreateContext();
        var generator = new ReportViewGenerator(db);

        await generator.GenerateVersionViewsAsync(b.Doc.TemplateVersionId, CancellationToken.None);
        await generator.GenerateStableViewsAsync(b.TemplateId, b.Doc.TemplateVersionId, CancellationToken.None);

        var versioned = await ColumnsAsync(b.WideView);
        AssertNoMonthText(versioned, b, "широка версійна");
        Assert.InRange(versioned.Count, 536, 1024);
        var stable = await ColumnsAsync(b.WideStable);
        AssertNoMonthText(stable, b, "широка стабільна");
        Assert.InRange(stable.Count, 536, 1024);
    }

    [Fact]
    public async Task Stable_group_over_1000_only_because_of_non_month_Text_fails_with_50422_and_the_other_groups_are_generated()
    {
        // 15 + 520 немісячних формул = 535 колонок; із 521 супутником _Text - понад 1000 (і понад 1024 у CREATE VIEW).
        var b = await BuildAsync(monthCalculated: 0, nonMonthFormula: 520);
        await using var db = sql.CreateContext();

        var ex = await Assert.ThrowsAsync<SqlException>(
            () => new ReportViewGenerator(db).GenerateStableViewsAsync(b.TemplateId, b.Doc.TemplateVersionId, CancellationToken.None));

        Assert.Equal(50422, ex.Number);                                  // не 4505 посеред циклу
        Assert.Empty(await ColumnsAsync(b.WideStable));
        Assert.NotEmpty(await ColumnsAsync(b.NarrowStable));
    }

    [Fact]
    public async Task Year_layout_versioned_view_has_no_month_Text_companions()
    {
        var b = await BuildAsync();
        await using var db = sql.CreateContext();

        await new ReportViewGenerator(db).GenerateVersionViewsAsync(b.Doc.TemplateVersionId, CancellationToken.None);

        AssertNoMonthText(await ColumnsAsync(b.WideView), b, "версійна");
    }

    [Fact]
    public async Task Monthly_layout_versioned_view_keeps_month_Text_companions()
    {
        var b = await BuildAsync(yearLayout: false);
        await using var db = sql.CreateContext();

        await new ReportViewGenerator(db).GenerateVersionViewsAsync(b.Doc.TemplateVersionId, CancellationToken.None);

        var columns = await ColumnsAsync(b.WideView);
        Assert.Contains(b.FirstMonthCode + "_Text", columns);            // Layout = 0 - як до B170
        Assert.Contains(b.FormulaCode + "_Text", columns);
    }

    [Fact]
    public async Task Year_layout_stable_view_has_no_month_Text_companions()
    {
        var b = await BuildAsync();
        await using var db = sql.CreateContext();

        await new ReportViewGenerator(db).GenerateStableViewsAsync(b.TemplateId, b.Doc.TemplateVersionId, CancellationToken.None);

        AssertNoMonthText(await ColumnsAsync(b.WideStable), b, "стабільна");
    }

    [Fact]
    public async Task Table_over_1024_columns_fails_with_50422_and_the_other_tables_are_generated()
    {
        var b = await BuildAsync(monthCalculated: 520, plainNonMonth: 500);   // 1035 колонок і без жодного супутника місяців
        await using var db = sql.CreateContext();
        var generator = new ReportViewGenerator(db);

        var ex = await Assert.ThrowsAsync<SqlException>(
            () => generator.GenerateVersionViewsAsync(b.Doc.TemplateVersionId, CancellationToken.None));
        Assert.Equal(50422, ex.Number);                                  // не 4505 посеред циклу
        Assert.Empty(await ColumnsAsync(b.WideView));
        Assert.NotEmpty(await ColumnsAsync(b.NarrowView));               // генерація не обірвалась

        var stable = await Assert.ThrowsAsync<SqlException>(
            () => generator.GenerateStableViewsAsync(b.TemplateId, b.Doc.TemplateVersionId, CancellationToken.None));
        Assert.Equal(50422, stable.Number);
        Assert.Empty(await ColumnsAsync(b.WideStable));
        Assert.NotEmpty(await ColumnsAsync(b.NarrowStable));
    }
}
