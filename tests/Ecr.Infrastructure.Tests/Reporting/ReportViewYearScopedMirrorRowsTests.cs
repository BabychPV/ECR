// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewYearScopedMirrorRowsTests.cs
using System.Text.RegularExpressions;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// b14 document-move (D-579, огляд L-7): таблиця «екземпляр на період» версії «рік + місячні колонки» (HSE401 775) —
/// річна, усі значення в якорі року. GET зрізу за 202611 створює лінивий екземпляр із ЗБЕРЕЖЕНИМИ порожніми дзеркалами
/// рядків якоря (B1) — rpt-вʼюхи (версійна _vN і стабільна) не показують їх як дані: рядки й значення ті самі, що до GET.
/// </summary>
/// <remarks>
/// МУТАЦІЯ (локально, 26.11): прибрати фільтр дзеркал у usp_GenerateTemplateViews і usp_GenerateStableViews — червоний
/// тест (після GET у кожній вʼюсі +3 порожні рядки _PeriodKey 202611). Порожній рядок САМОГО якоря лишається у вʼюсі.
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "b14-document-move-L7")]
public sealed class ReportViewYearScopedMirrorRowsTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerFixture _sql = SqlServerFixture.WithOwnDatabase("_ysmirror");

    public Task InitializeAsync() => _sql.InitializeAsync();

    public Task DisposeAsync() => _sql.DisposeAsync();

    [Fact]
    public async Task GET_за_202611_не_додає_у_вʼюхи_порожніх_дзеркал_рядків_якоря()
    {
        var code = "YS" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var (versionId, sheetId, tableId, cols) = await VersionAsync(code);
        var documentId = await DocumentAsync(versionId, sheetId, tableId, cols);
        await GenerateAsync(versionId);
        var versioned = Sanitize($"v_{code}_S_T_v1");
        var stable = Sanitize($"v_{code}_S_T");

        var beforeVersioned = await RowsAsync(versioned, documentId);
        var beforeStable = await RowsAsync(stable, documentId);
        Assert.Equal(3, beforeVersioned.Count);   // R1 (A = 5), R2 (B = x) і порожній рядок якоря R3 — він лишається
        Assert.Equal(beforeVersioned, beforeStable);

        // GET зрізу документа за 202611 (як клієнт): лінивий екземпляр із дзеркалами рядків якоря (RowStore, B1).
        await using (var db = _sql.CreateContext())
        {
            var registry = Substitute.For<ITemplateLayoutRegistry>();
            registry.HasAny.Returns(true);
            registry.IsYearSheet(sheetId).Returns(true);
            registry.IsYearVersion(versionId).Returns(true);
            var router = new MonthColumnRouter(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(), registry);
            var created = await new RowStore(db, new BulkCellLoader(_sql.ConnectionString, 1000), new TestClock(Now), monthRouter: router)
                .EnsureTableInstancesAsync(documentId, new PeriodKey(202611), CancellationToken.None);
            Assert.Equal(1, created);
        }

        Assert.Equal(3, (int)(await QueryAsync(
            $"SELECT COUNT(*) FROM doc.TableRow r JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId WHERE i.DocumentId = {documentId} AND i.PeriodKey = 202611"))[0][0]!);

        Assert.Equal(beforeVersioned, await RowsAsync(versioned, documentId));
        Assert.Equal(beforeStable, await RowsAsync(stable, documentId));
    }

    // ── помічники ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Sanitize(string name) => Regex.Replace(name, "[^A-Za-z0-9_]", "_");

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Опублікована версія з розкладкою «рік» (Layout = 1): аркуш S, таблиця T «екземпляр на період», колонки A, B.</summary>
    private async Task<(int VersionId, int SheetId, int TableId, Dictionary<string, int> Cols)> VersionAsync(string code)
    {
        await using var db = _sql.CreateContext();
        var t = new Template(EcrCode.Create(code), Name(code), 1, Now);
        db.Templates.Add(t);
        await db.SaveChangesAsync();
        var version = new TemplateVersion(t.Id, "1", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();
        var sheet = new SheetDef(version.Id, EcrCode.Create("S"), Name("S"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        var table = new TableDef(sheet.Id, EcrCode.Create("T"), Name("T"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Mixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();
        var columns = new List<ColumnDef>
        {
            new(table.Id, EcrCode.Create("A"), Name("A"), 1, CellDataType.Decimal),
            new(table.Id, EcrCode.Create("B"), Name("B"), 2, CellDataType.String),
        };
        db.ColumnDefs.AddRange(columns);
        await db.SaveChangesAsync();
        version.Publish(1, Now);
        await db.SaveChangesAsync();
        await ExecuteAsync($"UPDATE cfg.TemplateVersion SET Layout = {(int)TemplateLayout.YearWithMonthColumns} WHERE Id = {version.Id}");
        return (version.Id, sheet.Id, table.Id, columns.ToDictionary(c => c.Code, c => c.Id, StringComparer.Ordinal));
    }

    /// <summary>Місячний проєкт 2026 (12 періодів, якір 202601), документ; у якорі R1 (A = 5), R2 (B = x), R3 порожній.</summary>
    private async Task<long> DocumentAsync(int versionId, int sheetId, int tableId, Dictionary<string, int> cols)
    {
        await using var db = _sql.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var project = new Project(
            EcrCode.Create("PRJ" + tag), Name(tag), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            versionId, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        for (var m = 1; m <= 12; m++)
        {
            var key = PeriodKey.Create(2026, m);
            db.Periods.Add(new Period(project.Id, key, (byte)m, new DateOnly(2026, m, 1), new DateOnly(2026, m, DateTime.DaysInMonth(2026, m))));
        }

        var document = new Document(project.Id, "DOC" + tag, 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        db.DocumentSheets.Add(new DocumentSheet(document.Id, sheetId));

        var loader = new BulkCellLoader(_sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 3, CancellationToken.None);
        var anchor = new PeriodKey(202601);
        db.TableInstances.Add(new TableInstance(anchor, instanceId, document.Id, tableId, Now));
        for (var i = 0; i < 3; i++)
        {
            db.TableRows.Add(new TableRow(anchor, rowId + i, instanceId, RowKey.Create($"R{i + 1}"), i + 1, Now));
        }

        await db.SaveChangesAsync();
        await ExecuteAsync($"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, ValueString)
            VALUES (202601, {rowId}, {cols["A"]}, {tableId}, 5, NULL),
                   (202601, {rowId + 1}, {cols["B"]}, {tableId}, NULL, N'x')
            """);
        return document.Id;
    }

    private async Task GenerateAsync(int versionId)
    {
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        foreach (var proc in new[] { "rpt.usp_GenerateTemplateViews", "rpt.usp_GenerateStableViews" })
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 300;
            command.CommandText = $"EXEC {proc} @TemplateVersionId = @v, @MonthRouting = 0";
            command.Parameters.AddWithValue("@v", versionId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // результат процедури не потрібен
            }
        }
    }

    private async Task<List<string>> RowsAsync(string view, long documentId)
        => [.. (await QueryAsync($"SELECT _PeriodKey, _RowKey, [A], [B] FROM rpt.[{view}] WHERE _DocumentId = {documentId} ORDER BY _PeriodKey, _RowKey"))
            .Select(r => string.Join('|', r.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))))];

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
