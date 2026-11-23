// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewMonthRowTests.cs
using System.Globalization;
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
/// D-573 (людина 23.11, «Додати вузькі поряд»): поряд із кожною широкою вʼюхою (стабільною й <c>_vN</c>) генерується вʼюха
/// «місяць - рядок» <c>&lt;вʼюха&gt;_m</c> (<c>rpt.usp_GenerateMonthView</c>, <c>05-rpt-views.sql</c>). Еталон - широка вʼюха: для кожного
/// рядка й місяця <c>_m</c>.(<c>_RowId</c>, <c>_Month</c> = mm).<c>X</c> = широка.<c>X_Mmm</c> комірка в комірку (родини числова,
/// рядкова неповна - 3 місяці, Formula із супутником <c>_Text</c>), немісячні колонки - лише в рядку <c>_Month</c> = 0, службові - в
/// кожному рядку; типи колонок родини = типи колонок широкої.
/// </summary>
/// <remarks>
/// Мутації (локально, у <c>rpt.usp_GenerateMonthView</c>): (1) <c>c.Mo = k.Mo</c> → <c>c.Mo = k.Mo % 12 + 1</c> (місяць зсунуто) -
/// перший тест червоний; (2) немісячна колонка в усіх рядках (<c>WHEN o.Kind = 'A' AND k.Mo = 0</c> → <c>WHEN o.Kind = 'A'</c>) -
/// перший тест червоний; (3) прибрати перевірку _m з умови пропуску AN-155d - <see cref="ReportViewMonthRowStartupTests"/> червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportViewMonthRowTests(SqlServerFixture sql) : ReportViewMonthRowCases(sql)
{
    [Fact]
    [Trait("Requirement", "ФВ-10.13")]
    public Task Month_row_view_equals_the_wide_view_cell_for_cell_for_version_and_stable_views() => MonthRowEqualsWideAsync();
}

/// <summary>
/// D-573, старт (AN-155d): відсутня <c>_m</c> додається без DDL широкої, повтор - без DDL. ВЛАСНА база: прогін «усі шаблони»
/// у спільній базі колекції кидає 50409 на навмисно «зіткнених» шаблонах інших тестів (<see cref="ReportViewGeneratorTests"/>:
/// <c>Q_X/S/T</c> і <c>Q/X_S/T</c>) - CI 23.11, run 45177336935, server-part (як <see cref="ReportViewStartupSkipTests"/>).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class ReportViewMonthRowStartupTests() : ReportViewMonthRowCases(SqlServerFixture.WithOwnDatabase("_viewmrow")), IAsyncLifetime
{
    public Task InitializeAsync() => Sql.InitializeAsync();

    public Task DisposeAsync() => Sql.DisposeAsync();

    [Fact]
    [Trait("Requirement", "ФВ-10.13")]
    public Task Startup_adds_a_missing_month_row_view_without_touching_the_wide_view_and_then_skips() => StartupAddsMonthViewAsync();
}

/// <summary>Спільні кроки тестів D-573 (база - <see cref="Sql"/>).</summary>
public abstract class ReportViewMonthRowCases(SqlServerFixture sql)
{
    protected SqlServerFixture Sql => sql;
    private static readonly string[] Families = ["QTY", "LBL", "FX", "FX_Text"];

    private static readonly int[] AllMonths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    private sealed record Built(TestDocument Doc, string VersionView, string StableView);

