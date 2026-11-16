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
/// (6) відбиток без <c>cfg.ColumnRenameProject</c>; (7) усі пари глобальні; (8) читач <c>rv</c> (результати методики, обидві гілки)
/// без предиката скоупу; (9) підпис скоупу (FOR XML PATH) без пар — усі прив'язані проєкти в одному скоупі; (10) окремий скоуп на
/// кожен проєкт; (11) rpt.ViewProjectScope не пишеться; (12) заміна ключа ViewSlot без транзакції; (13) DROP без перевірки
/// наявності ключа — кожна червонить щонайменше один тест.
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
        for (var p = 0; p < 5; p++)
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

        // Скоупи: PB0 і PB4 - C1 (ОДИН скоуп, Z3), PB1 - C1,C2, PB2 - C1,C2,C3; PB3 - без пар.
        foreach (var (p, pairs) in new[] { (0, 1), (1, 2), (2, 3), (4, 1) })
        {
            for (var i = 1; i <= pairs; i++)
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
        Assert.Equal("C11", await ScalarStringAsync(stable, projects[4].ProjectId, 209901, "N1", "R1"));
        Assert.Null(await ScalarStringAsync(stable, projects[4].ProjectId, 209901, "N2", "R1"));

        // Z3: проєкти з однаковим набором пар - один скоуп: 4 прив'язані проєкти, 3 скоупи (і в мапі слотів - не більше 3).
        var scopes = await ScalarRowsAsync($"SELECT ProjectId, Scope FROM rpt.ViewProjectScope WHERE TemplateId = {templateId}");
        Assert.Equal(4, scopes.Count);
        Assert.Equal(3, scopes.Select(r => r.Scope).Distinct().Count());
        Assert.Equal(scopes.Single(r => r.ProjectId == projects[0].ProjectId).Scope, scopes.Single(r => r.ProjectId == projects[4].ProjectId).Scope);
        var mapScopes = await ScalarRowsAsync($"SELECT 0, COUNT(DISTINCT Scope) FROM rpt.ViewSlot WHERE ViewName = N'{stable}' AND Scope > 0");
        Assert.Equal(3, mapScopes[0].Scope);
    }

    /// <summary>
    /// Z2: 50 проєктів у 2 скоупах (без маршрутизації, з читачем rv): текст вʼюхи не залежить від кількості проєктів, нова прив'язка
    /// проєкту з наявним набором пар не переписує вʼюху (Unchanged) - лише рядок rpt.ViewProjectScope; читання - у межах.
    /// </summary>
    [Fact]
    public async Task Пʼятдесят_проєктів_у_двох_скоупах_текст_вʼюхи_не_росте_і_без_ALTER()
    {
        var (templateId, code) = await NewTemplateAsync();
        var v1 = await AddTypedVersionAsync(templateId, "1", [("T", [("A", CellDataType.String), ("B", CellDataType.String), ("K", CellDataType.Calculated)])]);
        var v2 = await AddTypedVersionAsync(templateId, "2", [("T", [("A", CellDataType.String), ("Z", CellDataType.String), ("KN", CellDataType.Calculated)])]);
        var t1 = v1.Tables["T"];
        var t2 = v2.Tables["T"];
        var projects = new List<ProjectRef>();
        for (var i = 0; i < 51; i++)
        {
            var project = await NewProjectAsync(v1.VersionId, 209901);
            projects.Add(project);
            await SeedAsync(project, 209901, t1, ("A", "m"), ("B", $"b{i}"));
        }

        // Результати методики K - для трьох проєктів (скоуп X, скоуп Y, без пар).
        await SeedResultsAsync(t1.TableId, t1.Cols["K"], "{}", (projects[0], 209901, 1m), (projects[1], 209901, 2m), (projects[50], 209901, 3m));
        await RenameAsync(t1.Cols["B"], t2.Cols["Z"], legacy: false);
        await RenameAsync(t1.Cols["K"], t2.Cols["KN"], legacy: false);
        var stable = Sanitise($"v_{code}_S_T");
        await GenerateAsync(v2.VersionId, routing: false);
        var plainReads = await ReadsAsync(stable);

        // Скоуп X (B -> Z): парні проєкти; скоуп Y (B -> Z, K -> KN): непарні; проєкт 50 - без пар. Спершу 5 проєктів.
        async Task BindAsync(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                await ExecAsync($"INSERT INTO cfg.ColumnRenameProject (FromColumnDefId, ToColumnDefId, ProjectId) VALUES ({t1.Cols["B"]}, {t2.Cols["Z"]}, {projects[i].ProjectId})"
                    + (i % 2 == 1 ? $";INSERT INTO cfg.ColumnRenameProject (FromColumnDefId, ToColumnDefId, ProjectId) VALUES ({t1.Cols["K"]}, {t2.Cols["KN"]}, {projects[i].ProjectId})" : string.Empty));
            }
        }

        await BindAsync(0, 5);
        await GenerateAsync(v2.VersionId, routing: false);
        var five = await DefinitionAsync(stable);

        await BindAsync(5, 50);
        var actions = await StableActionsAsync(v2.VersionId);
        var fifty = await DefinitionAsync(stable);
        var scopedReads = await ReadsAsync(stable);
        output.WriteLine($"B129 Z2: текст вʼюхи 5 проєктів {five.Length}, 50 проєктів {fifty.Length}; логічні читання без прив'язок {plainReads} -> 50 проєктів {scopedReads}");

        Assert.Equal(five, fifty);   // текст не залежить від кількості проєктів
        Assert.Equal("Unchanged", actions.Single(a => a.View == stable).Action);   // без ALTER VIEW
        Assert.True(scopedReads <= plainReads * 2 + 200, $"читання {plainReads} -> {scopedReads}");
        Assert.Equal(2, (await ScalarRowsAsync($"SELECT 0, COUNT(DISTINCT Scope) FROM rpt.ViewProjectScope WHERE TemplateId = {templateId}"))[0].Scope);

        Assert.Equal("b48", await ScalarStringAsync(stable, projects[48].ProjectId, 209901, "Z", "R1"));   // скоуп X (доданий без ALTER)
        Assert.Equal("b49", await ScalarStringAsync(stable, projects[49].ProjectId, 209901, "Z", "R1"));   // скоуп Y
        Assert.Null(await ScalarStringAsync(stable, projects[50].ProjectId, 209901, "Z", "R1"));          // без пар
        Assert.Equal(((decimal?)1m, (decimal?)null), await KAsync(stable, projects[0].ProjectId));        // X: K без KN
        Assert.Equal(((decimal?)2m, (decimal?)2m), await KAsync(stable, projects[1].ProjectId));          // Y: K і KN
        Assert.Equal(((decimal?)3m, (decimal?)null), await KAsync(stable, projects[50].ProjectId));       // без пар
    }

    /// <summary>
    /// Z1: оновлення ключа rpt.ViewSlot (05-rpt-views.sql) - ідемпотентне з будь-якого стану (ключа немає; старий ключ; схема до B129)
    /// і атомарне: збій ADD (DDL-тригер) відкочує DROP - старий ключ на місці, повторний прогін довершує.
    /// </summary>
    [Fact]
    public async Task Оновлення_ключа_ViewSlot_ідемпотентне_з_будь_якого_стану_і_атомарне()
    {
        const string Old = "ALTER TABLE rpt.ViewSlot ADD CONSTRAINT PK_ViewSlot PRIMARY KEY CLUSTERED (ViewName, ColumnDefId, Slot)";
        const string DropPk = "IF OBJECT_ID(N'rpt.PK_ViewSlot', N'PK') IS NOT NULL ALTER TABLE rpt.ViewSlot DROP CONSTRAINT PK_ViewSlot";

        // (а) ключа немає.
        await ExecAsync(DropPk);
        await RunRptScriptAsync();
        Assert.Equal(["ViewName", "ColumnDefId", "Slot", "Scope"], await PkColumnsAsync());

        // (б) старий ключ, колонка Scope є.
        await ExecAsync(DropPk + ";" + Old);
        await RunRptScriptAsync();
        Assert.Equal(["ViewName", "ColumnDefId", "Slot", "Scope"], await PkColumnsAsync());

        // (в) схема до B129: без Scope, старий ключ; (г) повторний прогін - без змін.
        await ExecAsync(DropPk + ";ALTER TABLE rpt.ViewSlot DROP CONSTRAINT DF_ViewSlot_Scope;ALTER TABLE rpt.ViewSlot DROP COLUMN Scope;" + Old);
        await RunRptScriptAsync();
        await RunRptScriptAsync();
        Assert.Equal(["ViewName", "ColumnDefId", "Slot", "Scope"], await PkColumnsAsync());

        // (д) атомарність: ADD падає - DROP відкочено, старий ключ лишився; без тригера повтор довершує.
        await ExecAsync(DropPk + ";" + Old);
        await ExecAsync("""
            CREATE TRIGGER trg_b129_fail_pk ON DATABASE FOR ALTER_TABLE AS
            BEGIN
                IF EVENTDATA().value('(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'nvarchar(max)') LIKE N'%ADD CONSTRAINT PK_ViewSlot%Scope%'
                    THROW 50000, N'b129: simulated ADD failure', 1;
            END
            """);
        try
        {
            await Assert.ThrowsAsync<SqlException>(RunRptScriptAsync);
            Assert.Equal(["ViewName", "ColumnDefId", "Slot"], await PkColumnsAsync());
        }
        finally
        {
            await ExecAsync("IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = N'trg_b129_fail_pk' AND parent_class = 0) DROP TRIGGER trg_b129_fail_pk ON DATABASE");
        }

        await RunRptScriptAsync();
        Assert.Equal(["ViewName", "ColumnDefId", "Slot", "Scope"], await PkColumnsAsync());
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

    /// <summary>
    /// F13: скоуп проєкту змінюється (новий перенос додав пару; прив'язки прибрано; проєкт видалено) — після перегенерації в
    /// rpt.ViewProjectScope рівно актуальний рядок (або жодного), і вʼюха показує аліаси НОВОГО скоупу, а старого - ні.
    /// </summary>
    [Fact]
    public async Task Зміна_скоупу_проєкту_оновлює_і_прибирає_його_рядок_відповідності()
    {
        var (templateId, code) = await NewTemplateAsync();
        var v1 = await AddVersionAsync(templateId, "1", ("T", ["A", "B", "C"]));
        var v2 = await AddVersionAsync(templateId, "2", ("T", ["A", "Z", "W"]));
        var t1 = v1.Tables["T"];
        var t2 = v2.Tables["T"];
        var pb = await NewProjectAsync(v1.VersionId, 209901);
        var pq = await NewProjectAsync(v1.VersionId, 209901);
        var gone = await NewProjectAsync(v1.VersionId);   // без даних: буде видалено
        await SeedAsync(pb, 209901, t1, ("A", "a"), ("B", "pb_b"), ("C", "pb_c"));
        await SeedAsync(pq, 209901, t1, ("A", "a"), ("B", "pq_b"), ("C", "pq_c"));
        await RenameAsync(t1.Cols["B"], t2.Cols["Z"], legacy: false, pb.ProjectId);   // PB: X = {B -> Z}
        await RenameAsync(t1.Cols["C"], t2.Cols["W"], legacy: false, pq.ProjectId);
        await BindAsync(t1.Cols["B"], t2.Cols["Z"], pq.ProjectId);                       // PQ: Y = {B -> Z, C -> W}
        await BindAsync(t1.Cols["B"], t2.Cols["Z"], gone.ProjectId);                     // gone: X
        await GenerateAsync(v2.VersionId, routing: false);
        var stable = Sanitise($"v_{code}_S_T");

        async Task<Dictionary<int, int>> ScopesAsync()
            => (await ScalarRowsAsync($"SELECT ProjectId, Scope FROM rpt.ViewProjectScope WHERE TemplateId = {templateId}"))
                .ToDictionary(r => r.ProjectId, r => r.Scope);

        var before = await ScopesAsync();
        Assert.Equal(3, before.Count);
        Assert.NotEqual(before[pq.ProjectId], before[pb.ProjectId]);
        Assert.Equal("pb_b", await ScalarStringAsync(stable, pb.ProjectId, 209901, "Z", "R1"));
        Assert.Null(await ScalarStringAsync(stable, pb.ProjectId, 209901, "W", "R1"));

        // (1) Новий перенос додав PB пару C -> W: PB тепер у скоупі PQ - один рядок, новий скоуп, аліас W видно.
        await BindAsync(t1.Cols["C"], t2.Cols["W"], pb.ProjectId);
        await GenerateAsync(v2.VersionId, routing: false);
        var moved = await ScopesAsync();
        Assert.Equal(moved[pq.ProjectId], moved[pb.ProjectId]);
        Assert.Equal(1, (await ScalarRowsAsync($"SELECT ProjectId, COUNT(*) FROM rpt.ViewProjectScope WHERE TemplateId = {templateId} AND ProjectId = {pb.ProjectId} GROUP BY ProjectId"))[0].Scope);
        Assert.Equal("pb_c", await ScalarStringAsync(stable, pb.ProjectId, 209901, "W", "R1"));
        Assert.Equal("pb_b", await ScalarStringAsync(stable, pb.ProjectId, 209901, "Z", "R1"));

        // (2) Прив'язки PB прибрано: рядка немає (скоуп 0), аліасів немає.
        await ExecAsync($"DELETE FROM cfg.ColumnRenameProject WHERE ProjectId = {pb.ProjectId}");
        await GenerateAsync(v2.VersionId, routing: false);
        Assert.False((await ScopesAsync()).ContainsKey(pb.ProjectId));
        Assert.Null(await ScalarStringAsync(stable, pb.ProjectId, 209901, "Z", "R1"));
        Assert.Null(await ScalarStringAsync(stable, pb.ProjectId, 209901, "W", "R1"));
        Assert.Equal("pq_c", await ScalarStringAsync(stable, pq.ProjectId, 209901, "W", "R1"));   // PQ не зачеплено

        // (3) Проєкт видалено (прив'язка лишилась): його рядок прибирається.
        Assert.True((await ScopesAsync()).ContainsKey(gone.ProjectId));
        await ExecAsync($"DELETE FROM doc.Document WHERE ProjectId = {gone.ProjectId}; DELETE FROM doc.Project WHERE Id = {gone.ProjectId};");
        await GenerateAsync(v2.VersionId, routing: false);
        var after = await ScopesAsync();
        Assert.False(after.ContainsKey(gone.ProjectId));
        Assert.Equal([pq.ProjectId], after.Keys.ToList());
    }

    /// <summary>
    /// D1: номер скоупу стабільний - за підписом набору пар у порядку появи (1, 2, 3, ...); новий набір не перенумеровує наявні.
    /// </summary>
    [Fact]
    public async Task Номери_скоупів_стабільні_новий_набір_пар_отримує_наступний_номер()
    {
        var (templateId, _) = await NewTemplateAsync();
        var v1 = await AddVersionAsync(templateId, "1", ("T", ["A", "B", "C", "D"]));
        var v2 = await AddVersionAsync(templateId, "2", ("T", ["A", "Z", "W", "V"]));
        var t1 = v1.Tables["T"];
        var t2 = v2.Tables["T"];
        (int From, int To) bz = (t1.Cols["B"], t2.Cols["Z"]), cw = (t1.Cols["C"], t2.Cols["W"]), dv = (t1.Cols["D"], t2.Cols["V"]);
        await RenameAsync(bz.From, bz.To, legacy: false);
        await RenameAsync(cw.From, cw.To, legacy: false);
        await RenameAsync(dv.From, dv.To, legacy: false);

        // П'ять проєктів по черзі, кожен з НОВИМ набором пар: номери 1..5 у порядку появи, наявні не змінюються.
        (int From, int To)[][] sets = [[bz], [bz, cw], [cw], [dv], [bz, dv]];
        var projects = new List<ProjectRef>();
        for (var i = 0; i < sets.Length; i++)
        {
            var project = await NewProjectAsync(v1.VersionId, 209901);
            projects.Add(project);
            foreach (var (from, to) in sets[i])
            {
                await BindAsync(from, to, project.ProjectId);
            }

            await GenerateAsync(v2.VersionId, routing: false);
            var scopes = (await ScalarRowsAsync($"SELECT ProjectId, Scope FROM rpt.ViewProjectScope WHERE TemplateId = {templateId}"))
                .ToDictionary(r => r.ProjectId, r => r.Scope);
            Assert.Equal([.. Enumerable.Range(1, i + 1)], projects.Select(p => scopes[p.ProjectId]).ToList());
        }
    }

    /// <summary>
    /// D1: група, яку виклик пропускає (50422, понад 1000 колонок), лишається зі старим текстом; новий набір пар іншого проєкту
    /// не змінює номера скоупу PB - старий текст і rpt.ViewProjectScope узгоджені: PB бачить свій аліас, PQ - не бачить чужого.
    /// Набори обрано за SHA2 (як підпис процедури): підпис PQ МЕНШИЙ за підпис PB, тож нумерація за підписом (DENSE_RANK) дала б
    /// PQ номер 1 (старий текст: 1 = набір PB) детерміновано, а не з імовірністю 1/2.
    /// </summary>
    [Fact]
    public async Task Пропущена_група_50422_лишається_узгодженою_зі_стабільними_номерами()
    {
        var (templateId, code) = await NewTemplateAsync();
        string[] olds = ["B", "C", "D", "E", "F"], news = ["Z", "W", "V", "U", "X"];
        var filler = Enumerable.Range(1, 984).Select(i => ($"F{i}", CellDataType.String)).ToArray();
        (string, CellDataType)[] v1Cols = [("A", CellDataType.String), .. olds.Select(c => (c, CellDataType.String)), .. filler];
        (string, CellDataType)[] v2Cols = [("A", CellDataType.String), .. news.Select(c => (c, CellDataType.String)), .. filler];
        var v1 = await AddTypedVersionAsync(templateId, "1", [("T", v1Cols)]);
        var v2 = await AddTypedVersionAsync(templateId, "2", [("T", v2Cols)]);
        var t1 = v1.Tables["T"];
        var t2 = v2.Tables["T"];
        var pairs = olds.Select((c, i) => (From: t1.Cols[c], To: t2.Cols[news[i]])).ToArray();
        foreach (var (from, to) in pairs)
        {
            await RenameAsync(from, to, legacy: false);
        }

        // PB: {B -> Z} + будь-яка підмножина решти (найбільший підпис); PQ: непорожня підмножина без B -> Z (найменший підпис).
        var rest = pairs[1..];
        var subsets = Enumerable.Range(0, 1 << rest.Length)
            .Select(mask => rest.Where((_, i) => (mask & (1 << i)) != 0).ToArray()).ToList();
        var pbSet = subsets.Select(s => (IReadOnlyList<(int From, int To)>)[pairs[0], .. s]).MaxBy(s => Convert.ToHexString(Signature(s)))!;
        var pqSet = subsets.Where(s => s.Length > 0).Select(s => (IReadOnlyList<(int From, int To)>)s).MinBy(s => Convert.ToHexString(Signature(s)))!;
        Assert.True(string.CompareOrdinal(Convert.ToHexString(Signature(pqSet)), Convert.ToHexString(Signature(pbSet))) < 0, "передумова: підпис PQ < PB");

        var pb = await NewProjectAsync(v1.VersionId, 209901);
        var pq = await NewProjectAsync(v1.VersionId, 209901);
        await SeedAsync(pb, 209901, t1, [("A", "a"), .. olds.Select(c => (c, "pb_" + c))], "R1");
        await SeedAsync(pq, 209901, t1, [("A", "a"), .. olds.Select(c => (c, "pq_" + c))], "R1");
        foreach (var (from, to) in pbSet)
        {
            await BindAsync(from, to, pb.ProjectId);
        }

        await GenerateAsync(v2.VersionId, routing: false);
        var stable = Sanitise($"v_{code}_S_T");
        var oldText = await DefinitionAsync(stable);
        Assert.Equal("pb_B", await ScalarStringAsync(stable, pb.ProjectId, 209901, "Z", "R1"));
        Assert.Null(await ScalarStringAsync(stable, pq.ProjectId, 209901, "Z", "R1"));

        // v3 додає 10 колонок: група понад 1000 - 50422, вʼюха лишається зі старим текстом. Тим часом PQ - новий набір пар.
        (string, CellDataType)[] v3Cols = [.. v2Cols, .. Enumerable.Range(1, 10).Select(i => ($"G{i}", CellDataType.String))];
        var v3 = await AddTypedVersionAsync(templateId, "3", [("T", v3Cols)]);
        foreach (var (from, to) in pqSet)
        {
            await BindAsync(from, to, pq.ProjectId);
        }

        var failed = await Assert.ThrowsAsync<SqlException>(() => GenerateAsync(v3.VersionId, routing: false));
        Assert.Equal(50422, failed.Number);
        Assert.Equal(oldText, await DefinitionAsync(stable));
        var scopes = (await ScalarRowsAsync($"SELECT ProjectId, Scope FROM rpt.ViewProjectScope WHERE TemplateId = {templateId}"))
            .ToDictionary(r => r.ProjectId, r => r.Scope);
        Assert.Equal(1, scopes[pb.ProjectId]);   // номер PB не змінився
        Assert.Equal(2, scopes[pq.ProjectId]);   // новий набір - наступний номер

        Assert.Equal("pb_B", await ScalarStringAsync(stable, pb.ProjectId, 209901, "Z", "R1"));   // PB - свій аліас
        for (var i = 0; i < olds.Length; i++)
        {
            Assert.Null(await ScalarStringAsync(stable, pq.ProjectId, 209901, news[i], "R1"));     // PQ - жодного чужого аліаса
        }
    }

    /// <summary>
    /// D1, оновлення наявної бази: шаблон з rpt.ViewProjectScope, але без rpt.ViewScopeSig (база до цього коміту; номери могли
    /// бути будь-якими, напр. за DENSE_RANK підпису) - перша перегенерація ЗБЕРІГАЄ номери проєктів, резервує номери без
    /// власника (до найбільшого відомого), а новий набір пар отримує наступний вільний номер.
    /// </summary>
    [Fact]
    public async Task Оновлена_база_зберігає_номери_скоупів_на_першій_перегенерації()
    {
        var (templateId, code) = await NewTemplateAsync();
        var v1 = await AddVersionAsync(templateId, "1", ("T", ["A", "B", "C"]));
        var v2 = await AddVersionAsync(templateId, "2", ("T", ["A", "Z", "W"]));
        var t1 = v1.Tables["T"];
        var t2 = v2.Tables["T"];
        var pb = await NewProjectAsync(v1.VersionId, 209901);
        var pq = await NewProjectAsync(v1.VersionId, 209901);
        var pr = await NewProjectAsync(v1.VersionId, 209901);
        var px = await NewProjectAsync(v1.VersionId);   // без прив'язок: рядок-сирота старої бази
        foreach (var (project, tag) in new[] { (pb, "pb"), (pq, "pq"), (pr, "pr") })
        {
            await SeedAsync(project, 209901, t1, ("A", "a"), ("B", $"{tag}_b"), ("C", $"{tag}_c"));
        }

        await RenameAsync(t1.Cols["B"], t2.Cols["Z"], legacy: false, pb.ProjectId);   // PB: {B -> Z}
        await RenameAsync(t1.Cols["C"], t2.Cols["W"], legacy: false, pq.ProjectId);   // PQ: {C -> W}
        await GenerateAsync(v2.VersionId, routing: false);

        // Стара база: номерів-підписів немає, номери інші (PB 2, PQ 1), сирота PX з номером 4.
        await ExecAsync($"""
            DELETE FROM rpt.ViewScopeSig WHERE TemplateId = {templateId};
            UPDATE rpt.ViewProjectScope SET Scope = 3 - Scope WHERE TemplateId = {templateId};
            INSERT INTO rpt.ViewProjectScope (TemplateId, ProjectId, Scope) VALUES ({templateId}, {px.ProjectId}, 4);
            """);
        await BindAsync(t1.Cols["B"], t2.Cols["Z"], pr.ProjectId);
        await BindAsync(t1.Cols["C"], t2.Cols["W"], pr.ProjectId);                    // PR: новий набір {B -> Z, C -> W}
        await GenerateAsync(v2.VersionId, routing: false);

        var scopes = (await ScalarRowsAsync($"SELECT ProjectId, Scope FROM rpt.ViewProjectScope WHERE TemplateId = {templateId}"))
            .ToDictionary(r => r.ProjectId, r => r.Scope);
        Assert.Equal(new Dictionary<int, int> { [pb.ProjectId] = 2, [pq.ProjectId] = 1, [pr.ProjectId] = 5 }, scopes);
        Assert.Equal([1, 2, 3, 4, 5], (await ScalarRowsAsync($"SELECT 0, Scope FROM rpt.ViewScopeSig WHERE TemplateId = {templateId} ORDER BY Scope"))
            .Select(r => r.Scope).ToList());

        var stable = Sanitise($"v_{code}_S_T");
        Assert.Equal("pb_b", await ScalarStringAsync(stable, pb.ProjectId, 209901, "Z", "R1"));
        Assert.Null(await ScalarStringAsync(stable, pb.ProjectId, 209901, "W", "R1"));
        Assert.Equal("pq_c", await ScalarStringAsync(stable, pq.ProjectId, 209901, "W", "R1"));
        Assert.Null(await ScalarStringAsync(stable, pq.ProjectId, 209901, "Z", "R1"));
        Assert.Equal("pr_b", await ScalarStringAsync(stable, pr.ProjectId, 209901, "Z", "R1"));
        Assert.Equal("pr_c", await ScalarStringAsync(stable, pr.ProjectId, 209901, "W", "R1"));
    }

    /// <summary>
    /// D1, конкурентність: номер скоупу виділяється (MAX + ROW_NUMBER) лише під замком шаблону B126. Поки замок тримає інша
    /// сесія, виклик з версією відмовляє 50423, не записавши ні rpt.ViewScopeSig, ні rpt.ViewProjectScope; після звільнення - пише.
    /// </summary>
    [Fact]
    public async Task Номер_скоупу_виділяється_лише_під_замком_шаблону()
    {
        var (templateId, _) = await NewTemplateAsync();
        var v1 = await AddVersionAsync(templateId, "1", ("T", ["A", "B"]));
        var v2 = await AddVersionAsync(templateId, "2", ("T", ["A", "Z"]));
        var pb = await NewProjectAsync(v1.VersionId, 209901);
        await RenameAsync(v1.Tables["T"].Cols["B"], v2.Tables["T"].Cols["Z"], legacy: false, pb.ProjectId);

        async Task<int> CountAsync(string table)
            => (await ScalarRowsAsync($"SELECT 0, COUNT(*) FROM rpt.{table} WHERE TemplateId = {templateId}"))[0].Scope;

        await using (var holder = await OpenAsync())
        {
            await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync();
            await using (var take = holder.CreateCommand())
            {
                take.Transaction = transaction;
                take.CommandText = $"DECLARE @rc int; EXEC @rc = sys.sp_getapplock @Resource = N'ecr.rpt.stableviews.template.{templateId}', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0; SELECT @rc;";
                Assert.True((int)(await take.ExecuteScalarAsync())! >= 0);
            }

            var busy = await Assert.ThrowsAsync<SqlException>(() => ExecAsync($"EXEC rpt.usp_GenerateStableViews {v2.VersionId}, 0, 0, 200"));
            Assert.Equal(50423, busy.Number);
            Assert.Equal(0, await CountAsync("ViewScopeSig"));
            Assert.Equal(0, await CountAsync("ViewProjectScope"));
            await transaction.RollbackAsync();
        }

        await GenerateAsync(v2.VersionId, routing: false);
        Assert.Equal(1, await CountAsync("ViewScopeSig"));
        Assert.Equal(1, await CountAsync("ViewProjectScope"));
    }

    /// <summary>
    /// r3: маршрутизація місяців - значення перейменованої МІСЯЧНОЇ колонки (січень) у рядках лютого й березня PB приходить ЛИШЕ
    /// від екземпляра-власника (fam, січневий екземпляр v1 свого документа): не з власної копії лютого і не з березневого
    /// екземпляра v1 (той самий Id колонки B, але не власник січня). PA (без прив'язки) значення власника під новим іменем не бачить.
    /// </summary>
    [Fact]
    public async Task Маршрутизація_значення_власника_перейменованої_місячної_колонки_за_скоупом_проєкту()
    {
        var (templateId, code) = await NewTemplateAsync();
        var v1 = await AddVersionAsync(templateId, "1", ("T", ["A", "B"]));
        var v2 = await AddVersionAsync(templateId, "2", ("T", ["A", "Z"]));
        var t1 = v1.Tables["T"];
        var t2 = v2.Tables["T"];
        await ExecAsync($"UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = 1 WHERE Id IN ({t1.Cols["B"]}, {t2.Cols["Z"]})");
        var pb = await NewProjectAsync(v1.VersionId, 209901, 209902, 209903);
        var pa = await NewProjectAsync(v1.VersionId, 209901, 209902, 209903);
        foreach (var (project, tag) in new[] { (pb, "pb"), (pa, "pa") })
        {
            await SeedAsync(project, 209901, t1, ("A", $"{tag}_a1"), ("B", $"{tag}_jan"));         // власник січня - екземпляр v1
            await SeedAsync(project, 209902, t2, ("A", $"{tag}_a2"), ("Z", $"{tag}_feb_copy"));    // лютий (v2): власна копія Z
            await SeedAsync(project, 209903, t1, ("A", $"{tag}_a3"), ("B", $"{tag}_mar"));         // березень (v1): НЕ власник січня
        }

        await RenameAsync(t1.Cols["B"], t2.Cols["Z"], legacy: false, pb.ProjectId);
        await GenerateAsync(v2.VersionId, routing: true);
        var stable = Sanitise($"v_{code}_S_T");
        Assert.Contains("FROM rpt.ViewSlot AS fm", await DefinitionAsync(stable), StringComparison.Ordinal);

        Assert.Equal("pb_jan", await ScalarStringAsync(stable, pb.ProjectId, 209902, "Z", "R1"));
        Assert.Equal("pb_jan", await ScalarStringAsync(stable, pb.ProjectId, 209903, "Z", "R1"));
        Assert.Equal("pb_jan", await ScalarStringAsync(stable, pb.ProjectId, 209903, "B", "R1"));
        Assert.Equal("pa_feb_copy", await ScalarStringAsync(stable, pa.ProjectId, 209902, "Z", "R1"));   // без аліаса: власник Z не має значення
        Assert.Null(await ScalarStringAsync(stable, pa.ProjectId, 209903, "Z", "R1"));
        Assert.Equal("pa_jan", await ScalarStringAsync(stable, pa.ProjectId, 209903, "B", "R1"));
    }

    /// <summary>
    /// PT-2 (пентест 2026-11): код таблиці з переносом рядка (можливий лише прямим записом у базу, в обхід <c>EcrCode</c>) не
    /// виводить текст із коментаря-заголовка стабільної вʼюхи: жоден рядок тіла не починається з підкинутого SQL.
    /// </summary>
    [Fact]
    public async Task Перенос_рядка_в_коді_таблиці_не_виходить_із_коментаря_заголовка()
    {
        var (templateId, code) = await NewTemplateAsync();
        var v1 = await AddVersionAsync(templateId, "1", ("T1", ["A"]));
        await ExecAsync($"UPDATE cfg.TableDef SET Code = N'T1' + NCHAR(13) + NCHAR(10) + N'SELECT 1 AS Injected --' WHERE Id = {v1.Tables["T1"].TableId}");

        await GenerateAsync(v1.VersionId, routing: false);

        var definitions = new List<string>();
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT OBJECT_DEFINITION(v.object_id) FROM sys.views AS v WHERE v.schema_id = SCHEMA_ID(N'rpt') AND v.name LIKE @p";
            command.Parameters.AddWithValue("@p", $"v[_]{code}[_]%");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                definitions.Add(reader.GetString(0));
            }
        }

        var definition = Assert.Single(definitions);
        Assert.Contains("SELECT 1 AS Injected", definition, StringComparison.Ordinal);   // текст лишився - у коментарі
        Assert.DoesNotContain(definition.Split('\r', '\n'), line => line.TrimStart().StartsWith("SELECT 1 AS Injected", StringComparison.Ordinal));
    }

    /// <summary>Підпис набору пар - як у rpt.usp_GenerateStableViews: SHA2_256 nvarchar-рядка «,from>to» за (from, to).</summary>
    private static byte[] Signature(IEnumerable<(int From, int To)> pairs)
        => System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes(
            string.Concat(pairs.OrderBy(p => p.From).ThenBy(p => p.To).Select(p => $",{p.From}>{p.To}"))));

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

    private Task BindAsync(int from, int to, int projectId)
        => ExecAsync($"INSERT INTO cfg.ColumnRenameProject (FromColumnDefId, ToColumnDefId, ProjectId) VALUES ({from}, {to}, {projectId})");

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

    private async Task RunRptScriptAsync()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Persistence", "Sql", "05-rpt-views.sql"));
        await using var connection = await OpenAsync();
        foreach (var batch in Ecr.Infrastructure.Persistence.SqlBatches.Split(script))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<List<string>> PkColumnsAsync()
    {
        var list = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COL_NAME(ic.object_id, ic.column_id) FROM sys.indexes AS i
              JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
             WHERE i.object_id = OBJECT_ID(N'rpt.ViewSlot') AND i.name = N'PK_ViewSlot' ORDER BY ic.key_ordinal
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private async Task<List<(int ProjectId, int Scope)>> ScalarRowsAsync(string text)
    {
        var list = new List<(int, int)>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return list;
    }

    private async Task<List<(string View, string Action)>> StableActionsAsync(int versionId)
    {
        var list = new List<(string, string)>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXEC rpt.usp_GenerateStableViews {versionId}, 0, 0";
        command.CommandTimeout = 600;
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
