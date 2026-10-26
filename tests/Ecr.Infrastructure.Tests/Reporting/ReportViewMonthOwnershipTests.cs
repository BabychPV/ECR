// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewMonthOwnershipTests.cs
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

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Q-17.7: широкі вʼюхи <c>rpt.v_&lt;Шаблон&gt;_&lt;Аркуш&gt;_&lt;Таблиця&gt;_v&lt;N&gt;</c> таблиць
/// <c>MonthsInColumns</c> беруть значення місячного стовпця з екземпляра періоду-власника, а введеного
/// не-місячного — з екземпляра-якоря року, із запасним значенням власної копії (до консолідації даних).
/// </summary>
/// <remarks>
/// ⚠ Перевіряється РОЗГОРНУТА вʼюха на живому SQL Server (те, що бачить SSRS). Мутація: у
/// <c>05-rpt-views.sql</c> замінити <c>COALESCE(o…, c…)</c> на лише <c>c…</c> — тести 2–3 червоніють.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportViewMonthOwnershipTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Табличні колонки: C1 (рядок), C2 (число, введений не-місячний), M1..M12, D (Formula, похідна).</summary>
    private sealed record Fixture(TestDocument Doc, string View, string C2, string M10, string M11, string Derived,
        int C2Id, int M10Id, int M11Id, int DerivedId,
        string C1 = "", int C1Id = 0, string Flag = "", int FlagId = 0, string Day = "", int DayId = 0);

    private async Task<Fixture> BuildAsync(
        int periodKey = 202610, TableLayoutKind layout = TableLayoutKind.MonthsInColumns, int columnCount = 2)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey, columnCount: columnCount, rowCount: 2, layoutKind: layout,
            monthColumns: layout == TableLayoutKind.MonthsInColumns ? 12 : 0);

        await using var db = sql.CreateContext();
        var derived = new ColumnDef(doc.TableDefId, EcrCode.Create("DER" + Guid.NewGuid().ToString("N")[..8]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Derived" }), 9999, CellDataType.Formula);
        // Q-17.fix: різнотипні введені не-місячні колонки (Bool, Date) - тип вʼюхи не має змінитися від прапора.
        var flag = new ColumnDef(doc.TableDefId, EcrCode.Create("FLG" + Guid.NewGuid().ToString("N")[..8]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Flag" }), 9998, CellDataType.Bool);
        var day = new ColumnDef(doc.TableDefId, EcrCode.Create("DAY" + Guid.NewGuid().ToString("N")[..8]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Day" }), 9997, CellDataType.Date);
        db.ColumnDefs.AddRange(derived, flag, day);
        await db.SaveChangesAsync();

        var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        version.Publish(publishedByUserId: 1, Now);
        await db.SaveChangesAsync();

        var cols = await db.ColumnDefs.Where(c => c.TableDefId == doc.TableDefId).ToListAsync();
        var template = await db.Templates.SingleAsync(t => t.Id == version.TemplateId);
        var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
        var view = System.Text.RegularExpressions.Regex.Replace(
            $"v_{template.Code}_{doc.SheetCode}_{table.Code}_v{version.Version}", "[^A-Za-z0-9_]", "_");

        var c2 = cols.Single(c => c.Ordinal == 2);
        var m10 = layout == TableLayoutKind.MonthsInColumns ? cols.Single(c => c.MonthNumber == 10) : c2;
        var m11 = layout == TableLayoutKind.MonthsInColumns ? cols.Single(c => c.MonthNumber == 11) : c2;
        var c1 = cols.Single(c => c.Ordinal == 1);
        return new Fixture(doc, view, c2.Code, m10.Code, m11.Code, derived.Code, c2.Id, m10.Id, m11.Id, derived.Id,
            c1.Code, c1.Id, flag.Code, flag.Id, day.Code, day.Id);
    }

    /// <summary>Типізована комірка (рядок/біт/дата) - окремо від <see cref="PutAsync"/> (число).</summary>
    private async Task PutTypedAsync(Fixture f, int period, long rowId, int columnId, string valueColumn, object value)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, {valueColumn})
            VALUES ({period}, {rowId}, {columnId}, {f.Doc.TableDefId}, @v);
            """;
        command.Parameters.AddWithValue("@v", value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ViewObjectAsync(Fixture f, int period, string rowKey, string column)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT [{column}] FROM rpt.[{f.View}] WHERE _DocumentId = @d AND _PeriodKey = @p AND _RowKey = @k";
        command.Parameters.AddWithValue("@d", f.Doc.DocumentId);
        command.Parameters.AddWithValue("@p", period);
        command.Parameters.AddWithValue("@k", rowKey);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>Імена й типи колонок вʼюхи (без nullability): те, що бачить SSRS.</summary>
    private async Task<List<string>> ViewColumnTypesAsync(string view)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name + ':' + TYPE_NAME(c.user_type_id) + ':' + CAST(c.max_length AS varchar(10)) + ':'
                 + CAST(c.precision AS varchar(10)) + ':' + CAST(c.scale AS varchar(10))
              FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(N'rpt.' + QUOTENAME(@v), N'V') ORDER BY c.column_id
            """;
        command.Parameters.AddWithValue("@v", view);
        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>Генерація вʼюх; за замовчуванням з УВІМКНЕНИМ прапором (розширені вʼюхи), <c>routing: false</c> — без роутера.</summary>
    private async Task GenerateAsync(Fixture f, bool routing = true)
    {
        await using var db = sql.CreateContext();
        var router = routing
            ? new MonthColumnRouter(
                db,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
                    .Build())
            : null;
        await new ReportViewGenerator(db, monthRouter: router).GenerateAsync(f.Doc.TemplateVersionId, CancellationToken.None);
    }

    private async Task PutAsync(Fixture f, int period, long rowId, int columnId, decimal value)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
            VALUES ({period}, {rowId}, {columnId}, {f.Doc.TableDefId}, @v);
            """;
        command.Parameters.AddWithValue("@v", value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Значення колонки у рядку вʼюхи (період, ключ рядка) документа; <c>null</c> — NULL; <c>-1</c> — рядка нема.</summary>
    private async Task<decimal?> ViewValueAsync(Fixture f, int period, string rowKey, string column)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT [{column}] FROM rpt.[{f.View}] WHERE _DocumentId = @d AND _PeriodKey = @p AND _RowKey = @k";
        command.Parameters.AddWithValue("@d", f.Doc.DocumentId);
        command.Parameters.AddWithValue("@p", period);
        command.Parameters.AddWithValue("@k", rowKey);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return -1m;
        }

        return reader.IsDBNull(0) ? null : reader.GetDecimal(0);
    }

    private async Task<string> ViewDefinitionAsync(string view)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(N'rpt.' + QUOTENAME(@v), N'V'))";
        command.Parameters.AddWithValue("@v", view);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static string RowKeyOf(TestDocument doc, int index) => $"R{index + 1}_{doc.SheetCode["SHEET".Length..]}";

    /// <summary>
    /// Прапор <c>Collection:MonthColumnRouting</c> вимкнено (генератор без роутера) → текст вʼюхи MonthsInColumns без жодного
    /// приєднання власників: ніяких <c>COALESCE(o…)</c>, <c>doc.TableInstance AS oi…</c>, якоря <c>pa</c> — байт-у-байт як до Q-17.7.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Прапор_вимкнено_вʼюха_MonthsInColumns_без_приєднань_власників()
    {
        var f = await BuildAsync();
        await GenerateAsync(f, routing: false);

        var definition = await ViewDefinitionAsync(f.View);

        Assert.DoesNotContain("COALESCE(o", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("doc.TableInstance AS oi", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("doc.TableRow AS ow", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("AnchorKey", definition, StringComparison.Ordinal);
    }

    /// <summary>
    /// Вмикання прапора і повторна генерація ПЕРЕБУДОВУЮТЬ вʼюху (розширена: власники й якір), вимикання — повертає
    /// початковий текст байт-у-байт.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Зміна_прапора_і_повторна_генерація_міняють_текст_вʼюхи_і_повертають_його()
    {
        var f = await BuildAsync();

        await GenerateAsync(f, routing: false);
        var plain = await ViewDefinitionAsync(f.View);

        await GenerateAsync(f, routing: true);
        var routed = await ViewDefinitionAsync(f.View);
        Assert.Contains("COALESCE(o", routed, StringComparison.Ordinal);
        Assert.Contains("AnchorKey", routed, StringComparison.Ordinal);
        Assert.NotEqual(plain, routed);

        await GenerateAsync(f, routing: false);
        Assert.Equal(plain, await ViewDefinitionAsync(f.View));
    }

    /// <summary>
    /// GOLDEN проти main: процедура з origin/main 55f6f8010 (до Q-17.7) лежить у тестових даних без змін (окрім імені) і
    /// будує вʼюху еталонної таблиці MonthsInColumns; нова процедура з ВИМКНЕНИМ прапором мусить дати той самий текст
    /// байт-у-байт (інакше вона перебудувала б вʼюху — ALTER — і текст відрізнявся б від еталона), а з увімкненим — ні.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Прапор_вимкнено_DDL_вʼюхи_байт_у_байт_як_у_процедури_main()
    {
        var f = await BuildAsync();

        var script = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Reporting", "Golden", "usp_GenerateTemplateViews.main-55f6f8010.sql"));
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var install = connection.CreateCommand();
            install.CommandText = script;
            await install.ExecuteNonQueryAsync();

            await using var run = connection.CreateCommand();
            run.CommandText = "EXEC rpt.usp_GenerateTemplateViews_GoldenMain @TemplateVersionId = @v";
            run.Parameters.AddWithValue("@v", f.Doc.TemplateVersionId);
            await run.ExecuteNonQueryAsync();
        }

        var golden = await ViewDefinitionAsync(f.View);
        Assert.DoesNotContain("COALESCE(o", golden, StringComparison.Ordinal);

        await GenerateAsync(f, routing: false);
        Assert.Equal(golden, await ViewDefinitionAsync(f.View));

        await GenerateAsync(f, routing: true);
        Assert.NotEqual(golden, await ViewDefinitionAsync(f.View));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task До_консолідації_вʼюха_віддає_власні_копії_як_раніше()
    {
        var f = await BuildAsync();
        var row = f.Doc.RowIds[0];
        await PutAsync(f, 202610, row, f.M10Id, 10m);
        await PutAsync(f, 202610, row, f.M11Id, 11m);   // копія: M11 лише в екземплярі перегляду
        await PutAsync(f, 202610, row, f.C2Id, 2m);
        await GenerateAsync(f);

        Assert.Equal(10m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.M10));
        Assert.Equal(11m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.M11));
        Assert.Equal(2m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.C2));
        Assert.Null(await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 1), f.M11));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Після_консолідації_M11_береться_з_202611_а_не_місячна_з_якоря()
    {
        var f = await BuildAsync();
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var anchor = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202601));
        var nov = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202611));

        // Канонічні значення: M11 — лише у власника (202611), C2 — лише в якорі (202601); у перегляді 202610 порожньо.
        await PutAsync(f, 202611, nov.RowIds[0], f.M11Id, 111m);
        await PutAsync(f, 202601, anchor.RowIds[0], f.C2Id, 22m);
        await GenerateAsync(f);

        Assert.Equal(111m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.M11));
        Assert.Equal(22m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.C2));
        // Рядок з іншим RowKey власника не підтягується.
        Assert.Null(await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 1), f.M11));
        Assert.Null(await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 1), f.C2));
        // Рядок самого власника показує те саме.
        Assert.Equal(111m, await ViewValueAsync(f, 202611, RowKeyOf(f.Doc, 0), f.M11));
        Assert.Equal(22m, await ViewValueAsync(f, 202601, RowKeyOf(f.Doc, 0), f.C2));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Значення_власника_має_пріоритет_над_копією_а_без_власника_береться_копія()
    {
        var f = await BuildAsync();
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var nov = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202611));

        // Рядок 1: і копія, і власник — перемагає власник. Рядок 2: власного рядка в 202611 нема значення — копія.
        await PutAsync(f, 202610, f.Doc.RowIds[0], f.M11Id, 1m);
        await PutAsync(f, 202611, nov.RowIds[0], f.M11Id, 999m);
        await PutAsync(f, 202610, f.Doc.RowIds[1], f.M11Id, 5m);
        await GenerateAsync(f);

        Assert.Equal(999m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.M11));
        Assert.Equal(5m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 1), f.M11));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Похідна_колонка_лишається_з_рядка_перегляду_а_не_від_власника()
    {
        var f = await BuildAsync();
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var anchor = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202601));

        await PutAsync(f, 202610, f.Doc.RowIds[0], f.DerivedId, 7m);
        await PutAsync(f, 202601, anchor.RowIds[0], f.DerivedId, 70m);   // у якорі похідна — не береться
        await GenerateAsync(f);

        Assert.Equal(7m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.Derived));
        Assert.Equal(70m, await ViewValueAsync(f, 202601, RowKeyOf(f.Doc, 0), f.Derived));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Немає_екземпляра_власника_або_якоря_значення_NULL_чи_копія_без_помилки()
    {
        // Календар без 202601: якір = 202610 (мінімум єдиного періоду); екземпляра 202611 нема зовсім.
        var f = await BuildAsync();
        await PutAsync(f, 202610, f.Doc.RowIds[0], f.M11Id, 3m);
        await GenerateAsync(f);

        Assert.Equal(3m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.M11));
        Assert.Null(await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 1), f.M11));
        Assert.Null(await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.C2));

        // Період у календарі є (є й екземпляр-якір), але рядків у ньому нема — NULL, а не зниклий рядок перегляду.
        await new TestDocumentBuilder(sql.ConnectionString).AddPeriodAsync(f.Doc, new PeriodKey(202601));
        Assert.Null(await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.C2));
    }

    /// <summary>
    /// B1 (QA2): широка таблиця (≈220 колонок, як T9_04 Land) з увімкненим прапором мусить будуватися - ліміт SQL Server
    /// 256 таблиць у запиті не може рахуватися «по приєднанню на колонку». Значення збігаються з власниками.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Широка_таблиця_200_плюс_колонок_будується_з_прапором_і_віддає_значення_власників()
    {
        var f = await BuildAsync(columnCount: 205);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var anchor = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202601));
        var nov = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202611));

        await PutAsync(f, 202611, nov.RowIds[0], f.M11Id, 111m);
        await PutAsync(f, 202601, anchor.RowIds[0], f.C2Id, 22m);
        await PutAsync(f, 202610, f.Doc.RowIds[0], f.M10Id, 10m);   // власна копія власного місяця
        await PutAsync(f, 202610, f.Doc.RowIds[1], f.M11Id, 5m);    // копія без власника в рядку 2 - запасне значення

        await GenerateAsync(f);   // раніше: THROW 50422 (399 таблиць у запиті)

        Assert.Equal(111m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.M11));
        Assert.Equal(22m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.C2));
        Assert.Equal(10m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.M10));
        Assert.Equal(5m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 1), f.M11));
        Assert.Null(await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 1), f.C2));
    }

    /// <summary>
    /// Типи колонок вʼюхи з прапором ідентичні типам без прапора (string/bit/date/decimal), а значення різних типів
    /// беруться з власника з пріоритетом над копією.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Різнотипні_колонки_зберігають_типи_вʼюхи_і_беруться_з_власника()
    {
        var f = await BuildAsync();
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var anchor = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202601));

        await GenerateAsync(f, routing: false);
        var plainTypes = await ViewColumnTypesAsync(f.View);

        await PutTypedAsync(f, 202601, anchor.RowIds[0], f.C1Id, "ValueString", "owner-text");
        await PutTypedAsync(f, 202610, f.Doc.RowIds[0], f.C1Id, "ValueString", "copy-text");
        await PutTypedAsync(f, 202601, anchor.RowIds[0], f.FlagId, "ValueBool", true);
        await PutTypedAsync(f, 202610, f.Doc.RowIds[1], f.FlagId, "ValueBool", true);   // копія без власника
        await PutTypedAsync(f, 202601, anchor.RowIds[0], f.DayId, "ValueDate", new DateTime(2026, 3, 5));

        await GenerateAsync(f, routing: true);

        Assert.Equal(plainTypes, await ViewColumnTypesAsync(f.View));
        Assert.Equal("owner-text", await ViewObjectAsync(f, 202610, RowKeyOf(f.Doc, 0), f.C1));
        Assert.Equal(true, await ViewObjectAsync(f, 202610, RowKeyOf(f.Doc, 0), f.Flag));
        Assert.Equal(true, await ViewObjectAsync(f, 202610, RowKeyOf(f.Doc, 1), f.Flag));
        Assert.Equal(new DateTime(2026, 3, 5), await ViewObjectAsync(f, 202610, RowKeyOf(f.Doc, 0), f.Day));
        Assert.Null(await ViewObjectAsync(f, 202610, RowKeyOf(f.Doc, 1), f.C1));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.12")]
    public async Task Не_MonthsInColumns_вʼюха_без_приєднань_власників_і_генерація_ідемпотентна()
    {
        var plain = await BuildAsync(layout: TableLayoutKind.PerPeriodInstance);
        await PutAsync(plain, 202610, plain.Doc.RowIds[0], plain.C2Id, 4m);
        await GenerateAsync(plain);

        Assert.Equal(4m, await ViewValueAsync(plain, 202610, RowKeyOf(plain.Doc, 0), plain.C2));
        Assert.DoesNotContain("doc.Period", await ViewDefinitionAsync(plain.View), StringComparison.Ordinal);

        var months = await BuildAsync();
        await GenerateAsync(months);
        var definition = await ViewDefinitionAsync(months.View);
        Assert.Contains("doc.Period", definition, StringComparison.Ordinal);

        // Повторна генерація не змінює вʼюху (Unchanged), тож SSRS не бачить Sch-M без потреби.
        await GenerateAsync(months);
        Assert.Equal(definition, await ViewDefinitionAsync(months.View));
    }
}
