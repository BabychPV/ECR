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
        int periodKey = 202610, TableLayoutKind layout = TableLayoutKind.MonthsInColumns, int columnCount = 2,
        int rowCount = 2, bool collideText = false, int calculatedColumns = 0)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey, columnCount: columnCount, rowCount: rowCount, layoutKind: layout,
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
        if (collideText)
        {
            // AN-147: наявна колонка з кодом <Код формули>_Text - супутник цієї формули має пропуститися.
            db.ColumnDefs.Add(new ColumnDef(doc.TableDefId, EcrCode.Create(derived.Code + "_Text"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Own" }), 9996, CellDataType.Decimal));
        }
        // Q-17.recalc: колонки Calculated (ordinal 7000+) - структура публікованої версії заморожена, тож лише ДО публікації.
        for (var i = 0; i < calculatedColumns; i++)
        {
            db.ColumnDefs.Add(new ColumnDef(doc.TableDefId, EcrCode.Create("CLP" + i + Guid.NewGuid().ToString("N")[..6]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "calc" }), 7000 + i, CellDataType.Calculated));
        }

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
        Assert.DoesNotContain("COALESCE(sc.", definition, StringComparison.Ordinal);
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
        Assert.Contains("COALESCE(sc.", routed, StringComparison.Ordinal);
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

        // AN-147: еталон main не знає колонок-супутників <Код>_Text (адитивні, у кінці списку) - вони єдина допустима
        // різниця; після їх вилучення текст байт-у-байт той самий.
        await GenerateAsync(f, routing: false);
        var current = await ViewDefinitionAsync(f.View);
        Assert.Contains("_Text]", current, StringComparison.Ordinal);
        Assert.Equal(golden, System.Text.RegularExpressions.Regex.Replace(
            current, @",\r?\n\s+CASE WHEN c\d+\.ValueNumeric IS NULL THEN c\d+\.ValueString END AS \[[^\]]+_Text\]", ""));

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
    /// Q-17.perf2 (форма запиту, замінює P2-1 QA3): з прапором значення власників (місяці й якір) читає ОДНА похідна <c>fam</c> -
    /// один прохід doc.TableInstance -> doc.TableRow -> doc.CellValue таблиці з умовною агрегацією по (документ, рік, RowKey), що
    /// приєднується хеш-зʼєднанням; на рядок лишаються лише sv (власна копія) і sc (власна обчислена). Ланцюжок ow&lt;m&gt;/om&lt;m&gt;
    /// на рядок (~100 читань, 26 seek-ів TableRow на рядок на формі Land) прибрано; таблиць у запиті менше за ліміт 250.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task З_прапором_власники_читає_одна_наборова_похідна_а_на_рядок_лише_sv_і_sc()
    {
        var f = await BuildAsync(columnCount: 205);
        await GenerateAsync(f);

        var definition = await ViewDefinitionAsync(f.View);

        // На рядок doc.CellValue читають лише sc (власна обчислена) і sv (власна копія); pc (нероутовані колонки) і скалярний rv -
        // окремі apply; власників у apply на рядок немає.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(definition, "FROM doc\\.CellValue AS x WHERE x\\.PeriodKey = r\\.PeriodKey AND x\\.TableRowId = r\\.Id( AND x\\.IsCalculated = 1)?\\) AS s[vc]"));
        for (var m = 1; m <= 12; m++)
        {
            Assert.DoesNotContain($") AS om{m}", definition, StringComparison.Ordinal);
            Assert.DoesNotContain($"ow{m}.", definition, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(") AS omA", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("AS oim", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM doc.TableInstance GROUP BY", definition, StringComparison.Ordinal);
        // Одна похідна fam: місячні колонки - за місяцем екземпляра, введені не-місячні - за якорем року; приєднання хешем по
        // (документ, рік, RowKey), PeriodKind = 0 в умові.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(definition, "\\) AS fam\\b"));
        Assert.Contains("LEFT HASH JOIN (SELECT oi.DocumentId, oi.PeriodKey / 100 AS Yr, ow.RowKey", definition, StringComparison.Ordinal);
        Assert.Contains("p.PeriodKind = 0 AND fam.DocumentId = ti.DocumentId AND fam.Yr = ti.PeriodKey / 100 AND fam.RowKey = r.RowKey", definition, StringComparison.Ordinal);
        for (var m = 1; m <= 12; m++)
        {
            Assert.Contains($"oi.PeriodKey % 100 = {m} THEN", definition, StringComparison.Ordinal);
        }

        Assert.Contains("oi.PeriodKey = pa.AnchorKey THEN", definition, StringComparison.Ordinal);
        // Власник читається діапазоном ключа рядка, а не seek-ом на колонку: IN-списку колонок у apply немає.
        Assert.DoesNotContain("x.ColumnDefId IN (", definition, StringComparison.Ordinal);

        // Усі таблиці запиту (ліміт SQL Server 256): кожна doc.* - один рядок apply/приєднання.
        var tables = System.Text.RegularExpressions.Regex.Count(
            definition, "(?:FROM|JOIN)\\s+(?:doc|wf)\\.\\w+");
        Assert.True(tables < 250, $"таблиць у запиті {tables}");
    }
    /// <summary>Результат вимірювання запиту: логічні читання (STATISTICS IO) і процесорний час (STATISTICS TIME).</summary>
    private sealed record Measure(long Reads, long CpuMs);

    /// <summary>
    /// Вимірює запит: один прогрів (план у кеші), далі 3 заміри - читання останнього, CPU - мінімум (стійко до шуму).
    /// </summary>
    private async Task<Measure> MeasureAsync(string query)
    {
        long reads = 0;
        long cpu = 0;
        await using var connection = new SqlConnection(sql.ConnectionString);
        connection.InfoMessage += (_, e) =>
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         e.Message, "logical reads (\\d+)"))
            {
                reads += long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         e.Message, "CPU time = (\\d+) ms"))
            {
                cpu += long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        };
        await connection.OpenAsync();
        await using (var on = connection.CreateCommand())
        {
            on.CommandText = "SET STATISTICS IO ON; SET STATISTICS TIME ON;";
            await on.ExecuteNonQueryAsync();
        }

        var best = long.MaxValue;
        var lastReads = 0L;
        for (var run = 0; run < 4; run++)
        {
            reads = 0;
            cpu = 0;
            await using var command = connection.CreateCommand();
            command.CommandText = query;
            command.CommandTimeout = 600;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                do
                {
                    while (await reader.ReadAsync())
                    {
                    }
                }
                while (await reader.NextResultAsync());
            }

            if (run > 0)
            {
                best = Math.Min(best, cpu);
                lastReads = reads;
            }
        }

        return new Measure(lastReads, best);
    }

    /// <summary>
    /// P2-1 (перф): синтетичний шаблон ≥ 200 колонок × 12 місяців × 40 рядків. Читання вʼюхи з прапором (ON) проти
    /// вимкненого (OFF) за детермінованими логічними читаннями (CPU лише у звіті): <c>SELECT *</c> не гірше 3x; вибірка 8 колонок - не
    /// гірше 10x за читаннями (до P2-1: ~25x на цьому наборі й ~340x за часом на T9_04 Land - один apply рахував усі
    /// місяці для кожної колонки; після: ~7x). Мутація: повернути один спільний apply на всі місяці - межа 8 колонок
    /// червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Читання_вʼюхи_з_прапором_у_межах_кратності_від_вимкненого_за_cpu_і_логічними_читаннями()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var f = await BuildAsync(columnCount: 205, rowCount: 40);
        for (var m = 1; m <= 12; m++)
        {
            if (202600 + m != 202610)
            {
                await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202600 + m));
            }
        }

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var fill = connection.CreateCommand();
            fill.CommandTimeout = 600;
            fill.CommandText = """
                INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
                SELECT r.PeriodKey, r.Id, c.Id, @td, 1.5
                  FROM doc.TableRow AS r
                  JOIN doc.TableInstance AS ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
                  JOIN cfg.ColumnDef AS c ON c.TableDefId = @td AND c.DataType = @dec AND c.IsDeleted = 0
                 WHERE ti.DocumentId = @d AND ti.TableDefId = @td AND r.PeriodKey = @pk;
                """;
            fill.Parameters.AddWithValue("@td", f.Doc.TableDefId);
            fill.Parameters.AddWithValue("@d", f.Doc.DocumentId);
            fill.Parameters.AddWithValue("@dec", (byte)CellDataType.Decimal);
            var period = fill.Parameters.Add("@pk", System.Data.SqlDbType.Int);

            // Порціями по місяцю з CHECKPOINT: спільна тестова база має стелю розміру файлів (журнал не росте).
            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "CHECKPOINT";
            for (var m = 1; m <= 12; m++)
            {
                period.Value = 202600 + m;
                await fill.ExecuteNonQueryAsync();
                await checkpoint.ExecuteNonQueryAsync();
            }
        }

        await using var db = sql.CreateContext();
        var monthCodes = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == f.Doc.TableDefId && c.MonthNumber >= 1 && c.MonthNumber <= 7)
            .OrderBy(c => c.MonthNumber).Select(c => c.Code).ToListAsync();
        var columns = string.Join(", ", monthCodes.Select(c => $"[{c}]"));
        var eight = $"SELECT _RowKey, {columns} FROM rpt.[{f.View}] WHERE _DocumentId = {f.Doc.DocumentId} AND _PeriodKey = 202610";
        var star = $"SELECT * FROM rpt.[{f.View}] WHERE _DocumentId = {f.Doc.DocumentId} AND _PeriodKey = 202610";

        await GenerateAsync(f, routing: false);
        var offEight = await MeasureAsync(eight);
        var offStar = await MeasureAsync(star);
        await GenerateAsync(f, routing: true);
        var onEight = await MeasureAsync(eight);
        var onStar = await MeasureAsync(star);

        var report = $"8 колонок: ON {onEight} OFF {offEight}; SELECT *: ON {onStar} OFF {offStar}";
        Assert.True(onStar.Reads <= 3 * offStar.Reads, report);
        Assert.True(onEight.Reads <= 10 * offEight.Reads, report);
    }

    /// <summary>
    /// Q-17.recalc (перф): вʼюха з 40 колонками Calculated, прив'язаними до виходів методології (результати є для всіх
    /// 40 рядків × 40 виходів). Логічні читання <c>SELECT *</c> і вибірки 8 колонок ON/OFF; apply результатів не має
    /// давати кратності, що з'їдає бюджет T7/T10/T11. Числа друкуються в повідомленні (до/після - у звіті lane).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Читання_вʼюхи_з_колонками_Calculated_і_результатами_методології_у_межах_бюджету()
    {
        const int calcCount = 40;
        const int rows = 600;
        var f = await BuildAsync(columnCount: 150, rowCount: rows, calculatedColumns: calcCount);
        int methodologyId;
        int versionId;
        long runId;
        long historyRunId;
        int unitId;
        await using (var db = sql.CreateContext())
        {
            var methodology = new Ecr.Domain.Entities.Calculations.Methodology(
                EcrCode.Create("PRF" + Guid.NewGuid().ToString("N")[..8]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "perf" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            var version = new Ecr.Domain.Entities.Calculations.MethodologyVersion(
                methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();
            methodologyId = methodology.Id;
            versionId = version.Id;

            var calcColumns = await db.ColumnDefs.AsNoTracking()
                .Where(c => c.TableDefId == f.Doc.TableDefId && c.DataType == CellDataType.Calculated)
                .OrderBy(c => c.Id).ToListAsync();
            Assert.Equal(calcCount, calcColumns.Count);
            for (var i = 0; i < calcCount; i++)
            {
                db.CalculationBindings.Add(new CalculationBinding(f.Doc.TableDefId, calcColumns[i].Id, methodologyId, "OUT" + i, "{}"));
            }

            await db.SaveChangesAsync();

            // Історія перерахунків: Land p66 - 88 % прочитаного були результатами НЕактуальних прогонів (Superseded). Минулий прогін
            // того самого документа/періоду несе в 3 рази більше результатів, ніж актуальний, і не має впливати на вартість читання.
            var history = new Ecr.Domain.Entities.Calculations.CalculationRun(
                f.Doc.ProjectId, 202610, triggeredByUserId: 1, Now, f.Doc.DocumentId);
            db.CalculationRuns.Add(history);
            await db.SaveChangesAsync();
            history.Complete("Completed", Now, profileJson: null, errorMessage: null);
            await db.SaveChangesAsync();
            historyRunId = history.Id;

            var run = new Ecr.Domain.Entities.Calculations.CalculationRun(
                f.Doc.ProjectId, 202610, triggeredByUserId: 1, Now, f.Doc.DocumentId);
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync();
            run.Complete("Completed", Now, profileJson: null, errorMessage: null);
            run.MakeCurrent();
            await db.SaveChangesAsync();
            runId = run.Id;
            unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        }

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var fill = connection.CreateCommand();
            fill.CommandTimeout = 600;
            fill.CommandText = """
                INSERT INTO calc.CalculationResult
                    (PeriodKey, Id, CalculationRunId, MethodologyVersionId, DocumentId, SourceRowKey, OutputCode, Value, UnitId, Kind)
                SELECT 202610, NEXT VALUE FOR calc.CalculationResultSeq, @run, @ver, @d, x.RowKey, x.OutputCode, 1.5, @unit, 0
                  FROM (SELECT r.RowKey, b.OutputCode
                          FROM doc.TableRow AS r
                          JOIN doc.TableInstance AS ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
                          CROSS JOIN cfg.CalculationBinding AS b
                         WHERE ti.Id = @ti AND r.PeriodKey = 202610 AND b.TableDefId = @td AND b.MethodologyId = @m) AS x;
                """;
            fill.Parameters.AddWithValue("@run", runId);
            fill.Parameters.AddWithValue("@ver", versionId);
            fill.Parameters.AddWithValue("@d", f.Doc.DocumentId);
            fill.Parameters.AddWithValue("@unit", unitId);
            fill.Parameters.AddWithValue("@ti", f.Doc.TableInstanceId);
            fill.Parameters.AddWithValue("@td", f.Doc.TableDefId);
            fill.Parameters.AddWithValue("@m", methodologyId);
            Assert.Equal(rows * calcCount, await fill.ExecuteNonQueryAsync());

            fill.CommandText = """
                INSERT INTO calc.CalculationResult
                    (PeriodKey, Id, CalculationRunId, MethodologyVersionId, DocumentId, SourceRowKey, OutputCode, Value, UnitId, Kind)
                SELECT 202610, NEXT VALUE FOR calc.CalculationResultSeq, @run, @ver, @d, x.RowKey, x.OutputCode, 9.5 + k.n, @unit, 0
                  FROM (SELECT r.RowKey, b.OutputCode
                          FROM doc.TableRow AS r
                          JOIN doc.TableInstance AS ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
                          CROSS JOIN cfg.CalculationBinding AS b
                         WHERE ti.Id = @ti AND r.PeriodKey = 202610 AND b.TableDefId = @td AND b.MethodologyId = @m) AS x
                 CROSS JOIN (VALUES (1), (2), (3)) AS k(n);
                """;
            fill.Parameters["@run"].Value = historyRunId;
            Assert.Equal(3 * rows * calcCount, await fill.ExecuteNonQueryAsync());
        }

        var star = $"SELECT * FROM rpt.[{f.View}] WHERE _DocumentId = {f.Doc.DocumentId} AND _PeriodKey = 202610";
        await using var db2 = sql.CreateContext();
        var calcCodes = await db2.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == f.Doc.TableDefId && c.DataType == CellDataType.Calculated)
            .OrderBy(c => c.Id).Select(c => c.Code).Take(8).ToListAsync();
        var eight = $"SELECT _RowKey, {string.Join(", ", calcCodes.Select(c => $"[{c}]"))} FROM rpt.[{f.View}] WHERE _DocumentId = {f.Doc.DocumentId} AND _PeriodKey = 202610";

        await GenerateAsync(f, routing: false);
        var offStar = await MeasureAsync(star);
        var offEight = await MeasureAsync(eight);
        await GenerateAsync(f, routing: true);
        var onStar = await MeasureAsync(star);
        var onEight = await MeasureAsync(eight);

        var report = $"SELECT *: ON {onStar} OFF {offStar}; 8 Calculated: ON {onEight} OFF {offEight}";
        // Q-17.perf. Межі - у ЧИТАННЯХ (детерміновані; час на CI плаває). Набір: 600 рядків x 40 Calculated, 24 000 актуальних результатів
        // + 72 000 результатів НЕактуального прогону. Виміри (ON): SELECT * - 7,6 тис. читань, коли тест іде окремо, 31 тис. у наборі
        // класу (спільна база, інша статистика, інший план); 8 колонок Calculated - 0,4-2,3 тис. Те саме на формі p66 (корельований
        // apply на рядок x прив'язку, SourceRowKey лише residual): SELECT * 56 тис., 8 колонок 2 067 тис. читань (3,4 тис. на рядок).
        // Межа SELECT * (80 читань на рядок) лишає запас над 52 на рядок у наборі, а 8 колонок (20 тис.) - 50x над виміром і 100x
        // нижче p66: саме вона червоніє на корельованій формі. Історія прогонів (x3 результатів) на читання не впливає.
        // Форма плану (фактичний): результати читаються seek-ом IX_CalculationResult_DocRun по (PeriodKey, DocumentId, CalculationRunId) -
        // предикат документа/періоду зайшов у apply екземпляра, - не більш як кілька разів (по екземпляру), а не на кожен рядок/результат.
        var plan = System.Xml.Linq.XDocument.Parse(await PlanXmlAsync(star));
        System.Xml.Linq.XNamespace sp = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var crOps = plan.Descendants(sp + "RelOp")
            .Where(o => o.Descendants(sp + "Object").Any(x => (string?)x.Attribute("Table") == "[CalculationResult]"
                                                          && (string?)x.Attribute("Index") != null)).ToList();
        var docRunSeeks = crOps.Where(o => (string?)o.Attribute("PhysicalOp") == "Index Seek"
                                           && o.Descendants(sp + "Object").Any(x => (string?)x.Attribute("Index") == "[IX_CalculationResult_DocRun]")).ToList();
        Assert.NotEmpty(docRunSeeks);
        foreach (var op in docRunSeeks)
        {
            var executions = op.Descendants(sp + "RunTimeCountersPerThread").Sum(c => (long)c.Attribute("ActualExecutions")!);
            Assert.True(executions <= 5, $"IX_CalculationResult_DocRun seek виконано {executions} разів (по рядку/результату), {report}");
        }

        Assert.True(onStar.Reads <= 80L * rows, report);
        Assert.True(onEight.Reads <= 20_000, report);
        Assert.True(offEight.Reads <= 20_000, report);
    }

    /// <summary>
    /// Q-17.perf (T3_00 Land: PerPeriodInstance, 232 колонки = 232 LEFT JOIN doc.CellValue = 233 seek-ів на рядок, 5,4 млн виконань):
    /// з прапором ON нероутовані колонки читає ОДИН apply по ключу рядка, тож читання на рядок не ростуть з числом колонок, а
    /// вміст вʼюхи ідентичний вʼюсі без прапора.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Вʼюха_PerPeriodInstance_з_230_колонками_читає_комірки_одним_apply_і_дає_той_самий_вміст()
    {
        const int rows = 600;
        var f = await BuildAsync(layout: TableLayoutKind.PerPeriodInstance, columnCount: 230, rowCount: rows);
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var fill = connection.CreateCommand();
            fill.CommandTimeout = 600;
            // ~20 заповнених комірок на рядок (як у Land): колонки з ordinal 2..21.
            fill.CommandText = """
                INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
                SELECT r.PeriodKey, r.Id, c.Id, @td, CAST(r.Ordinal % 97 AS decimal(18, 4)) + c.Ordinal
                  FROM doc.TableRow AS r
                  JOIN cfg.ColumnDef AS c ON c.TableDefId = @td AND c.Ordinal BETWEEN 2 AND 21
                 WHERE r.TableInstanceId = @ti AND r.PeriodKey = 202610 AND c.DataType <> 0;
                """;
            fill.Parameters.AddWithValue("@td", f.Doc.TableDefId);
            fill.Parameters.AddWithValue("@ti", f.Doc.TableInstanceId);
            await fill.ExecuteNonQueryAsync();
        }

        var star = $"SELECT * FROM rpt.[{f.View}] WHERE _DocumentId = {f.Doc.DocumentId} AND _PeriodKey = 202610";
        await GenerateAsync(f, routing: false);
        var off = await MeasureAsync(star);
        var offHash = await ContentHashAsync(f);
        await GenerateAsync(f, routing: true);
        var on = await MeasureAsync(star);
        var onHash = await ContentHashAsync(f);

        var definition = await ViewDefinitionAsync(f.View);
        var report = $"ON {on} OFF {off} hash ON {onHash} OFF {offHash}";
        Assert.True(offHash == onHash, "ХЕШ " + report);
        Assert.True(System.Text.RegularExpressions.Regex.Count(definition, "\\) AS pc\\b") == 1, "pc " + report);
        Assert.DoesNotContain("LEFT JOIN doc.CellValue AS c2 ", definition, StringComparison.Ordinal);
        // Читання на рядок не залежать від числа колонок: 230 колонок = 1 apply по ключу рядка (7,3 читання на рядок). Форма
        // «LEFT JOIN doc.CellValue на колонку» (OFF, і ON до Q-17.perf для розкладок не MonthsInColumns) - 257 тис. (430 на рядок).
        Assert.True(on.Reads <= 15L * rows, report);
    }

    /// <summary>
    /// Q-17.perf: форма rv. Прив'язки лише з <c>{}</c> - наборовий rv на екземпляр (CROSS APPLY, актуальні прогони першими, хеш-
    /// зʼєднання з рядками); з'явилась активна прив'язка з предикатом MatchJson - ізольована гілка (apply на рядок), бо
    /// предикат залежить від комірок рядка. Значення в обох формах однакові (ReportViewSliceEquivalenceTests).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Форма_rv_наборова_а_з_предикатом_MatchJson_ізольований_apply_на_рядок()
    {
        var f = await BuildAsync(columnCount: 4, rowCount: 3, calculatedColumns: 2);
        int methodologyId;
        await using (var db = sql.CreateContext())
        {
            var methodology = new Ecr.Domain.Entities.Calculations.Methodology(
                EcrCode.Create("FRM" + Guid.NewGuid().ToString("N")[..8]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "form" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            methodologyId = methodology.Id;
            var calc = await db.ColumnDefs.AsNoTracking()
                .Where(c => c.TableDefId == f.Doc.TableDefId && c.DataType == CellDataType.Calculated)
                .OrderBy(c => c.Id).ToListAsync();
            db.CalculationBindings.Add(new CalculationBinding(f.Doc.TableDefId, calc[0].Id, methodologyId, "FA", "{}"));
            await db.SaveChangesAsync();
        }

        await GenerateAsync(f);
        var setBased = await ViewDefinitionAsync(f.View);
        Assert.Contains("CROSS APPLY (SELECT r0.Id, r0.RowKey, r0.Ordinal, r0.PeriodKey", setBased, StringComparison.Ordinal);
        Assert.Contains("INNER HASH JOIN (SELECT bb.Id", setBased, StringComparison.Ordinal);
        Assert.DoesNotContain("cr.SourceRowKey = r.RowKey", setBased, StringComparison.Ordinal);

        await using (var db = sql.CreateContext())
        {
            var calc = await db.ColumnDefs.AsNoTracking()
                .Where(c => c.TableDefId == f.Doc.TableDefId && c.DataType == CellDataType.Calculated)
                .OrderBy(c => c.Id).ToListAsync();
            db.CalculationBindings.Add(new CalculationBinding(f.Doc.TableDefId, calc[1].Id, methodologyId, "FB",
                $"{{\"{f.C2Id}\": \"x\"}}"));
            await db.SaveChangesAsync();
        }

        await GenerateAsync(f);
        var isolated = await ViewDefinitionAsync(f.View);
        Assert.Contains("cr.SourceRowKey = r.RowKey", isolated, StringComparison.Ordinal);
        Assert.DoesNotContain("CROSS APPLY (SELECT r0.Id", isolated, StringComparison.Ordinal);
    }

    /// <summary>Фактичний план запиту (<c>SET STATISTICS XML</c>) - для перевірки ФОРМИ плану, а не лише читань.</summary>
    private async Task<string> PlanXmlAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        command.CommandText = "SET STATISTICS XML ON; " + query;
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
        return last;
    }

    private async Task<string> ContentHashAsync(Fixture f)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        command.CommandText = $"SELECT * FROM rpt.[{f.View}] WHERE _DocumentId = @d AND _PeriodKey = 202610 ORDER BY _RowOrdinal";
        command.Parameters.AddWithValue("@d", f.Doc.DocumentId);
        await using var reader = await command.ExecuteReaderAsync();
        var text = new System.Text.StringBuilder();
        while (await reader.ReadAsync())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (!reader.GetName(i).StartsWith('_') || reader.GetName(i) == "_RowOrdinal")
                {
                    text.Append(reader.IsDBNull(i) ? "~" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append(';');
                }
            }

            text.Append('\n');
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary>
    /// D5 (QA3): з прапором генерація має власну стелю команди (холодна перебудова сотень вʼюх > 60 с), яка після виклику
    /// повертається - контекст scoped і живе далі (публікація версії); без прапора таймаут контексту не чіпається.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Генерація_з_прапором_повертає_таймаут_команди_контексту_а_без_прапора_не_чіпає_його()
    {
        var f = await BuildAsync();
        await using var db = sql.CreateContext();
        db.Database.SetCommandTimeout(77);
        var router = new MonthColumnRouter(
            db,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
                .Build());

        await new ReportViewGenerator(db, monthRouter: router).GenerateAsync(f.Doc.TemplateVersionId, CancellationToken.None);
        Assert.Equal(77, db.Database.GetCommandTimeout());

        await new ReportViewGenerator(db).GenerateAsync(f.Doc.TemplateVersionId, CancellationToken.None);
        Assert.Equal(77, db.Database.GetCommandTimeout());
        Assert.True(ReportViewGenerator.RoutedGenerationTimeoutSeconds >= 180);
    }

    /// <summary>
    /// P2-1: значення колонки місяця m береться ЛИШЕ з екземпляра цього місяця - стороння комірка тієї ж колонки в
    /// екземплярі іншого місяця (стара копія чи артефакт) у вʼюху не потрапляє. Мутація: діапазон року замість рівності
    /// ключа в apply місяця - тест червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Колонка_місяця_не_читається_з_екземпляра_іншого_місяця()
    {
        var f = await BuildAsync();
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var nov = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202611));
        var dec = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202612));

        await PutAsync(f, 202611, nov.RowIds[0], f.M11Id, 111m);     // власник M11
        await PutAsync(f, 202612, dec.RowIds[0], f.M11Id, 9999m);    // чужий місяць: колонка M11 у грудні - не власник
        await GenerateAsync(f);

        Assert.Equal(111m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 0), f.M11));
        Assert.Equal(111m, await ViewValueAsync(f, 202612, RowKeyOf(f.Doc, 0), f.M11));
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

    private async Task PutCalcAsync(Fixture f, int period, long rowId, int columnId, decimal value)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated)
            VALUES ({period}, {rowId}, {columnId}, {f.Doc.TableDefId}, @v, 1);
            """;
        command.Parameters.AddWithValue("@v", value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Значення зрізу API (MonthRoutedCellStore, прапор ON) для комірки перегляду; <c>null</c> - комірки нема.</summary>
    private async Task<decimal?> SliceValueAsync(Fixture f, int period, long rowId, int columnId)
    {
        await using var db = sql.CreateContext();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
            .Build();
        using var memory = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var store = new MonthRoutedCellStore(
            new NormalizedCellStore(db), new MonthColumnRouter(db, config),
            new Ecr.Infrastructure.Caching.MetadataCache(memory, db), db);
        var slice = await store.ReadSliceAsync(f.Doc.TableInstanceId, new PeriodKey(period), CancellationToken.None);
        return slice.SingleOrDefault(c => c.Address.TableRowId == rowId && c.Address.ColumnDefId == columnId)
            ?.Value.ValueNumeric;
    }

    /// <summary>
    /// P2-1 (QA Audit-2 проход C): обчислена комірка (IsCalculated = 1) введеної за типом місячної/якірної колонки в
    /// екземплярі перегляду - СВОЯ (так її віддає зріз API); вʼюха з прапором брала значення власника місяця (у звіті
    /// 25 проти 0 на екрані). Введена комірка власника (IsCalculated = 0) і далі має пріоритет над копією.
    /// Еталон - зріз <see cref="MonthRoutedCellStore"/>. Мутація: прибрати sc.cv з COALESCE у <c>05-rpt-views.sql</c> - червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Обчислена_комірка_перегляду_віддається_вʼюхою_як_у_зрізі_а_не_значення_власника()
    {
        var f = await BuildAsync();
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var anchor = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202601));
        var nov = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202611));

        // Рядок 1: власник місяця зберігає 111, обчислена копія в перегляді - 0 (як April: 25 проти 0 у Land).
        await PutCalcAsync(f, 202611, nov.RowIds[0], f.M11Id, 111m);
        await PutCalcAsync(f, 202610, f.Doc.RowIds[0], f.M11Id, 0m);
        // Не-місячна: обчислена і в якорі, і в перегляді.
        await PutCalcAsync(f, 202601, anchor.RowIds[0], f.C2Id, 22m);
        await PutCalcAsync(f, 202610, f.Doc.RowIds[0], f.C2Id, 3m);
        // Рядок 2: власного обчисленого немає, введене значення власника - береться (як і раніше).
        await PutAsync(f, 202611, nov.RowIds[1], f.M11Id, 77m);
        await GenerateAsync(f);

        var key0 = RowKeyOf(f.Doc, 0);
        var key1 = RowKeyOf(f.Doc, 1);
        Assert.Equal(0m, await ViewValueAsync(f, 202610, key0, f.M11));
        Assert.Equal(3m, await ViewValueAsync(f, 202610, key0, f.C2));
        Assert.Equal(77m, await ViewValueAsync(f, 202610, key1, f.M11));

        // Еквівалентність вʼюха == зріз для кожної перевіреної комірки.
        foreach (var (row, rowKey, colId, col) in new[]
                 {
                     (f.Doc.RowIds[0], key0, f.M11Id, f.M11), (f.Doc.RowIds[0], key0, f.C2Id, f.C2),
                     (f.Doc.RowIds[1], key1, f.M11Id, f.M11),
                 })
        {
            Assert.Equal(await SliceValueAsync(f, 202610, row, colId), await ViewValueAsync(f, 202610, rowKey, col));
        }
    }

    /// <summary>
    /// AN-147: рядкова обчислювана комірка (ValueString, IsCalculated = 1) у колонці Formula - числова колонка вʼюхи, як і
    /// раніше, NULL (тип/значення не змінено), а колонка-супутник <c>&lt;Код&gt;_Text</c> віддає рядок; і при вимкненому, і
    /// при увімкненому прапорі (обчислена власна комірка Formula не підміняється власником). Числовий результат -
    /// навпаки: число в основній колонці, NULL у супутнику. Супутник - nvarchar і стоїть ОСТАННІМ у вʼюсі.
    /// Мутація: прибрати <c>@selectText</c> у <c>05-rpt-views.sql</c> - тест червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Рядкове_значення_обчислюваної_колонки_віддається_супутником_Text_а_число_не_змінюється()
    {
        var f = await BuildAsync();
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var anchor = await builder.AddPeriodInstanceAsync(f.Doc, new PeriodKey(202601));
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, IsCalculated)
                VALUES (202610, {f.Doc.RowIds[0]}, {f.DerivedId}, {f.Doc.TableDefId}, N'Sample J-0', 1);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await PutCalcAsync(f, 202610, f.Doc.RowIds[1], f.DerivedId, 7m);
        await PutCalcAsync(f, 202601, anchor.RowIds[0], f.DerivedId, 70m);
        var text = f.Derived + "_Text";

        foreach (var routing in new[] { false, true })
        {
            await GenerateAsync(f, routing);
            Assert.Null(await ViewObjectAsync(f, 202610, RowKeyOf(f.Doc, 0), f.Derived));
            Assert.Equal("Sample J-0", await ViewObjectAsync(f, 202610, RowKeyOf(f.Doc, 0), text));
            Assert.Equal(7m, await ViewValueAsync(f, 202610, RowKeyOf(f.Doc, 1), f.Derived));
            Assert.Null(await ViewObjectAsync(f, 202610, RowKeyOf(f.Doc, 1), text));
            Assert.Equal(70m, await ViewValueAsync(f, 202601, RowKeyOf(f.Doc, 0), f.Derived));
            Assert.Null(await ViewObjectAsync(f, 202601, RowKeyOf(f.Doc, 0), text));

            var types = await ViewColumnTypesAsync(f.View);
            Assert.Contains(types, t => t.StartsWith(f.Derived + ":decimal:", StringComparison.Ordinal));
            Assert.StartsWith(text + ":nvarchar:", types[^1], StringComparison.Ordinal);
            Assert.Single(types, t => t.Contains("_Text:", StringComparison.Ordinal));
        }
    }

    /// <summary>AN-147: супутник лише для Formula/Calculated - введені колонки (рядкова, число) супутника не мають.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Супутник_Text_лише_для_Formula_і_Calculated()
    {
        var f = await BuildAsync();
        await GenerateAsync(f);

        var names = (await ViewColumnTypesAsync(f.View)).Select(t => t.Split(':')[0]).ToList();
        Assert.Equal(new[] { f.Derived + "_Text" }, names.Where(n => n.EndsWith("_Text", StringComparison.Ordinal)));
    }

    /// <summary>
    /// AN-147: колізія імені - якщо в таблиці вже є колонка з кодом <c>&lt;Код&gt;_Text</c>, супутник для цієї колонки
    /// ПРОПУСКАЄТЬСЯ, наявна колонка лишається як є (тип числа), вʼюха будується без помилки дублікатів.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Колізія_з_наявною_колонкою_Text_супутник_пропускається()
    {
        var f = await BuildAsync(collideText: true);
        await GenerateAsync(f);
        var types = await ViewColumnTypesAsync(f.View);
        Assert.Single(types, t => t.StartsWith(f.Derived + "_Text:", StringComparison.Ordinal));
        Assert.Contains(types, t => t.StartsWith(f.Derived + "_Text:decimal:", StringComparison.Ordinal));
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
