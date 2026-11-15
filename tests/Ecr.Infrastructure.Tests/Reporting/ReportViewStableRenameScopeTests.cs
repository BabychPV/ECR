// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewStableRenameScopeTests.cs
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// B129 (D-NEW, людина 15.11 — варіант «За проєктом»): стабільна вʼюха <c>rpt.v_&lt;Шаблон&gt;_&lt;Аркуш&gt;_&lt;Таблиця&gt;</c> бере
/// аліаси перейменувань (<c>cfg.ColumnRename</c>) для КОЖНОГО рядка лише за рішенням його проєкту (<c>_ProjectId</c>): пара діє,
/// якщо прив'язана до проєкту рядка (<c>cfg.ColumnRenameProject</c>, B127) або застаріла глобальна (<c>IsLegacyGlobal = 1</c>).
/// Сценарій: v1 (A, B) → v2 (A, Z); проєкт PB переніс B → Z, проєкт PA колонку B відкинув, проєкт PC стартував на v2.
/// Власна база, періоди 2099xx.
/// </summary>
/// <remarks>
/// До B129 (05-rpt-views.sql до цього коміту) червоніють тести колонок (обидва режими), R-2, відбитка і кількох скоупів: аліас
/// діяв на рядки всіх проєктів. Мутації B129 (локально, SQL у bin): (1) <c>@pred</c> з усіма Id без скоупу (pc); (2) мапа слотів
/// без <c>m.Scope</c>; (3) <c>fam</c> без <c>fm.Scope</c>; (4) рядки R-2 без предиката скоупу; (5) <c>IsLegacyGlobal</c> ігнорується;
/// (6) відбиток без <c>cfg.ColumnRenameProject</c>; (7) усі пари глобальні; (8) читач <c>rv</c> (результати методики) без
/// предиката скоупу; (9) підпис скоупу (FOR XML PATH) без пар — усі проєкти з прив'язками в одному скоупі — кожна червонить
/// щонайменше один тест.
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "RPT-STABLE")]
public sealed partial class ReportViewStableRenameScopeTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly DateTime Now = new(2099, 1, 2, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerFixture _sql = SqlServerFixture.WithOwnDatabase("_b129");

    public Task InitializeAsync() => _sql.InitializeAsync();

    public Task DisposeAsync() => _sql.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Кожен_рядок_бере_аліас_лише_за_рішенням_свого_проєкту(bool monthRouting)
    {
        var s = await ArrangeAsync(legacy: false, monthRouting);

        // PB переніс B -> Z: ряд безперервний під обома іменами.
        Assert.Equal("pb_b", await CellAsync(s, s.Pb, 209901, "Z"));
        Assert.Equal("pb_b", await CellAsync(s, s.Pb, 209901, "B"));
        if (!monthRouting)
        {
            Assert.Equal("pb_z", await CellAsync(s, s.Pb, 209902, "B"));
        }

        // PA відкинув B: старі дані B не стають Z, нові дані Z не стають B.
        Assert.Null(await CellAsync(s, s.Pa, 209901, "Z"));
        Assert.Equal("pa_b", await CellAsync(s, s.Pa, 209901, "B"));
        Assert.Equal("pa_z", await CellAsync(s, s.Pa, 209902, "Z"));
        if (!monthRouting)
        {
            Assert.Null(await CellAsync(s, s.Pa, 209902, "B"));
        }

        // PC (лише v2): Z - свій, під B - нічого.
        Assert.Equal("pc_z", await CellAsync(s, s.Pc, 209902, "Z"));
        Assert.Null(await CellAsync(s, s.Pc, 209902, "B"));
    }

    [Fact]
    public async Task Застаріла_глобальна_пара_діє_для_всіх_проєктів()
    {
        var s = await ArrangeAsync(legacy: true, monthRouting: false);

        Assert.Equal("pb_b", await CellAsync(s, s.Pb, 209901, "Z"));
        Assert.Equal("pa_b", await CellAsync(s, s.Pa, 209901, "Z"));
        Assert.Equal("pa_z", await CellAsync(s, s.Pa, 209902, "B"));
        Assert.Equal("pc_z", await CellAsync(s, s.Pc, 209902, "B"));
        Assert.DoesNotContain("d.ProjectId IN", await DefinitionAsync(s.Stable), StringComparison.Ordinal);   // текст як до B129
    }

    [Fact]
    public async Task Перейменування_таблиці_R2_повʼязує_рядки_лише_для_свого_проєкту()
    {
        var (templateId, code) = await NewTemplateAsync();
        var v1 = await AddVersionAsync(templateId, "1", ("T1", ["B"]));
        var v2 = await AddVersionAsync(templateId, "2", ("T2", ["B"]));
        var pb = await NewProjectAsync(v1.VersionId, 209901, 209902);
        var pa = await NewProjectAsync(v1.VersionId, 209901, 209902);
        await SeedAsync(pb, 209901, v1.Tables["T1"], ("B", "pb_t1"));
        await SeedAsync(pb, 209902, v2.Tables["T2"], ("B", "pb_t2"));
        await SeedAsync(pa, 209901, v1.Tables["T1"], ("B", "pa_t1"));
        await SeedAsync(pa, 209902, v2.Tables["T2"], ("B", "pa_t2"));
        await RenameAsync(v1.Tables["T1"].Cols["B"], v2.Tables["T2"].Cols["B"], legacy: false, pb.ProjectId);
        await GenerateAsync(v2.VersionId, routing: false);

        var t1 = Sanitise($"v_{code}_S_T1");
        var t2 = Sanitise($"v_{code}_S_T2");
        Assert.Equal([(209901, "pb_t1"), (209902, "pb_t2")], await SeriesAsync(t1, pb.ProjectId));
        Assert.Equal([(209901, "pb_t1"), (209902, "pb_t2")], await SeriesAsync(t2, pb.ProjectId));
        Assert.Equal([(209901, "pa_t1")], await SeriesAsync(t1, pa.ProjectId));
        Assert.Equal([(209902, "pa_t2")], await SeriesAsync(t2, pa.ProjectId));
    }

    [Fact]
    public async Task Нова_прив_язка_проєкту_змінює_відбиток_і_перегенеровує_вʼюху_на_старті()
    {
        var s = await ArrangeAsync(legacy: false, monthRouting: false);
        Assert.DoesNotContain(await RunAllAsync(), a => a == "Skipped");
        Assert.Contains(await RunAllAsync(), a => a == "Skipped");   // нічого не змінилося

        // PA тепер теж переніс B -> Z (той самий перенос пари) - лише нова прив'язка.
        await ExecAsync($"INSERT INTO cfg.ColumnRenameProject (FromColumnDefId, ToColumnDefId, ProjectId) VALUES ({s.BV1}, {s.ZV2}, {s.Pa})");
        Assert.DoesNotContain(await RunAllAsync(), a => a == "Skipped");
        Assert.Equal("pa_b", await CellAsync(s, s.Pa, 209901, "Z"));
    }

    [Fact]
    public async Task Кілька_скоупів_розмір_тексту_вʼюхи_і_читання_обмежені()
    {
        // 3 проєкти з різними наборами пар (3 скоупи) на 20 колонках проти тієї самої вʼюхи без проєктних прив'язок.
        var (templateId, code) = await NewTemplateAsync();
        string[] v1Cols = [.. Enumerable.Range(1, 20).Select(i => $"C{i}")];
        string[] v2Cols = [.. Enumerable.Range(1, 20).Select(i => i <= 3 ? $"N{i}" : $"C{i}")];
        var v1 = await AddVersionAsync(templateId, "1", ("T", v1Cols));
        var v2 = await AddVersionAsync(templateId, "2", ("T", v2Cols));
        var projects = new List<ProjectRef>();
        for (var p = 0; p < 4; p++)
        {
            var project = await NewProjectAsync(v1.VersionId, 209901, 209902);
            projects.Add(project);
            for (var row = 0; row < 25; row++)
            {
                await SeedAsync(project, 209901, v1.Tables["T"], [.. v1Cols.Select(c => (c, $"{c}{row}"))], $"R{row}");
                await SeedAsync(project, 209902, v2.Tables["T"], [.. v2Cols.Select(c => (c, $"{c}{row}"))], $"R{row}");
            }
        }

        for (var i = 1; i <= 3; i++)
        {
            await RenameAsync(v1.Tables["T"].Cols[$"C{i}"], v2.Tables["T"].Cols[$"N{i}"], legacy: false);
        }

        await GenerateAsync(v2.VersionId, routing: true);
        var stable = Sanitise($"v_{code}_S_T");
        var plain = (await DefinitionAsync(stable)).Length;
        var plainReads = await ReadsAsync(stable);

        // Скоупи: PB0 - C1, PB1 - C1,C2, PB2 - C1,C2,C3; PB3 - без пар.
        for (var p = 0; p < 3; p++)
        {
            for (var i = 1; i <= p + 1; i++)
            {
                await ExecAsync($"INSERT INTO cfg.ColumnRenameProject (FromColumnDefId, ToColumnDefId, ProjectId) VALUES ({v1.Tables["T"].Cols[$"C{i}"]}, {v2.Tables["T"].Cols[$"N{i}"]}, {projects[p].ProjectId})");
            }
        }

        await GenerateAsync(v2.VersionId, routing: true);
        var scoped = (await DefinitionAsync(stable)).Length;
        var scopedReads = await ReadsAsync(stable);
        output.WriteLine($"B129: текст вʼюхи {plain} -> {scoped} символів (+{scoped - plain}); логічні читання {plainReads} -> {scopedReads}");

        Assert.True(scoped - plain < 4000, $"текст виріс на {scoped - plain}");
        Assert.True(scopedReads <= plainReads * 3 / 2 + 50, $"читання {plainReads} -> {scopedReads}");
        Assert.Equal("C31", await ScalarStringAsync(stable, projects[2].ProjectId, 209901, "N3", "R1"));
        Assert.Null(await ScalarStringAsync(stable, projects[0].ProjectId, 209901, "N3", "R1"));
        Assert.Null(await ScalarStringAsync(stable, projects[3].ProjectId, 209901, "N1", "R1"));
    }

    /// <summary>
    /// Обидві гілки читача rv: прив'язка без предиката (<c>{}</c> - rowApply, <c>rvPack</c>) і з предикатом MatchJson за значенням
    /// колонки A рядка (rvApply, <c>rvAgg</c>).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Результат_методики_Calculated_колонки_йде_під_аліасом_лише_для_свого_проєкту(bool matchPredicate)
    {
        // v1: K (Calculated, прив'язка методики) -> v2: KN (Calculated); PB переніс K -> KN, PA - ні.
        var (templateId, code) = await NewTemplateAsync();
        var v1 = await AddTypedVersionAsync(templateId, "1", [("T", [("A", CellDataType.String), ("K", CellDataType.Calculated)])]);
        var v2 = await AddTypedVersionAsync(templateId, "2", [("T", [("A", CellDataType.String), ("KN", CellDataType.Calculated)])]);
        var pb = await NewProjectAsync(v1.VersionId, 209901);
        var pa = await NewProjectAsync(v1.VersionId, 209901);
        await SeedAsync(pb, 209901, v1.Tables["T"], ("A", "m"));
        await SeedAsync(pa, 209901, v1.Tables["T"], ("A", "m"));
        var matchJson = matchPredicate ? $"{{\"{v1.Tables["T"].Cols["A"]}\":\"m\"}}" : "{}";
        await SeedResultsAsync(v1.Tables["T"].TableId, v1.Tables["T"].Cols["K"], matchJson, (pb, 209901, 9.5m), (pa, 209901, 7.5m));
        await RenameAsync(v1.Tables["T"].Cols["K"], v2.Tables["T"].Cols["KN"], legacy: false, pb.ProjectId);
        await GenerateAsync(v2.VersionId, routing: false);

        var stable = Sanitise($"v_{code}_S_T");
        Assert.Equal(matchPredicate, (await DefinitionAsync(stable)).Contains("OPENJSON", StringComparison.Ordinal));   // гілка rvApply
        Assert.Equal(((decimal?)9.5m, (decimal?)9.5m), await KAsync(stable, pb.ProjectId));   // PB: результат під обома іменами
        Assert.Equal(((decimal?)7.5m, (decimal?)null), await KAsync(stable, pa.ProjectId));   // PA: лише під своїм
    }

    // ── помічники ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Scenario(string Stable, int Pb, int Pa, int Pc, int BV1, int ZV2);

    private sealed record VersionRef(int VersionId, Dictionary<string, TableRef> Tables);

    private sealed record TableRef(int TableId, Dictionary<string, int> Cols);

    private sealed record ProjectRef(int ProjectId, long DocumentId);

    /// <summary>v1 (A, B) → v2 (A, Z); PB: пара B → Z (своя або глобальна); PA відкинув B; PC - лише v2.</summary>
    private async Task<Scenario> ArrangeAsync(bool legacy, bool monthRouting)
    {
        var (templateId, code) = await NewTemplateAsync();
        var v1 = await AddVersionAsync(templateId, "1", ("T", ["A", "B"]));
        var v2 = await AddVersionAsync(templateId, "2", ("T", ["A", "Z"]));
        var pb = await NewProjectAsync(v1.VersionId, 209901, 209902);
        var pa = await NewProjectAsync(v1.VersionId, 209901, 209902);
        var pc = await NewProjectAsync(v2.VersionId, 209902);
        var t1 = v1.Tables["T"];
        var t2 = v2.Tables["T"];
        await SeedAsync(pb, 209901, t1, ("A", "pb_a1"), ("B", "pb_b"));
        await SeedAsync(pb, 209902, t2, ("A", "pb_a2"), ("Z", "pb_z"));
        await SeedAsync(pa, 209901, t1, ("A", "pa_a1"), ("B", "pa_b"));
        await SeedAsync(pa, 209902, t2, ("A", "pa_a2"), ("Z", "pa_z"));
        await SeedAsync(pc, 209902, t2, ("A", "pc_a2"), ("Z", "pc_z"));
        await RenameAsync(t1.Cols["B"], t2.Cols["Z"], legacy, legacy ? null : pb.ProjectId);
        await GenerateAsync(v2.VersionId, monthRouting);
        return new Scenario(Sanitise($"v_{code}_S_T"), pb.ProjectId, pa.ProjectId, pc.ProjectId, t1.Cols["B"], t2.Cols["Z"]);
    }

    private async Task<(int TemplateId, string Code)> NewTemplateAsync()
    {
        await using var db = _sql.CreateContext();
        var code = "RS" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var t = new Template(EcrCode.Create(code), Name(code), 1, Now);
        db.Templates.Add(t);
        await db.SaveChangesAsync();
        return (t.Id, code);
    }

    private Task<VersionRef> AddVersionAsync(int templateId, string versionText, params (string Table, string[] Columns)[] tables)
        => AddTypedVersionAsync(templateId, versionText,
            [.. tables.Select(t => (t.Table, t.Columns.Select(c => (c, CellDataType.String)).ToArray()))]);

    private async Task<VersionRef> AddTypedVersionAsync(int templateId, string versionText, (string Table, (string Code, CellDataType Type)[] Columns)[] tables)
    {
        await using var db = _sql.CreateContext();
        var version = new TemplateVersion(templateId, versionText, 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();
        var sheet = new SheetDef(version.Id, EcrCode.Create("S"), Name("S"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        var result = new Dictionary<string, TableRef>(StringComparer.Ordinal);
        var ordinal = 0;
        foreach (var (tableCode, columns) in tables)
        {
            var table = new TableDef(sheet.Id, EcrCode.Create(tableCode), Name(tableCode), ++ordinal,
                TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
            db.TableDefs.Add(table);
            await db.SaveChangesAsync();
            var defs = columns.Select((c, i) => new ColumnDef(table.Id, EcrCode.Create(c.Code), Name(c.Code), i + 1, c.Type)).ToList();
            db.ColumnDefs.AddRange(defs);
            await db.SaveChangesAsync();
            result[tableCode] = new TableRef(table.Id, defs.ToDictionary(d => d.Code, d => d.Id, StringComparer.Ordinal));
        }

        version.Publish(1, Now);
        await db.SaveChangesAsync();
        return new VersionRef(version.Id, result);
    }

    private async Task<ProjectRef> NewProjectAsync(int versionId, params int[] periods)
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
        return new ProjectRef(project.Id, document.Id);
    }

    private Task SeedAsync(ProjectRef project, int periodKey, TableRef table, params (string Code, string Value)[] values)
        => SeedAsync(project, periodKey, table, values, "R1");

    /// <summary>Рядок <paramref name="rowKey"/> в екземплярі таблиці за період (екземпляр - один на документ, період і таблицю).</summary>
    private async Task SeedAsync(ProjectRef project, int periodKey, TableRef table, (string Code, string Value)[] values, string rowKey)
    {
        var loader = new BulkCellLoader(_sql.ConnectionString, 1000);
        var key = new PeriodKey(periodKey);
        long instanceId;
        await using (var db = _sql.CreateContext())
        {
            var existing = await db.TableInstances.AsNoTracking()
                .Where(t => t.DocumentId == project.DocumentId && t.TableDefId == table.TableId && t.PeriodKeyValue == periodKey)
                .Select(t => (long?)t.Id).FirstOrDefaultAsync();
            if (existing is { } id)
            {
                instanceId = id;
            }
            else
            {
                instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
                db.TableInstances.Add(new TableInstance(key, instanceId, project.DocumentId, table.TableId, Now));
                await db.SaveChangesAsync();
            }
        }

        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        await using (var db = _sql.CreateContext())
        {
            db.TableRows.Add(new TableRow(key, rowId, instanceId, RowKey.Create(rowKey), int.Parse(rowKey[1..], System.Globalization.CultureInfo.InvariantCulture) + 1, Now));
            await db.SaveChangesAsync();
        }

        if (values.Length == 0)
        {
            return;
        }

        var text = string.Join("\n", values.Select(v =>
            $"INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString) VALUES ({periodKey}, {rowId}, {table.Cols[v.Code]}, {table.TableId}, N'{v.Value}');"));
        await ExecAsync(text);
    }

    private Task RenameAsync(int from, int to, bool legacy, int? projectId = null)
        => ExecAsync(
            $"INSERT INTO cfg.ColumnRename (FromColumnDefId, ToColumnDefId, IsLegacyGlobal) VALUES ({from}, {to}, {(legacy ? 1 : 0)});"
            + (projectId is { } p ? $"INSERT INTO cfg.ColumnRenameProject (FromColumnDefId, ToColumnDefId, ProjectId) VALUES ({from}, {to}, {p});" : string.Empty));

    private Task GenerateAsync(int versionId, bool routing)
        => ExecAsync($"EXEC rpt.usp_GenerateStableViews {versionId}, {(routing ? 1 : 0)}, 0");

    private async Task<List<string>> RunAllAsync()
    {
        var actions = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC rpt.usp_GenerateStableViews NULL, 0, 1";
        command.CommandTimeout = 600;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(1));
        }

        return actions;
    }

    private async Task<string?> CellAsync(Scenario s, int projectId, int period, string column)
        => await ScalarStringAsync(s.Stable, projectId, period, column, "R1");

    private async Task<string?> ScalarStringAsync(string view, int projectId, int period, string column, string rowKey)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // імена - з кодів шаблону тесту
        command.CommandText = $"SELECT [{column}] FROM rpt.[{view}] WHERE _ProjectId = @p AND _PeriodKey = @k AND _RowKey = @r";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("@p", projectId);
        command.Parameters.AddWithValue("@k", period);
        command.Parameters.AddWithValue("@r", rowKey);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"{view}: немає рядка проєкту {projectId} за {period}");
        var value = reader.IsDBNull(0) ? null : reader.GetString(0);
        Assert.False(await reader.ReadAsync(), $"{view}: кілька рядків проєкту {projectId} за {period}");
        return value;
    }

    private async Task<List<(int Period, string Value)>> SeriesAsync(string view, int projectId)
    {
        var list = new List<(int, string)>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = $"SELECT _PeriodKey, [B] FROM rpt.[{view}] WHERE _ProjectId = @p ORDER BY _PeriodKey";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("@p", projectId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        return list;
    }

    private async Task<(decimal? K, decimal? Kn)> KAsync(string view, int projectId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = $"SELECT [K], [KN] FROM rpt.[{view}] WHERE _ProjectId = @p AND _RowKey = N'R1'";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("@p", projectId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"{view}: немає рядка проєкту {projectId}");
        return (reader.IsDBNull(0) ? null : reader.GetDecimal(0), reader.IsDBNull(1) ? null : reader.GetDecimal(1));
    }

    /// <summary>Методологія з прив'язкою до колонки, актуальний документний прогін і результат R1 на кожен (проєкт, період).</summary>
    private async Task SeedResultsAsync(int tableId, int columnId, string matchJson, params (ProjectRef Project, int Period, decimal Value)[] results)
    {
        int versionId, unitId;
        var runs = new List<long>();
        await using (var db = _sql.CreateContext())
        {
            var methodology = new Methodology(EcrCode.Create("B129" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()), Name("b129"));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
            db.MethodologyVersions.Add(version);
            db.CalculationBindings.Add(new CalculationBinding(tableId, columnId, methodology.Id, "OUT", matchJson));
            await db.SaveChangesAsync();
            versionId = version.Id;
            unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
            foreach (var (project, period, _) in results)
            {
                var run = new CalculationRun(project.ProjectId, period, triggeredByUserId: 1, Now, project.DocumentId);
                db.CalculationRuns.Add(run);
                await db.SaveChangesAsync();
                run.Complete("Completed", Now, profileJson: null, errorMessage: null);
                run.MakeCurrent();
                await db.SaveChangesAsync();
                runs.Add(run.Id);
            }
        }

        for (var i = 0; i < results.Length; i++)
        {
            var (project, period, value) = results[i];
            await ExecAsync(
                "INSERT INTO calc.CalculationResult (PeriodKey, Id, CalculationRunId, MethodologyVersionId, DocumentId, SourceRowKey, OutputCode, Value, UnitId, Kind) "
                + $"VALUES ({period}, NEXT VALUE FOR calc.CalculationResultSeq, {runs[i]}, {versionId}, {project.DocumentId}, N'R1', N'OUT', "
                + $"{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {unitId}, 0);");
        }
    }

    private async Task<string> DefinitionAsync(string view)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(N'rpt.' + QUOTENAME(@v), N'V'))";
        command.Parameters.AddWithValue("@v", view);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Логічні читання повного SELECT вʼюхи (найменше з 3 прогонів після прогріву).</summary>
    private async Task<long> ReadsAsync(string view)
    {
        var best = long.MaxValue;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            long reads = 0;
            await using var connection = new SqlConnection(_sql.ConnectionString);
            connection.InfoMessage += (_, e) =>
            {
                foreach (var m in LogicalReads().Matches(e.Message).Cast<Match>())
                {
                    reads += long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            };
            await connection.OpenAsync();
            await using (var set = connection.CreateCommand())
            {
                set.CommandText = "SET STATISTICS IO ON";
                await set.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
#pragma warning disable CA2100
            command.CommandText = $"SELECT COUNT_BIG(*), MAX(CHECKSUM(*)) FROM rpt.[{view}]";
#pragma warning restore CA2100
            command.CommandTimeout = 300;
            await command.ExecuteScalarAsync();
            if (attempt > 0)
            {
                best = Math.Min(best, reads);
            }
        }

        return best;
    }

    private async Task ExecAsync(string text)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
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

    [GeneratedRegex(@"logical reads (\d+)")]
    private static partial Regex LogicalReads();
}
