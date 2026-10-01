// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewGeneratorTests.cs
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
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
/// Шар сирих даних документів для SSRS (<c>ФВ-10.2</c>, <c>ФВ-10.4</c>; рішення
/// людини 2026-09-30): «довга» <c>rpt.v_DocumentCells</c> і «широкі» вʼюхи
/// <c>rpt.usp_GenerateTemplateViews</c> на живому SQL Server.
/// </summary>
/// <remarks>
/// ⚠ Перевіряється РОЗГОРНУТА вʼюха — її колонки в <c>sys.columns</c> і рядки,
/// які вона віддає, — а не текст процедури: споживач (SSRS) бачить саме це.
/// Кожен тест будує власний шаблон, тож вʼюхи тестів не перетинаються.
/// </remarks>
[Collection("SqlServer")]
public sealed partial class ReportViewGeneratorTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Публікація_шаблону_дає_широку_вʼюху_таблиці_з_кодами_колонок()
    {
        var (doc, view, codes) = await PublishedDocumentAsync();

        await using (var db = sql.CreateContext())
        {
            await new ReportViewGenerator(db).GenerateAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        // ⛔ Контракт: службові колонки з `_`, далі колонки шаблону ЇХНІМИ
        // КОДАМИ, кожна — типом свого значення (текст → nvarchar, число → decimal).
        Assert.Equal(
            [
                ("_ProjectId", "int"), ("_ProjectCode", "nvarchar"), ("_DocumentId", "bigint"),
                ("_DocumentKey", "nvarchar"), ("_PeriodKey", "int"), ("_Status", "tinyint"),
                ("_RowId", "bigint"), ("_RowKey", "nvarchar"), ("_RowOrdinal", "int"),
                (codes[0], "nvarchar"), (codes[1], "decimal"), (codes[2], "decimal"),
            ],
            await ColumnsAsync(view));

        // Рядок таблиці = рядок вʼюхи; відсутня комірка — NULL, а не зниклий
        // рядок; статус подання аркуша — колонкою (Submitted = 1), без фільтра.
        var rows = await QueryAsync<(string, byte, string?, decimal?)>(
            $"SELECT _RowKey, _Status, [{codes[0]}], [{codes[1]}] FROM rpt.[{view}] "
            + "WHERE _DocumentId = @doc ORDER BY _RowOrdinal",
            doc.DocumentId,
            r => (r.GetString(0), r.GetByte(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetDecimal(3)));

        Assert.Equal(
            [(RowKeyOf(doc, 0), (byte)1, "a", 1.5m), (RowKeyOf(doc, 1), (byte)1, null, 2m)],
            rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Довга_вʼюха_віддає_кожну_комірку_з_адресою_і_статусом()
    {
        var (doc, _, codes) = await PublishedDocumentAsync();

        var rows = await QueryAsync<(string, string, string?, decimal?, byte, int)>(
            "SELECT RowKey, ColumnCode, ValueString, ValueNumeric, Status, PeriodKey FROM rpt.v_DocumentCells "
            + "WHERE DocumentId = @doc ORDER BY RowOrdinal, ColumnCode",
            doc.DocumentId,
            r => (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                  r.IsDBNull(3) ? null : r.GetDecimal(3), r.GetByte(4), r.GetInt32(5)));

        Assert.Equal(
            [
                (RowKeyOf(doc, 0), codes[0], "a", null, (byte)1, doc.PeriodKey.Value),
                (RowKeyOf(doc, 0), codes[1], null, 1.5m, (byte)1, doc.PeriodKey.Value),
                (RowKeyOf(doc, 1), codes[1], null, 2m, (byte)1, doc.PeriodKey.Value),
            ],
            rows.OrderBy(r => r.Item1, StringComparer.Ordinal).ThenBy(r => r.Item2, StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.12")]
    public async Task Чернетка_вʼюхи_не_має_а_повторна_генерація_нічого_не_змінює()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(columnCount: 2, rowCount: 1);
        var view = await ViewNameAsync(doc);

        // Чернетка — структура ще змінюється, вʼюха над нею була б неправдою.
        Assert.Empty(await ExecAsync(doc.TemplateVersionId));
        Assert.Empty(await ColumnsAsync(view));

        await PublishAsync(doc.TemplateVersionId);

        Assert.Equal([$"{view}:Created"], await ExecAsync(doc.TemplateVersionId));

        // ⚠ Повторна публікація чи повторний старт: вʼюха не перестворюється.
        Assert.Equal([$"{view}:Unchanged"], await ExecAsync(doc.TemplateVersionId));
        Assert.Equal([$"{view}:Unchanged"], await ExecAsync(doc.TemplateVersionId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.12")]
    public async Task Дві_таблиці_з_одним_іменем_вʼюхи_відмовляють_а_не_переписують_одна_одну()
    {
        var tag = "Q" + Guid.NewGuid().ToString("N")[..8];

        // `Q_X` / `S` / `T` і `Q` / `X_S` / `T` дають одне ім'я `v_Q_X_S_T_v1`.
        await StructureAsync($"{tag}_X", "S", "T");
        var second = await StructureAsync(tag, "X_S", "T");

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(second));

        Assert.Equal(50409, error.Number);
        Assert.Contains($"v_{tag}_X_S_T_v1", error.Message, StringComparison.Ordinal);
        Assert.Empty(await ColumnsAsync($"v_{tag}_X_S_T_v1"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Роль_rpt_reader_читає_вʼюхи_але_не_таблиці_документів()
    {
        var (doc, view, _) = await PublishedDocumentAsync();
        await using (var db = sql.CreateContext())
        {
            await new ReportViewGenerator(db).GenerateAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            IF USER_ID(N'rpt_probe') IS NULL CREATE USER rpt_probe WITHOUT LOGIN;
            ALTER ROLE rpt_reader ADD MEMBER rpt_probe;
            """);

        // ⛔ Обліковий запис SSRS читає і довгу, і згенеровану вʼюху (право на
        // СХЕМУ — нова вʼюха не потребує окремого GRANT), а таблиці `doc.*`
        // напряму — ні: ланцюг власності пропускає лише крізь вʼюху.
        await ExecuteAsync(connection, "EXECUTE AS USER = N'rpt_probe';");
        try
        {
            Assert.Equal(3, await ScalarAsync(connection,
                $"SELECT COUNT(*) FROM rpt.v_DocumentCells WHERE DocumentId = {doc.DocumentId}"));
            Assert.Equal(2, await ScalarAsync(connection,
                $"SELECT COUNT(*) FROM rpt.[{view}] WHERE _DocumentId = {doc.DocumentId}"));

            var denied = await Assert.ThrowsAsync<SqlException>(
                () => ScalarAsync(connection, "SELECT COUNT(*) FROM doc.CellValue"));
            Assert.Equal(229, denied.Number);
        }
        finally
        {
            await ExecuteAsync(connection, "REVERT;");
        }
    }

    /// <summary>
    /// Межа ширини вʼюхи: 250 колонок — Created, 251 — <c>50422</c> з кількістю в повідомленні, вʼюхи нема
    /// (TESTER-GUIDE §7.6: «тесту на 250+ колонок нема» — тепер є).
    /// Мутація: у <c>05-rpt-views.sql</c> замінити <c>@n &gt; 250</c> на <c>@n &gt; 251</c> — 251 стане Created.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Таблиця_понад_250_колонок_вʼюхи_не_отримує_а_250_отримує()
    {
        var edge = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(columnCount: 250, rowCount: 1);
        await PublishAsync(edge.TemplateVersionId);
        var edgeView = await ViewNameAsync(edge);
        Assert.Equal([$"{edgeView}:Created"], await ExecAsync(edge.TemplateVersionId));

        var wide = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(columnCount: 251, rowCount: 1);
        await PublishAsync(wide.TemplateVersionId);
        var wideView = await ViewNameAsync(wide);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(wide.TemplateVersionId));

        Assert.Equal(50422, error.Number);
        Assert.Contains("251", error.Message, StringComparison.Ordinal);
        Assert.Empty(await ColumnsAsync(wideView));
    }

    /// <summary>
    /// Документ на опублікованій версії: рядок 1 — текст <c>a</c> і число 1.5,
    /// рядок 2 — лише число 2; аркуш поданий.
    /// </summary>
    private async Task<(TestDocument Doc, string View, string[] Codes)> PublishedDocumentAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(columnCount: 3, rowCount: 2);
        await PublishAsync(doc.TemplateVersionId);

        await using var db = sql.CreateContext();
        var codes = await db.ColumnDefs
            .Where(c => doc.ColumnDefIds.Contains(c.Id))
            .OrderBy(c => c.Id)
            .Select(c => c.Code)
            .ToArrayAsync();
        var tableDefId = doc.TableDefId;

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"""
                INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric)
                VALUES ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[0]}, {tableDefId}, N'a', NULL),
                       ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {tableDefId}, NULL, 1.5),
                       ({doc.PeriodKey.Value}, {doc.RowIds[1]}, {doc.ColumnDefIds[1]}, {tableDefId}, NULL, 2);
                """);
        }

        var approval = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value);
        approval.Submit(userId: 1, Now);
        db.ApprovalStates.Add(approval);
        await db.SaveChangesAsync();

        return (doc, await ViewNameAsync(doc), codes);
    }

    private async Task PublishAsync(int templateVersionId)
    {
        await using var db = sql.CreateContext();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == templateVersionId);
        version.Publish(publishedByUserId: 1, Now);
        await db.SaveChangesAsync();
    }

    /// <summary>Опублікована версія <c>1</c> шаблону з одним аркушем, таблицею й колонкою.</summary>
    private async Task<int> StructureAsync(string template, string sheet, string table)
    {
        await using var db = sql.CreateContext();
        var t = new Template(EcrCode.Create(template), Name(template), 1, Now);
        db.Templates.Add(t);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(t.Id, "1", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var s = new SheetDef(version.Id, EcrCode.Create(sheet), Name(sheet), 1);
        db.SheetDefs.Add(s);
        await db.SaveChangesAsync();

        var td = new TableDef(s.Id, EcrCode.Create(table), Name(table), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(td);
        await db.SaveChangesAsync();

        db.ColumnDefs.Add(new ColumnDef(td.Id, EcrCode.Create("V"), Name("V"), 1, CellDataType.Decimal));
        await db.SaveChangesAsync();

        version.Publish(publishedByUserId: 1, Now);
        await db.SaveChangesAsync();
        return version.Id;
    }

    /// <summary>Очікуване ім'я вʼюхи — з кодів у базі, тим самим правилом, що й процедура.</summary>
    private async Task<string> ViewNameAsync(TestDocument doc)
    {
        await using var db = sql.CreateContext();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        var template = await db.Templates.SingleAsync(t => t.Id == version.TemplateId);
        var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);

        return NotIdentifier().Replace(
            $"v_{template.Code}_{doc.SheetCode}_{table.Code}_v{version.Version}", "_");
    }

    private static string RowKeyOf(TestDocument doc, int index)
        => $"R{index + 1}_{doc.SheetCode["SHEET".Length..]}";

    /// <summary>Виконує процедуру напряму й повертає <c>ім'я:дія</c> з її результату.</summary>
    private async Task<List<string>> ExecAsync(int templateVersionId)
    {
        var result = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId";
        command.Parameters.AddWithValue("@TemplateVersionId", templateVersionId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add($"{reader.GetString(1)["rpt.".Length..]}:{reader.GetString(2)}");
        }

        return result;
    }

    /// <summary>Колонки вʼюхи з розгорнутої бази; порожньо — вʼюхи немає.</summary>
    private async Task<List<(string Name, string Type)>> ColumnsAsync(string view)
        => await QueryAsync<(string, string)>(
            """
            SELECT c.name, t.name
              FROM sys.columns c
              JOIN sys.types t ON t.user_type_id = c.user_type_id
             WHERE c.object_id = OBJECT_ID(N'rpt.' + QUOTENAME(@view), N'V')
             ORDER BY c.column_id
            """,
            view,
            r => (r.GetString(0), r.GetString(1)),
            parameter: "@view");

    private async Task<List<T>> QueryAsync<T>(
        string text, object value, Func<SqlDataReader, T> read, string parameter = "@doc")
    {
        var result = new List<T>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // ⚠ Ім'я вʼюхи в тексті — з кодів тестового шаблону, не з вводу.
        command.CommandText = text;
        command.Parameters.AddWithValue(parameter, value);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(read(reader));
        }

        return result;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string text)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string text)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotIdentifier();
}
