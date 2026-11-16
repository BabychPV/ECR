// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewWorkflowModeTests.cs
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
/// B138 (B75, звітна частина): вʼюхи сирих даних віддають режим погодження версії шаблону службовою колонкою
/// <c>_WorkflowMode</c> (<c>cfg.TemplateVersion.WorkflowMode</c>: 0 SubmitApprove, 1 None) — ОСТАННЬОЮ в списку:
/// версійна <c>rpt.v_*_vN</c> — константа своєї версії; стабільна <c>rpt.v_&lt;Шаблон&gt;_&lt;Аркуш&gt;_&lt;Таблиця&gt;</c> — за версією
/// РЯДКА (не за версією з параметра процедури); довга <c>rpt.v_DocumentCells</c> — за версією рядка. <c>_Status</c> не
/// змінюється: комірка версії None видна одразу після запису зі статусом 0.
/// Сценарій: шаблон із v1 (SubmitApprove) і v2 (None), один документ: період 209901 на таблиці v1, 209902 — на таблиці v2.
/// Власна база, періоди 2099xx.
/// </summary>
/// <remarks>
/// Мутації (локально, вихідний SQL + перезбірка, 9 тестів класу): (1) константа 0 замість режиму версії в усіх трьох місцях —
/// червоні «Версійна…», «Стабільна…» (×3), «Довга…», «Комірка…»; (2) стабільна вʼюха бере режим версії з параметра процедури —
/// червоні «Стабільна…» (×3) і «Комірка…»; (3) колонка не остання (перед <c>_SheetDisplayName</c> / <c>_IsArchived</c> /
/// <c>IsArchived</c>) — червоні «Колонка_WorkflowMode_остання…» (×2).
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "RPT-STABLE")]
public sealed partial class ReportViewWorkflowModeTests : IAsyncLifetime
{
    private const byte SubmitApprove = 0;
    private const byte None = 1;

