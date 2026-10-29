using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Групи заголовків колонок (<c>ECR-TMPL-4229</c>): чисті правила (глибина, цикли, батько, суміжність) і
/// заміна дерева / призначення колонки на реальному SQL Server, у тому числі в опублікованій версії.
/// </summary>
[Collection("SqlServer")]
public sealed class HeaderGroupTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Ecr.Application.Ports.IMetadataCache _cache = Substitute.For<Ecr.Application.Ports.IMetadataCache>();

    // ── чисті правила ────────────────────────────────────────────────────────────────

    private static HeaderColumnPlacement Col(string code, int ordinal, string? group = null) => new(code, ordinal, group);

    private static HeaderGroupNode G(string code, string? parent = null) => new(code, parent);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Коректне_трирівневе_дерево_зі_суміжними_колонками_проходить()
    {
        var result = ColumnHeaderGroupRules.Validate(
            "T",
            [G("Q1"), G("JAN", "Q1"), G("FEB", "Q1")],
            [Col("A", 1), Col("T", 2, "JAN"), Col("MG", 3, "JAN"), Col("T2", 4, "FEB"), Col("Z", 5)]);

        Assert.Empty(result);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Глибина_понад_три_відхиляється()
    {
        var result = ColumnHeaderGroupRules.Validate(
            "T", [G("L1"), G("L2", "L1"), G("L3", "L2"), G("L4", "L3")], [Col("A", 1, "L4")]);

        var violation = Assert.Single(result);
        Assert.Equal("err.ECR-TMPL-4229.depthExceeded", violation.MessageKey);
        Assert.Equal("L4", violation.Args["groupCode"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Цикл_відхиляється()
    {
        var result = ColumnHeaderGroupRules.Validate("T", [G("A", "B"), G("B", "A")], [Col("X", 1)]);

        Assert.Contains(result, v => v.MessageKey == "err.ECR-TMPL-4229.cycle");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Батько_якого_немає_в_таблиці_відхиляється()
    {
        var result = ColumnHeaderGroupRules.Validate("T", [G("A", "OTHER")], [Col("X", 1)]);

        Assert.Equal("err.ECR-TMPL-4229.parentNotFound", Assert.Single(result).MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Дубль_коду_групи_і_невідома_група_колонки_відхиляються()
    {
        var dup = ColumnHeaderGroupRules.Validate("T", [G("A"), G("A")], []);
        Assert.Equal("err.ECR-TMPL-4229.duplicateGroupCode", Assert.Single(dup).MessageKey);

        var unknown = ColumnHeaderGroupRules.Validate("T", [G("A")], [Col("X", 1, "NOPE")]);
        Assert.Equal("err.ECR-TMPL-4229.unknownGroup", Assert.Single(unknown).MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Колонки_групи_що_не_стоять_підряд_відхиляються_а_нащадки_враховуються()
    {
        var gap = ColumnHeaderGroupRules.Validate(
            "T", [G("A")], [Col("X", 1, "A"), Col("Y", 2), Col("Z", 3, "A")]);
        Assert.Equal("err.ECR-TMPL-4229.notContiguous", Assert.Single(gap).MessageKey);

        // Колонка діда між двома колонками внука порушує суміжність ДІДА теж не порушує, а ВНУКА - так.
        var nested = ColumnHeaderGroupRules.Validate(
            "T", [G("P"), G("C", "P")], [Col("X", 1, "C"), Col("Y", 2, "P"), Col("Z", 3, "C")]);
        Assert.Equal("C", Assert.Single(nested).Args["groupCode"]);

        // Прихована колонка між членами групи теж рахується (позиції — за всіма живими колонками).
        var ok = ColumnHeaderGroupRules.Validate(
            "T", [G("P"), G("C", "P")], [Col("X", 1, "C"), Col("Y", 2, "C"), Col("Z", 3, "P")]);
        Assert.Empty(ok);
    }

    // ── заміна дерева на реальній базі ───────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Заміна_дерева_створює_групи_зберігає_Id_за_кодом_і_піднімає_ревізію()
    {
        var v = await VersionAsync();

        var first = await ReplaceAsync(v, [Input("Q1", null, 1, "Q1"), Input("JAN", "Q1", 1, "Jan")]);
        var jan = Assert.Single(first.Groups, g => g.Code == "JAN");
        Assert.Equal(Assert.Single(first.Groups, g => g.Code == "Q1").Id, jan.ParentId);
        Assert.Equal(1, first.PresentationRevision);

        // Повторна заміна з тим самим кодом зберігає Id, міняє підпис.
        var second = await ReplaceAsync(v, [Input("Q1", null, 1, "Q1 renamed"), Input("JAN", "Q1", 1, "Jan")]);
        Assert.Equal(jan.Id, Assert.Single(second.Groups, g => g.Code == "JAN").Id);
        Assert.Equal("Q1 renamed", Assert.Single(second.Groups, g => g.Code == "Q1").LabelL10n.Get("en"));
        Assert.Equal(2, second.PresentationRevision);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Призначення_колонок_групі_в_опублікованій_версії_проходить_а_розрив_відхиляється_і_відкочується()
    {
        var v = await VersionAsync();
        var tree = await ReplaceAsync(v, [Input("G1", null, 1, "G1")]);
        var g1 = tree.Groups.Single().Id;
        await SetStatusAsync(v.VersionId, 1); // Published

        // A і B суміжні - можна, навіть після публікації.
        await PatchAsync(v, $$"""
            [{"entityType":"ColumnDef","entityId":{{v.Columns[0]}},"field":"HeaderGroupId","value":"{{g1}}"},
             {"entityType":"ColumnDef","entityId":{{v.Columns[1]}},"field":"HeaderGroupId","value":"{{g1}}"}]
            """);
        Assert.Equal(g1, await GroupOfAsync(v.Columns[1]));

        // C (третя) - ні: A,B,C суміжні, а от D після пропуску ні. Призначимо D, лишивши C поза групою.
        await using var db = Context();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler(db).PatchAsync(
            v.VersionId,
            $$"""[{"entityType":"ColumnDef","entityId":{{v.Columns[3]}},"field":"HeaderGroupId","value":"{{g1}}"}]""",
            9, CancellationToken.None));

        Assert.Equal("ECR-TMPL-4229", ex.ErrorCode);
        Assert.Equal("err.ECR-TMPL-4229.notContiguous", ex.Details?["messageKey"]);
        Assert.Null(await GroupOfAsync(v.Columns[3]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перестановка_колонок_що_розриває_групу_відхиляється()
    {
        var v = await VersionAsync();
        var g = (await ReplaceAsync(v, [Input("G1", null, 1, "G1")])).Groups.Single().Id;
        await PatchAsync(v, $$"""
            [{"entityType":"ColumnDef","entityId":{{v.Columns[0]}},"field":"HeaderGroupId","value":"{{g}}"},
             {"entityType":"ColumnDef","entityId":{{v.Columns[1]}},"field":"HeaderGroupId","value":"{{g}}"}]
            """);

        // Колонку C (ordinal 3) ставимо між A і B.
        await using var db = Context();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler(db).PatchAsync(
            v.VersionId,
            $$"""[{"entityType":"ColumnDef","entityId":{{v.Columns[2]}},"field":"Ordinal","value":"1"}]""",
            9, CancellationToken.None));
        Assert.Equal("err.ECR-TMPL-4229.notContiguous", ex.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Заміна_дерева_з_циклом_глибиною_і_чужим_батьком_відхиляється_422_без_змін()
    {
        var v = await VersionAsync();

        await AssertRejectedAsync(v, [Input("A", "B", 1, "A"), Input("B", "A", 2, "B")], "err.ECR-TMPL-4229.cycle");
        await AssertRejectedAsync(
            v,
            [Input("L1", null, 1, "1"), Input("L2", "L1", 1, "2"), Input("L3", "L2", 1, "3"), Input("L4", "L3", 1, "4")],
            "err.ECR-TMPL-4229.depthExceeded");
        await AssertRejectedAsync(v, [Input("A", "NOPE", 1, "A")], "err.ECR-TMPL-4229.parentNotFound");

        await using var fresh = Context();
        Assert.Empty(await fresh.ColumnHeaderGroupDefs.AsNoTracking().Where(g => g.TableDefId == v.TableId).ToListAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прибрана_група_звільняє_колонки_а_група_чужої_таблиці_для_колонки_відхиляється()
    {
        var v = await VersionAsync();
        var other = await VersionAsync();
        var foreign = (await ReplaceAsync(other, [Input("F", null, 1, "F")])).Groups.Single().Id;
        var g = (await ReplaceAsync(v, [Input("G1", null, 1, "G1")])).Groups.Single().Id;
        await PatchAsync(v, $$"""[{"entityType":"ColumnDef","entityId":{{v.Columns[0]}},"field":"HeaderGroupId","value":"{{g}}"}]""");

        await using (var db = Context())
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler(db).PatchAsync(
                v.VersionId,
                $$"""[{"entityType":"ColumnDef","entityId":{{v.Columns[1]}},"field":"HeaderGroupId","value":"{{foreign}}"}]""",
                9, CancellationToken.None));
            Assert.Equal("err.ECR-TMPL-4229.unknownGroup", ex.Details?["messageKey"]);
        }

        await ReplaceAsync(v, []);
        Assert.Null(await GroupOfAsync(v.Columns[0]));
    }

    // ── допоміжне ────────────────────────────────────────────────────────────────────

    private static HeaderGroupInputDto Input(string code, string? parent, int ordinal, string en)
        => new(code, parent, ordinal, new Dictionary<string, string> { ["en"] = en });

    private async Task AssertRejectedAsync(Fixture v, IReadOnlyList<HeaderGroupInputDto> input, string key)
    {
        await using var db = Context();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => ReplaceHandler(db).HandleAsync(v.VersionId, v.TableId, input, 9, CancellationToken.None));
        Assert.Equal("ECR-TMPL-4229", ex.ErrorCode);
        Assert.Equal(key, ex.Details?["messageKey"]);
    }

    private async Task<ReplaceHeaderGroupsResult> ReplaceAsync(Fixture v, IReadOnlyList<HeaderGroupInputDto> input)
    {
        await using var db = Context();
        return await ReplaceHandler(db).HandleAsync(v.VersionId, v.TableId, input, 9, CancellationToken.None);
    }

    private async Task PatchAsync(Fixture v, string json)
    {
        await using var db = Context();
        await Handler(db).PatchAsync(v.VersionId, json, 9, CancellationToken.None);
    }

    private void Allow()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());
    }

    private ReplaceHeaderGroupsHandler ReplaceHandler(EcrDbContext db)
    {
        Allow();
        return new ReplaceHeaderGroupsHandler(
            new TemplateVersionStore(db), _cache, new AuditWriter(db), new UnitOfWork(db), new TestClock(Now),
            _access, _user, new Repository<TemplateVersion, int>(db));
    }

    private PatchPresentationHandler Handler(EcrDbContext db)
    {
        Allow();
        return new PatchPresentationHandler(
            new Repository<TemplateVersion, int>(db), new TemplateVersionStore(db), new ChangeClassifier(), _cache,
            new AuditWriter(db), new UnitOfWork(db), new TestClock(Now), _access, _user);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private async Task<int?> GroupOfAsync(int columnId)
    {
        await using var fresh = Context();
        return (await fresh.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == columnId)).HeaderGroupId;
    }

    private async Task SetStatusAsync(int versionId, int status)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE cfg.TemplateVersion SET Status = @s, PublishedAt = SYSUTCDATETIME(), PublishedByUserId = 1 WHERE Id = @id";
        command.Parameters.AddWithValue("@s", status);
        command.Parameters.AddWithValue("@id", versionId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Версія з таблицею і колонками A,B,C,D (ordinal 1..4).</summary>
    private async Task<Fixture> VersionAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"HG{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var columns = new List<ColumnDef>();
        foreach (var (code, i) in ColumnCodes.Select((c, i) => (c, i)))
        {
            var column = new ColumnDef(table.Id, EcrCode.Create(code), Name(code), i + 1, CellDataType.Decimal);
            db.ColumnDefs.Add(column);
            columns.Add(column);
        }

        await db.SaveChangesAsync();
        return new Fixture(version.Id, table.Id, [.. columns.Select(c => c.Id)]);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static readonly string[] ColumnCodes = ["A", "B", "C", "D"];

    private sealed record Fixture(int VersionId, int TableId, IReadOnlyList<int> Columns);
}
