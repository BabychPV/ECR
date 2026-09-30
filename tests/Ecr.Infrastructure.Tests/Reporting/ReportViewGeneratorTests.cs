// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewGeneratorTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Генератор пласких вʼюх <c>rpt.v_&lt;Звіт&gt;_v&lt;Версія&gt;</c> для SSRS
/// (<c>ФВ-10.2</c>, <c>ФВ-10.4</c>): процедура <c>rpt.usp_GenerateReportViews</c>
/// на живому SQL Server.
/// </summary>
/// <remarks>
/// ⚠ Перевіряється РОЗГОРНУТА вʼюха — її колонки в <c>sys.columns</c> і рядки,
/// які вона віддає, — а не текст процедури: споживач (SSRS) бачить саме це.
/// Кожен тест бере власний код звіту, тож вʼюхи тестів не перетинаються.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportViewGeneratorTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Колонки: текст, число, дата — по одній на кожен тип значення зрізу.</summary>
    private const string ThreeColumns =
        """[{"code":"RowKey","kind":"text"},{"code":"Value","kind":"number"},{"code":"MeasuredAt","kind":"date"}]""";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    [Trait("Requirement", "ФВ-10.4")]
    [Trait("Requirement", "ФВ-10.11")]
    public async Task Опублікована_версія_дає_пласку_вʼюху_і_регулятор_не_бачить_чернеток()
    {
        var code = NewCode();
        await using var db = sql.CreateContext();
        var projectId = await ArrangeProjectAsync(db);
        var defId = await DefAsync(db, code, isRegulatory: true);
        var versionId = await VersionAsync(db, defId, "1.0", ThreeColumns, publish: true);

        await new ReportViewGenerator(db).GenerateAsync(defId, CancellationToken.None);

        // ⛔ Контракт вʼюхи: службові колонки з префіксом, далі колонки звіту
        // ЇХНІМИ КОДАМИ й у порядку опису, кожна — типом свого значення.
        Assert.Equal(
            [
                ("SnapshotId", "bigint"), ("SnapshotProjectId", "int"), ("SnapshotPeriodKey", "int"),
                ("SnapshotStatus", "tinyint"), ("SnapshotBuiltAt", "datetime2"),
                ("SnapshotCalculationRunId", "bigint"), ("RowNo", "int"),
                ("RowKey", "nvarchar"), ("Value", "decimal"), ("MeasuredAt", "datetime2"),
            ],
            await ColumnsAsync($"v_{code}_v1_0"));

        // Два поточні зрізи однієї версії: чернетка (січень) і затверджений (лютий).
        var draft = await SnapshotAsync(db, versionId, projectId, 202601, SnapshotStatus.Draft);
        var approved = await SnapshotAsync(db, versionId, projectId, 202602, SnapshotStatus.Approved);
        await RowsAsync(db, draft, (1, "RowKey", "draft", null, null));
        await RowsAsync(
            db, approved,
            (1, "RowKey", "a", null, null),
            (1, "Value", null, 2.5m, null),
            (1, "MeasuredAt", null, null, new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Unspecified)),
            (2, "RowKey", "b", null, null));

        var rows = await QueryAsync($"v_{code}_v1_0", projectId);

        // ⛔ ФВ-10.11: чернетка регуляторного звіту не видна — фільтр у вʼюсі.
        // Рядок 2 без значення `Value` лишається рядком із NULL, а не зникає.
        Assert.Equal(
            [
                (202602, 1, "a", (decimal?)2.5m, (DateTime?)new DateTime(2026, 2, 3)),
                (202602, 2, "b", (decimal?)null, (DateTime?)null),
            ],
            rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.10")]
    public async Task Нерегуляторний_звіт_бачить_і_чернетки()
    {
        var code = NewCode();
        await using var db = sql.CreateContext();
        var projectId = await ArrangeProjectAsync(db);
        var defId = await DefAsync(db, code, isRegulatory: false);
        var versionId = await VersionAsync(db, defId, "1", ThreeColumns, publish: true);
        var draft = await SnapshotAsync(db, versionId, projectId, 202601, SnapshotStatus.Draft);
        await RowsAsync(db, draft, (1, "RowKey", "draft", null, null));

        await new ReportViewGenerator(db).GenerateAsync(defId, CancellationToken.None);

        // ФВ-10.10: числа перевіряють ДО затвердження — внутрішній звіт це дає.
        Assert.Equal([(202601, 1, "draft", (decimal?)null, (DateTime?)null)], await QueryAsync($"v_{code}_v1", projectId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.12")]
    public async Task Повторна_генерація_нічого_не_змінює_а_нова_версія_дає_нову_вʼюху()
    {
        var code = NewCode();
        await using var db = sql.CreateContext();
        var defId = await DefAsync(db, code, isRegulatory: true);
        await VersionAsync(db, defId, "1.0", ThreeColumns, publish: true);
        await VersionAsync(db, defId, "1.1", ThreeColumns, publish: false);

        Assert.Equal([$"v_{code}_v1_0:Created"], await ExecAsync(defId));

        // ⚠ Повторна публікація / повторний старт: вʼюха не перестворюється.
        Assert.Equal([$"v_{code}_v1_0:Unchanged"], await ExecAsync(defId));

        // ⛔ D-53: нова версія — НОВА вʼюха; стара лишається зі своїми колонками,
        // і RDL, прив'язаний до неї, не ламається.
        await VersionAsync(db, defId, "2.0", """[{"code":"OutputCode","kind":"text"}]""", publish: true);

        Assert.Equal([$"v_{code}_v1_0:Unchanged", $"v_{code}_v2_0:Created"], await ExecAsync(defId));
        Assert.Equal(10, (await ColumnsAsync($"v_{code}_v1_0")).Count);
        Assert.Equal(
            ("OutputCode", "nvarchar"),
            (await ColumnsAsync($"v_{code}_v2_0"))[^1]);

        // Чернетка (1.1) вʼюхи не отримує.
        Assert.Empty(await ColumnsAsync($"v_{code}_v1_1"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.12")]
    public async Task Дві_версії_з_одним_іменем_вʼюхи_відмовляють_а_не_переписують_одна_одну()
    {
        var code = NewCode();
        await using var db = sql.CreateContext();
        var defId = await DefAsync(db, code, isRegulatory: true);
        await VersionAsync(db, defId, "1.0", ThreeColumns, publish: true);
        await VersionAsync(db, defId, "1_0", """[{"code":"OutputCode","kind":"text"}]""", publish: true);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(defId));

        Assert.Equal(50409, error.Number);
        Assert.Contains($"v_{code}_v1_0", error.Message, StringComparison.Ordinal);
        Assert.Empty(await ColumnsAsync($"v_{code}_v1_0"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Зламаний_опис_одного_звіту_не_лишає_без_вʼюх_решту()
    {
        var good = NewCode();
        var bad = NewCode();
        await using var db = sql.CreateContext();
        await VersionAsync(db, await DefAsync(db, good, isRegulatory: true), "1", ThreeColumns, publish: true);
        await VersionAsync(
            db, await DefAsync(db, bad, isRegulatory: true), "1", """[{"code":"Bad code","kind":"text"}]""",
            publish: true);

        // ⚠ Виклик для ВСІХ звітів — так його робить старт. Відмова лишається
        // відмовою (старт її журналює), але справні звіти вʼюху отримують.
        var error = await Assert.ThrowsAsync<SqlException>(
            () => new ReportViewGenerator(db).GenerateAsync(reportDefId: null, CancellationToken.None));

        Assert.True(error.Number is 50422 or 50409, error.Message);
        Assert.Equal(10, (await ColumnsAsync($"v_{good}_v1")).Count);
        Assert.Empty(await ColumnsAsync($"v_{bad}_v1"));
    }

    private static string NewCode() => "V" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<int> ArrangeProjectAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var template = new Template(
            EcrCode.Create($"T{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "View template" }),
            createdByUserId: 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync(CancellationToken.None);

        var templateVersion = new TemplateVersion(template.Id, "1.0.0.0", createdByUserId: 1, Now);
        db.TemplateVersions.Add(templateVersion);
        await db.SaveChangesAsync(CancellationToken.None);

        var project = new Project(
            EcrCode.Create($"P{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "View project" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: templateVersion.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync(CancellationToken.None);

        return project.Id;
    }

    private static async Task<int> DefAsync(EcrDbContext db, string code, bool isRegulatory)
    {
        var def = new ReportDef(
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            isRegulatory);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None);
        return def.Id;
    }

    private static async Task<int> VersionAsync(EcrDbContext db, int defId, string version, string columns, bool publish)
    {
        var entity = new ReportVersion(defId, version, columns, """{"rowSource":"CalculationResults"}""", Now);
        if (publish)
        {
            entity.Publish();
        }

        db.ReportVersions.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);
        return entity.Id;
    }

    private static async Task<long> SnapshotAsync(
        EcrDbContext db, int versionId, int projectId, int period, SnapshotStatus status)
    {
        var snapshot = new ReportSnapshot(versionId, projectId, period, status, Now, builtByUserId: null);
        snapshot.Complete(rowCount: 1, contentHash: null, calculationRunId: null, parametersJson: null);
        snapshot.MakeCurrent();
        db.ReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync(CancellationToken.None);
        return snapshot.Id;
    }

    private static async Task RowsAsync(
        EcrDbContext db, long snapshotId,
        params (int RowNo, string Column, string? Text, decimal? Number, DateTime? Date)[] cells)
    {
        foreach (var cell in cells)
        {
            var row = new ReportRow(snapshotId, cell.RowNo, cell.Column);
            row.SetValue(cell.Text, cell.Number, cell.Date);
            db.ReportRows.Add(row);
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Виконує процедуру напряму й повертає <c>ім'я:дія</c> з її результату.</summary>
    private async Task<List<string>> ExecAsync(int defId)
    {
        var result = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC rpt.usp_GenerateReportViews @ReportDefId";
        command.Parameters.AddWithValue("@ReportDefId", defId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add($"{reader.GetString(1)["rpt.".Length..]}:{reader.GetString(2)}");
        }

        return result;
    }

    /// <summary>Колонки вʼюхи з розгорнутої бази; порожньо — вʼюхи немає.</summary>
    private async Task<List<(string Name, string Type)>> ColumnsAsync(string view)
    {
        var result = new List<(string, string)>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name, t.name
              FROM sys.columns c
              JOIN sys.types t ON t.user_type_id = c.user_type_id
             WHERE c.object_id = OBJECT_ID(N'rpt.' + QUOTENAME(@view), N'V')
             ORDER BY c.column_id
            """;
        command.Parameters.AddWithValue("@view", view);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private async Task<List<(int Period, int RowNo, string? RowKey, decimal? Value, DateTime? MeasuredAt)>> QueryAsync(
        string view, int projectId)
    {
        var result = new List<(int, int, string?, decimal?, DateTime?)>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // ⚠ Ім'я вʼюхи — з коду тесту (GUID), не з вводу; параметром його не передати.
        command.CommandText =
            $"SELECT SnapshotPeriodKey, RowNo, RowKey, Value, MeasuredAt FROM rpt.[{view}] "
            + "WHERE SnapshotProjectId = @p ORDER BY SnapshotPeriodKey, RowNo";
        command.Parameters.AddWithValue("@p", projectId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
        }

        return result;
    }
}