    private static readonly DateTime Now = new(2099, 1, 2, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerFixture _sql = SqlServerFixture.WithOwnDatabase("_b138");

    public Task InitializeAsync() => _sql.InitializeAsync();

    public Task DisposeAsync() => _sql.DisposeAsync();

    [Fact]
    public async Task Версійна_вʼюха_віддає_режим_своєї_версії_константою()
    {
        var s = await ArrangeAsync();

        Assert.Equal([(209901, "a1", SubmitApprove)], await WideAsync(s.V1View, s.DocumentId));
        Assert.Equal([(209902, "a2", None)], await WideAsync(s.V2View, s.DocumentId));
        Assert.Contains("CAST(0 AS tinyint) AS _WorkflowMode", await DefinitionAsync(s.V1View), StringComparison.Ordinal);
        Assert.Contains("CAST(1 AS tinyint) AS _WorkflowMode", await DefinitionAsync(s.V2View), StringComparison.Ordinal);
    }

    /// <summary>
    /// Режим — за версією РЯДКА, хоч би яку версію шаблону передали процедурі: v1 (SubmitApprove), v2 (None) або жодної
    /// (усі шаблони).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    public async Task Стабільна_вʼюха_віддає_режим_версії_рядка_а_не_версії_з_параметра(int generateFor)
    {
        var s = await ArrangeAsync();

        await ExecAsync(generateFor switch
        {
            1 => $"EXEC rpt.usp_GenerateStableViews {s.V1}, 0, 0",
            2 => $"EXEC rpt.usp_GenerateStableViews {s.V2}, 0, 0",
            _ => "EXEC rpt.usp_GenerateStableViews NULL, 0, 0",
        });

        Assert.Equal(
            [(209901, "1", "a1", SubmitApprove), (209902, "2", "a2", None)],
            await StableAsync(s.Stable, s.DocumentId));
    }

    [Fact]
    public async Task Довга_вʼюха_віддає_режим_версії_рядка()
    {
        var s = await ArrangeAsync();

        Assert.Equal(
            [(209901, "1", "a1", (byte)0, SubmitApprove), (209902, "2", "a2", (byte)0, None)],
            await LongAsync(s.DocumentId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Колонка_WorkflowMode_остання_в_усіх_вʼюхах(bool monthRouting)
    {
        var s = await ArrangeAsync(monthRouting);

        foreach (var view in new[] { s.V1View, s.V2View, s.Stable, "v_DocumentCells" })
        {
            var columns = await ColumnsAsync(view);
            Assert.True(columns.Count > 1, $"rpt.{view}: вʼюхи немає");
            Assert.Equal(("_WorkflowMode", "tinyint"), columns[^1]);
            Assert.Single(columns, c => c.Name == "_WorkflowMode");
        }

        // Попередні «останні» колонки лишилися на місці — одразу перед новою (колонки лише додаються).
        Assert.Equal("_SheetDisplayName", (await ColumnsAsync(s.V1View))[^2].Name);
        Assert.Equal("_SheetDisplayName", (await ColumnsAsync(s.V2View))[^2].Name);
        Assert.Equal("_IsArchived", (await ColumnsAsync(s.Stable))[^2].Name);
        Assert.Equal("IsArchived", (await ColumnsAsync("v_DocumentCells"))[^2].Name);
    }

    /// <summary>
    /// «Видно одразу після запису»: нова комірка документа версії None зʼявляється в усіх трьох вʼюхах без перегенерації,
    /// без подання й затвердження (стану <c>wf.ApprovalState</c> немає) — зі статусом 0 і режимом 1.
    /// </summary>
    [Fact]
    public async Task Комірка_версії_None_видна_одразу_після_запису_зі_статусом_0()
    {
        var s = await ArrangeAsync();
        var before = (await DefinitionAsync(s.V2View), await DefinitionAsync(s.Stable));

        await SeedAsync(s.DocumentId, 209902, s.T2, s.A2, "fresh", "R2");

        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM wf.ApprovalState WHERE DocumentId = {s.DocumentId}"));
        Assert.Equal(("fresh", (byte)0, None), await FreshAsync(
            $"SELECT [A], _Status, _WorkflowMode FROM rpt.[{s.V2View}] WHERE _DocumentId = @d AND _PeriodKey = 209902 AND _RowKey = N'R2'", s.DocumentId));
        Assert.Equal(("fresh", (byte)0, None), await FreshAsync(
            $"SELECT [A], _Status, _WorkflowMode FROM rpt.[{s.Stable}] WHERE _DocumentId = @d AND _PeriodKey = 209902 AND _RowKey = N'R2'", s.DocumentId));
        Assert.Equal(("fresh", (byte)0, None), await FreshAsync(
            "SELECT ValueString, Status, _WorkflowMode FROM rpt.v_DocumentCells WHERE DocumentId = @d AND PeriodKey = 209902 AND RowKey = N'R2'", s.DocumentId));
        Assert.Equal(before, (await DefinitionAsync(s.V2View), await DefinitionAsync(s.Stable)));
    }

    // ── помічники ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Scenario(int V1, int V2, int T2, int A2, long DocumentId, string V1View, string V2View, string Stable);

    /// <summary>v1 (SubmitApprove) і v2 (None), таблиця T з колонкою A; документ: 209901 на v1, 209902 на v2; вʼюхи згенеровано.</summary>
    private async Task<Scenario> ArrangeAsync(bool monthRouting = false)
    {
        string code;
        int templateId;
        await using (var db = _sql.CreateContext())
        {
            code = "WM" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var template = new Template(EcrCode.Create(code), Name(code), 1, Now);
            db.Templates.Add(template);
            await db.SaveChangesAsync();
            templateId = template.Id;
        }

        var v1 = await AddVersionAsync(templateId, "1", WorkflowMode.SubmitApprove);
        var v2 = await AddVersionAsync(templateId, "2", WorkflowMode.None);
        var documentId = await NewDocumentAsync(v1.VersionId, 209901, 209902);
        await SeedAsync(documentId, 209901, v1.TableId, v1.ColumnId, "a1", "R1");
        await SeedAsync(documentId, 209902, v2.TableId, v2.ColumnId, "a2", "R1");

        var routing = monthRouting ? 1 : 0;
        await ExecAsync($"EXEC rpt.usp_GenerateTemplateViews {v1.VersionId}, {routing}");
        await ExecAsync($"EXEC rpt.usp_GenerateTemplateViews {v2.VersionId}, {routing}");
        await ExecAsync($"EXEC rpt.usp_GenerateStableViews {v2.VersionId}, {routing}, 0");
        return new Scenario(
            v1.VersionId, v2.VersionId, v2.TableId, v2.ColumnId, documentId,
            Sanitise($"v_{code}_S_T_v1"), Sanitise($"v_{code}_S_T_v2"), Sanitise($"v_{code}_S_T"));
    }

    private async Task<(int VersionId, int TableId, int ColumnId)> AddVersionAsync(int templateId, string versionText, WorkflowMode mode)
    {
        await using var db = _sql.CreateContext();
        var version = new TemplateVersion(templateId, versionText, 1, Now);
        version.SetWorkflowMode(mode);   // лише в чернетці — до публікації
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();
        var sheet = new SheetDef(version.Id, EcrCode.Create("S"), Name("S"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        var table = new TableDef(sheet.Id, EcrCode.Create("T"), Name("T"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();
        var column = new ColumnDef(table.Id, EcrCode.Create("A"), Name("A"), 1, CellDataType.String);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync();
        version.Publish(1, Now);
        await db.SaveChangesAsync();
        return (version.Id, table.Id, column.Id);
    }

    private async Task<long> NewDocumentAsync(int versionId, params int[] periods)
    {
        await using var db = _sql.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var project = new Ecr.Domain.Entities.Documents.Project(
            EcrCode.Create("PRJ" + tag), Name(tag), new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31),
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
        return document.Id;
    }

    /// <summary>Рядок <paramref name="rowKey"/> («R&lt;n&gt;») з коміркою в екземплярі таблиці за період (екземпляр — один на документ, період і таблицю).</summary>
    private async Task SeedAsync(long documentId, int periodKey, int tableId, int columnId, string value, string rowKey)
    {
        var loader = new BulkCellLoader(_sql.ConnectionString, 1000);
        var key = new PeriodKey(periodKey);
        long instanceId;
        await using (var db = _sql.CreateContext())
        {
            var existing = await db.TableInstances.AsNoTracking()
                .Where(t => t.DocumentId == documentId && t.TableDefId == tableId && t.PeriodKeyValue == periodKey)
                .Select(t => (long?)t.Id).FirstOrDefaultAsync();
            if (existing is { } id)
            {
                instanceId = id;
            }
            else
            {
                instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
                db.TableInstances.Add(new TableInstance(key, instanceId, documentId, tableId, Now));
                await db.SaveChangesAsync();
            }
        }

        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        await using (var db = _sql.CreateContext())
        {
            db.TableRows.Add(new TableRow(key, rowId, instanceId, RowKey.Create(rowKey),
                int.Parse(rowKey[1..], System.Globalization.CultureInfo.InvariantCulture) + 1, Now));
            await db.SaveChangesAsync();
        }

        await ExecAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString) "
            + $"VALUES ({periodKey}, {rowId}, {columnId}, {tableId}, N'{value}');");
    }

    private async Task<List<(int Period, string? A, byte Mode)>> WideAsync(string view, long documentId)
    {
        var list = new List<(int, string?, byte)>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // імена — з кодів шаблону тесту
        command.CommandText = $"SELECT _PeriodKey, [A], _WorkflowMode FROM rpt.[{view}] WHERE _DocumentId = @d ORDER BY _PeriodKey, _RowOrdinal";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("@d", documentId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetByte(2)));
        }

        return list;
    }

    private async Task<List<(int Period, string Version, string? A, byte Mode)>> StableAsync(string view, long documentId)
    {
        var list = new List<(int, string, string?, byte)>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText =
            $"SELECT _PeriodKey, _TemplateVersion, [A], _WorkflowMode FROM rpt.[{view}] WHERE _DocumentId = @d ORDER BY _PeriodKey, _RowOrdinal";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("@d", documentId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetByte(3)));
        }

        return list;
    }

    private async Task<List<(int Period, string Version, string? Value, byte Status, byte Mode)>> LongAsync(long documentId)
    {
        var list = new List<(int, string, string?, byte, byte)>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT PeriodKey, TemplateVersion, ValueString, Status, _WorkflowMode FROM rpt.v_DocumentCells "
            + "WHERE DocumentId = @d AND ColumnCode = N'A' ORDER BY PeriodKey, RowOrdinal";
        command.Parameters.AddWithValue("@d", documentId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetByte(3), reader.GetByte(4)));
        }

        return list;
    }