    protected async Task MonthRowEqualsWideAsync()
    {
        var b = await BuildAsync();

        foreach (var wide in new[] { b.VersionView, b.StableView })
        {
            var wideRows = await ReadAsync(wide);
            var monthRows = await ReadAsync(wide + "_m");
            Assert.Equal(3, wideRows.Count);
            var months = AllMonths;
            Assert.Equal(wideRows.Count * months.Length, monthRows.Count);   // кожен місяць колонок + річний рядок

            var byKey = monthRows.ToDictionary(r => ((long)r["_RowId"]!, Convert.ToInt32(r["_Month"], CultureInfo.InvariantCulture)));
            foreach (var w in wideRows)
            {
                var rowId = (long)w["_RowId"]!;
                foreach (var (name, value) in w)
                {
                    var family = FamilyOf(name, out var month);
                    if (name.StartsWith('_'))
                    {
                        foreach (var mo in months)
                        {
                            Assert.Equal(value, byKey[(rowId, mo)][name]);   // службові - у кожному рядку
                        }
                    }
                    else if (family is not null)
                    {
                        Assert.Equal(value, byKey[(rowId, month)][family]);
                        Assert.NotEqual(DBNull.Value, byKey[(rowId, month)]["_Month"]);
                    }
                    else
                    {
                        Assert.Equal(value, byKey[(rowId, 0)][name]);       // немісячна - лише в _Month = 0
                        foreach (var mo in months.Where(m => m > 0))
                        {
                            Assert.Equal(DBNull.Value, byKey[(rowId, mo)][name]);
                        }
                    }
                }

                foreach (var family in Families)
                {
                    Assert.Equal(DBNull.Value, byKey[(rowId, 0)][family]);   // родина в річному рядку - NULL
                }

                Assert.Equal(DBNull.Value, byKey[(rowId, 7)]["LBL"]);       // місяця 7 у родині LBL немає
                Assert.Equal($"r{rowId}m3", byKey[(rowId, 3)]["LBL"]);
                Assert.Equal(rowId * 100m + 12m, Convert.ToDecimal(byKey[(rowId, 12)]["QTY"], CultureInfo.InvariantCulture));
            }

            Assert.Equal(await TypeAsync(wide, "QTY_M05"), await TypeAsync(wide + "_m", "QTY"));
            Assert.Equal(await TypeAsync(wide, "FX_M01_Text"), await TypeAsync(wide + "_m", "FX_Text"));
            Assert.DoesNotContain(await ColumnsAsync(wide + "_m"), c => c.StartsWith("QTY_M", StringComparison.Ordinal));
        }
    }

    protected async Task StartupAddsMonthViewAsync()
    {
        var b = await BuildAsync();
        await using (var db = sql.CreateContext())
        {
            // Повна генерація пише відбитки (== входи): далі пропуск на старті вирішує лише наявність вʼюх.
            db.Database.SetCommandTimeout(300);
            await new ReportViewGenerator(db).GenerateAllAsync(onlyIfChanged: false, CancellationToken.None);
        }

        await ExecuteAsync($"DROP VIEW rpt.{Q(b.VersionView + "_m")}; DROP VIEW rpt.{Q(b.StableView + "_m")};");   // стан до D-573
        var wideBefore = await ModifiedAsync(b.VersionView, b.StableView);

        await using (var db = sql.CreateContext())
        {
            db.Database.SetCommandTimeout(300);
            await new ReportViewGenerator(db).GenerateAllAsync(onlyIfChanged: true, CancellationToken.None);
        }

        Assert.NotEmpty(await ColumnsAsync(b.VersionView + "_m"));
        Assert.NotEmpty(await ColumnsAsync(b.StableView + "_m"));
        Assert.Equal(wideBefore, await ModifiedAsync(b.VersionView, b.StableView));   // широкі - без DDL
        var monthBefore = await ModifiedAsync(b.VersionView + "_m", b.StableView + "_m");

        await using (var db = sql.CreateContext())
        {
            db.Database.SetCommandTimeout(300);
            await new ReportViewGenerator(db).GenerateAllAsync(onlyIfChanged: false, CancellationToken.None);
        }

        Assert.Equal(monthBefore, await ModifiedAsync(b.VersionView + "_m", b.StableView + "_m"));   // повтор - Unchanged
    }

    private static string? FamilyOf(string name, out int month)
    {
        month = 0;
        var text = name.EndsWith("_Text", StringComparison.Ordinal);
        var core = text ? name[..^5] : name;
        if (core.Length > 4 && core[^4..^2] == "_M" && int.TryParse(core[^2..], NumberStyles.None, CultureInfo.InvariantCulture, out month)
            && month is >= 1 and <= 12 && Families.Contains(core[..^4]))
        {
            return core[..^4] + (text ? "_Text" : string.Empty);
        }

        month = 0;
        return null;
    }

