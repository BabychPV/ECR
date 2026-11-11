// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewTemplateLayoutTests.cs
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
/// D-474: маршрутизація місячних колонок у вʼюхах звітності — за версією шаблону. Версія з
/// <c>cfg.TemplateVersion.Layout = 1</c> (YearWithMonthColumns) маршрутизується і при вимкненому прапорі
/// (<c>@MonthRouting = 0</c>); версія з <c>Layout = 0</c> — лише за прапором, як до D-474. Стабільна вʼюха (кілька версій
/// одного шаблону) вирішує ПО ВЕРСІЇ рядка. Відбиток входів (<c>rpt.fn_ViewsFingerprint</c>) включає розкладку версії.
/// </summary>
/// <remarks>
/// Червоне до фіксу (05-rpt-views.sql з 801eb06f9): (1) вʼюхи версії з Layout = 1 при прапорі OFF не маршрутизовані
/// (текст ≠ тексту ON, M11 рядка 202610 = власна копія 1, а не 11); (2) у стабільній вʼюсі рядки версії з Layout = 1 теж
/// не маршрутизовані; (3) відбиток не змінюється від розкладки, і старт пропускає (Skipped) перегенерацію.
/// Значення: рядок R1, 202610 — A = 10, M11 = 1 (власна копія); 202611 — A = 20, M11 = 11. Маршрутизовано: A — з якоря
/// року (202610), M11 — з екземпляра 202611.
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait("Requirement", "ФВ-10.2")]
public sealed class ReportViewTemplateLayoutTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

    private static readonly (int Period, decimal? A, decimal? M11)[] Routed = [(202610, 10m, 11m), (202611, 10m, 11m)];
    private static readonly (int Period, decimal? A, decimal? M11)[] Plain = [(202610, 10m, 1m), (202611, 20m, 11m)];

    private readonly SqlServerFixture _sql = SqlServerFixture.WithOwnDatabase("_layout");

    private sealed record Version(int VersionId, int TableId, Dictionary<string, int> Cols);

    public Task InitializeAsync() => _sql.InitializeAsync();

    public Task DisposeAsync() => _sql.DisposeAsync();

    [Fact]
    public async Task Версія_з_розкладкою_рік_маршрутизується_при_вимкненому_прапорі_так_само_як_при_увімкненому()
    {
        var (templateId, tpl) = await NewTemplateAsync();
        var v1 = await AddVersionAsync(templateId, "1");
        var documentId = await NewDocumentAsync(v1.VersionId);
        await SeedPairAsync(documentId, v1, v1);
        var versioned = Sanitize($"v_{tpl}_S_T_v1");
        var stable = Sanitize($"v_{tpl}_S_T");

        // Прапор ON (як сьогодні): еталонний текст і мапа слотів.
        await GenerateAsync(v1.VersionId, routing: true);
        var onVersioned = await DefinitionAsync(versioned);
        var onStable = await DefinitionAsync(stable);
        var onMap = await MapAsync(versioned);
        Assert.Equal(Routed, await RowsAsync(versioned, documentId));
        Assert.Equal(Routed, await RowsAsync(stable, documentId));

        // Прапор OFF, Layout = 0: як до D-474 - без маршрутизації.
        await GenerateAsync(v1.VersionId, routing: false);
        Assert.NotEqual(onVersioned, await DefinitionAsync(versioned));
        Assert.Equal(Plain, await RowsAsync(versioned, documentId));
        Assert.Equal(Plain, await RowsAsync(stable, documentId));

        // Прапор OFF, Layout = 1: текст і мапа - рівно як при прапорі ON.
        await SetLayoutAsync(v1.VersionId, 1);
        await GenerateAsync(v1.VersionId, routing: false);
        Assert.Equal(onVersioned, await DefinitionAsync(versioned));
        Assert.Equal(onStable, await DefinitionAsync(stable));
        Assert.Equal(onMap, await MapAsync(versioned));
        Assert.Equal(onMap, await MapAsync(stable));
        Assert.Equal(Routed, await RowsAsync(versioned, documentId));
        Assert.Equal(Routed, await RowsAsync(stable, documentId));
    }

    [Fact]
    public async Task Дві_версії_одного_шаблону_маршрутизуються_кожна_за_своєю_розкладкою()
    {
        var (templateId, tpl) = await NewTemplateAsync();
        var v1 = await AddVersionAsync(templateId, "1");   // Layout = 0
        var v2 = await AddVersionAsync(templateId, "2");
        await SetLayoutAsync(v2.VersionId, 1);
        var d1 = await NewDocumentAsync(v1.VersionId);
        await SeedPairAsync(d1, v1, v1);
        var d2 = await NewDocumentAsync(v2.VersionId);
        await SeedPairAsync(d2, v2, v2);
        // Документ, що перейшов на v2 посеред року: рядок v1 (202610) не бере власника з екземпляра v2 (202611).
        var d3 = await NewDocumentAsync(v1.VersionId);
        await SeedPairAsync(d3, v1, v2);

        await GenerateAsync(v2.VersionId, routing: false);
        await GenerateAsync(v1.VersionId, routing: false);
        var stable = Sanitize($"v_{tpl}_S_T");
        var view1 = Sanitize($"v_{tpl}_S_T_v1");
        var view2 = Sanitize($"v_{tpl}_S_T_v2");

        Assert.Equal(Plain, await RowsAsync(view1, d1));
        Assert.Equal(Routed, await RowsAsync(view2, d2));
        Assert.Equal(Plain, await RowsAsync(stable, d1));
        Assert.Equal(Routed, await RowsAsync(stable, d2));
        Assert.Equal(Plain, await RowsAsync(stable, d3));

        // Мапа стабільної вʼюхи: Mo лише в Id колонок версії з Layout = 1.
        var map = await MapAsync(stable);
        Assert.All(map.Where(r => v1.Cols.ContainsValue(r.ColumnDefId)), r => Assert.Null(r.Mo));
        Assert.Contains(map, r => r.ColumnDefId == v2.Cols["M11"] && r.Mo == 11);
        Assert.Contains(map, r => r.ColumnDefId == v2.Cols["A"] && r.Mo == 0);

        // Прапор ON - усі версії маршрутизовані (як до D-474).
        await GenerateAsync(v1.VersionId, routing: true);
        Assert.Equal(Routed, await RowsAsync(view1, d1));
        Assert.Equal(Routed, await RowsAsync(stable, d1));
        Assert.Equal(Routed, await RowsAsync(stable, d2));
    }

    [Fact]
    public async Task Відбиток_враховує_розкладку_версії_і_стабільний_без_змін()
    {
        var (templateId, _) = await NewTemplateAsync();
        var v1 = await AddVersionAsync(templateId, "1");

        var first = await RunAllAsync(onlyIfChanged: true);
        Assert.DoesNotContain("Skipped", first.Template);
        var fp = await FingerprintAsync();
        Assert.Equal(fp, await FingerprintAsync());
        Assert.Equal(["Skipped"], (await RunAllAsync(onlyIfChanged: true)).Template);
        Assert.Equal(["Skipped"], (await RunAllAsync(onlyIfChanged: true)).Stable);

        await SetLayoutAsync(v1.VersionId, 1);
        var changedFp = await FingerprintAsync();
        Assert.NotEqual(fp, changedFp);
        Assert.Equal(changedFp, await FingerprintAsync());
        var changed = await RunAllAsync(onlyIfChanged: true);
        Assert.Contains("Altered", changed.Template);
        Assert.Contains("Altered", changed.Stable);
        var again = await RunAllAsync(onlyIfChanged: true);
        Assert.Equal(["Skipped"], again.Template);
        Assert.Equal(["Skipped"], again.Stable);

        // Назад до Layout = 0 - той самий відбиток, що й на початку.
        await SetLayoutAsync(v1.VersionId, 0);
        Assert.Equal(fp, await FingerprintAsync());
    }

    // ── помічники ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Sanitize(string name) => Regex.Replace(name, "[^A-Za-z0-9_]", "_");

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<(int TemplateId, string Code)> NewTemplateAsync()
    {
        await using var db = _sql.CreateContext();
        var code = "LAY" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var t = new Template(EcrCode.Create(code), Name(code), 1, Now);
        db.Templates.Add(t);
        await db.SaveChangesAsync();
        return (t.Id, code);
    }

    /// <summary>Опублікована версія: аркуш S, таблиця T (MonthsInColumns), колонки A (число, не місячна) і M1..M12.</summary>
    private async Task<Version> AddVersionAsync(int templateId, string versionText)
    {
        await using var db = _sql.CreateContext();
        var version = new TemplateVersion(templateId, versionText, 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();
        var sheet = new SheetDef(version.Id, EcrCode.Create("S"), Name("S"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        var table = new TableDef(sheet.Id, EcrCode.Create("T"), Name("T"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();
        var columns = new List<ColumnDef> { new(table.Id, EcrCode.Create("A"), Name("A"), 1, CellDataType.Decimal) };
        for (var m = 1; m <= 12; m++)
        {
            var column = new ColumnDef(table.Id, EcrCode.Create($"M{m}"), Name($"M{m}"), 1 + m, CellDataType.Decimal);
            column.SetMonth(true, (byte)m);
            columns.Add(column);
        }

        db.ColumnDefs.AddRange(columns);
        await db.SaveChangesAsync();
        version.Publish(1, Now);
        await db.SaveChangesAsync();
        return new Version(version.Id, table.Id, columns.ToDictionary(c => c.Code, c => c.Id, StringComparer.Ordinal));
    }

    /// <summary>Проєкт (місячний, періоди 202610-202611) і документ.</summary>
    private async Task<long> NewDocumentAsync(int versionId)
    {
        await using var db = _sql.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var project = new Ecr.Domain.Entities.Documents.Project(
            EcrCode.Create("PRJ" + tag), Name(tag), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            versionId, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        foreach (var period in new[] { 202610, 202611 })
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

    /// <summary>202610 на таблиці <paramref name="october"/>: A = 10, M11 = 1; 202611 на <paramref name="november"/>: A = 20, M11 = 11.</summary>
    private async Task SeedPairAsync(long documentId, Version october, Version november)
    {
        await SeedAsync(documentId, 202610, october, 10, 1);
        await SeedAsync(documentId, 202611, november, 20, 11);
    }

    private async Task SeedAsync(long documentId, int periodKey, Version table, int a, int m11)
    {
        var loader = new BulkCellLoader(_sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        var key = new PeriodKey(periodKey);
        await using (var db = _sql.CreateContext())
        {
            db.TableInstances.Add(new TableInstance(key, instanceId, documentId, table.TableId, Now));
            db.TableRows.Add(new TableRow(key, rowId, instanceId, RowKey.Create("R1"), 1, Now));
            await db.SaveChangesAsync();
        }

        await ExecuteAsync($"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
            VALUES ({periodKey}, {rowId}, {table.Cols["A"]}, {table.TableId}, {a}),
                   ({periodKey}, {rowId}, {table.Cols["M11"]}, {table.TableId}, {m11})
            """);
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

    private async Task<(List<string> Template, List<string> Stable)> RunAllAsync(bool onlyIfChanged)
        => (await ActionsAsync("EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId = NULL, @MonthRouting = 0, @OnlyIfChanged = @o", 2, onlyIfChanged),
            await ActionsAsync("EXEC rpt.usp_GenerateStableViews @TemplateVersionId = NULL, @MonthRouting = 0, @OnlyIfChanged = @o", 1, onlyIfChanged));

    private async Task<List<string>> ActionsAsync(string text, int actionColumn, bool onlyIfChanged)
    {
        var result = new List<string>();
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = text;
        command.Parameters.AddWithValue("@o", onlyIfChanged);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(actionColumn));
        }

        return result;
    }

    private async Task<string> FingerprintAsync()
        => (string)(await QueryAsync("SELECT rpt.fn_ViewsFingerprint(0)"))[0][0]!;

    private async Task<string> DefinitionAsync(string view)
        => (string)(await QueryAsync($"SELECT OBJECT_DEFINITION(OBJECT_ID(N'rpt.[{view}]', N'V'))"))[0][0]!;

    private async Task<List<(int ColumnDefId, int Slot, int? Mo)>> MapAsync(string view)
        => [.. (await QueryAsync($"SELECT ColumnDefId, Slot, Mo FROM rpt.ViewSlot WHERE ViewName = N'{view}' ORDER BY ColumnDefId, Slot"))
            .Select(r => ((int)r[0]!, (int)r[1]!, (int?)r[2]))];

    private async Task<(int Period, decimal? A, decimal? M11)[]> RowsAsync(string view, long documentId)
        => [.. (await QueryAsync($"SELECT _PeriodKey, [A], [M11] FROM rpt.[{view}] WHERE _DocumentId = {documentId} ORDER BY _PeriodKey"))
            .Select(r => ((int)r[0]!, (decimal?)r[1], (decimal?)r[2]))];

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