    /// <summary>Рівно один рядок: значення, статус подання, режим погодження.</summary>
    private async Task<(string? Value, byte Status, byte Mode)> FreshAsync(string text, long documentId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = text;
#pragma warning restore CA2100
        command.Parameters.AddWithValue("@d", documentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "нової комірки у вʼюсі немає: " + text);
        var row = (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetByte(1), reader.GetByte(2));
        Assert.False(await reader.ReadAsync(), "кілька рядків нової комірки: " + text);
        return row;
    }

    /// <summary>Колонки вʼюхи <c>rpt.&lt;view&gt;</c> за порядком (<c>sys.columns.column_id</c>): ім'я і тип.</summary>
    private async Task<List<(string Name, string Type)>> ColumnsAsync(string view)
    {
        var list = new List<(string, string)>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.name, TYPE_NAME(c.user_type_id) FROM sys.columns AS c "
            + "WHERE c.object_id = OBJECT_ID(N'rpt.' + QUOTENAME(@v), N'V') ORDER BY c.column_id";
        command.Parameters.AddWithValue("@v", view);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetString(0), reader.GetString(1)));
        }

        return list;
    }

    private async Task<string> DefinitionAsync(string view)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(N'rpt.' + QUOTENAME(@v), N'V'))";
        command.Parameters.AddWithValue("@v", view);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> CountAsync(string text)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = text;
#pragma warning restore CA2100
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecAsync(string text)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = text;
#pragma warning restore CA2100
        command.CommandTimeout = 600;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.NextResultAsync())
        {
        }
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Sanitise(string name) => NotIdentifier().Replace(name, "_");

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotIdentifier();
}