    private async Task<Built> BuildAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(202601, columnCount: 2, rowCount: 3);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var db = sql.CreateContext())
        {
            var added = new List<ColumnDef>();
            for (var m = 1; m <= 12; m++)
            {
                added.Add(Month(doc.TableDefId, $"QTY_M{m:00}", 100 + m, CellDataType.Decimal, m));
                if (m <= 3)
                {
                    added.Add(Month(doc.TableDefId, $"LBL_M{m:00}", 200 + m, CellDataType.String, m));
                }

                added.Add(Month(doc.TableDefId, $"FX_M{m:00}", 300 + m, CellDataType.Formula, m));
            }

            added.Add(new ColumnDef(doc.TableDefId, EcrCode.Create("ANN"), Text("annual"), 400, CellDataType.Decimal));
            db.ColumnDefs.AddRange(added);
            await db.SaveChangesAsync();
            foreach (var c in added)
            {
                ids[c.Code] = c.Id;
            }

            var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
            version.Publish(publishedByUserId: 1, new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync();
        }

        var values = new System.Text.StringBuilder();
        foreach (var rowId in doc.RowIds)
        {
            for (var m = 1; m <= 12; m++)
            {
                values.Append(CultureInfo.InvariantCulture, $"({doc.PeriodKey.Value}, {rowId}, {ids[$"QTY_M{m:00}"]}, {doc.TableDefId}, {rowId * 100 + m}, NULL),");
                if (m <= 3)
                {
                    values.Append(CultureInfo.InvariantCulture, $"({doc.PeriodKey.Value}, {rowId}, {ids[$"LBL_M{m:00}"]}, {doc.TableDefId}, NULL, N'r{rowId}m{m}'),");
                }
            }

            values.Append(CultureInfo.InvariantCulture, $"({doc.PeriodKey.Value}, {rowId}, {ids["FX_M01"]}, {doc.TableDefId}, NULL, N'n/a'),");
            values.Append(CultureInfo.InvariantCulture, $"({doc.PeriodKey.Value}, {rowId}, {ids["FX_M02"]}, {doc.TableDefId}, {rowId}.5, NULL),");
            values.Append(CultureInfo.InvariantCulture, $"({doc.PeriodKey.Value}, {rowId}, {ids["ANN"]}, {doc.TableDefId}, {rowId}.25, NULL),");
        }

        await ExecuteAsync("INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, ValueString) VALUES "
                           + values.ToString().TrimEnd(','));

        await using (var db = sql.CreateContext())
        {
            db.Database.SetCommandTimeout(120);
            var generator = new ReportViewGenerator(db);
            await generator.GenerateVersionViewsAsync(doc.TemplateVersionId, CancellationToken.None);
            await generator.GenerateStableViewsAsync(doc.TemplateId, doc.TemplateVersionId, CancellationToken.None);
            var template = await db.Templates.SingleAsync(t => t.Id == doc.TemplateId);
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
            var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
            static string Name(string s) => System.Text.RegularExpressions.Regex.Replace(s, "[^A-Za-z0-9_]", "_");
            var stable = Name($"v_{template.Code}_{doc.SheetCode}_{table.Code}");
            return new Built(doc, stable + Name($"_v{version.Version}"), stable);
        }
    }

    private static ColumnDef Month(int tableDefId, string code, int ordinal, CellDataType type, int month)
    {
        var c = new ColumnDef(tableDefId, EcrCode.Create(code), Text(code), ordinal, type);
        c.SetMonth(true, (byte)month);
        return c;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static string Q(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private async Task<List<Dictionary<string, object?>>> ReadAsync(string view)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT * FROM rpt.{Q(view)}", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private async Task<string> TypeAsync(string view, string column)
        => Convert.ToString(await ScalarAsync(
            $"SELECT CONCAT(TYPE_NAME(c.user_type_id), ':', c.max_length, ':', c.precision, ':', c.scale) FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(N'rpt.{Q(view)}', N'V') AND c.name = N'{column}'"),
            CultureInfo.InvariantCulture) ?? string.Empty;

    private async Task<List<string>> ColumnsAsync(string view)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT c.name FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(N'rpt.' + QUOTENAME(@v), N'V')", connection);
        command.Parameters.AddWithValue("@v", view);
        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private async Task<string> ModifiedAsync(string a, string b)
        => Convert.ToString(await ScalarAsync(
            $"SELECT CONCAT(CONVERT(varchar(30), (SELECT modify_date FROM sys.objects WHERE object_id = OBJECT_ID(N'rpt.{Q(a)}')), 121), '|', CONVERT(varchar(30), (SELECT modify_date FROM sys.objects WHERE object_id = OBJECT_ID(N'rpt.{Q(b)}')), 121))"),
            CultureInfo.InvariantCulture) ?? string.Empty;

    private async Task<object?> ScalarAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(text, connection);
        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(text, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }
}
