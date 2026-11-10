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
/// Масова правка групи заголовка «Edit selected…» (вкладка Columns, план PERMITES §3): один
/// <c>PATCH …/presentation</c> на всі вибрані колонки з <c>expectedPresentationRevision</c>. Рішення людини 10.11 —
/// <b>все або нічого</b>. На реальному SQL Server: аудит, ревізія і групи читаються з бази окремим контекстом.
/// </summary>
[Collection("SqlServer")]
public sealed class PatchPresentationRevisionTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Ecr.Application.Ports.IMetadataCache _cache = Substitute.For<Ecr.Application.Ports.IMetadataCache>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.2")]
    public async Task Застаріла_ревізія_дає_409_і_нічого_не_змінює()
    {
        var v = await VersionWithGroupAsync();
        var before = await RevisionAsync(v.VersionId);

        await using var db = Context();
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Handler(db, allowed: true).PatchAsync(
            v.VersionId, AssignPatch(v.Group, v.Columns[0], v.Columns[1], v.Columns[2]), 9,
            expectedPresentationRevision: before - 1, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0409.presentationRevisionConflict", ex.Details?["messageKey"]);
        Assert.Equal((before - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Details?["expectedRevision"]);
        Assert.Equal(before.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Details?["actualRevision"]);

        Assert.Equal(before, await RevisionAsync(v.VersionId));
        Assert.All(v.Columns, id => Assert.Null(GroupOf(id)));
        Assert.Empty(await ColumnAuditAsync(v.VersionId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.2")]
    public async Task Три_колонки_одним_патчем_три_рядки_аудиту_і_одна_ревізія_а_none_знімає_групу()
    {
        var v = await VersionWithGroupAsync();
        var before = await RevisionAsync(v.VersionId);

        await using (var db = Context())
        {
            var revision = await Handler(db, allowed: true).PatchAsync(
                v.VersionId, AssignPatch(v.Group, v.Columns[0], v.Columns[1], v.Columns[2]), 9,
                expectedPresentationRevision: before, CancellationToken.None);

            Assert.Equal(before + 1, revision);
        }

        Assert.Equal(before + 1, await RevisionAsync(v.VersionId));
        Assert.Equal([v.Group, v.Group, v.Group, null], v.Columns.Select(GroupOf).ToArray());

        var audit = await ColumnAuditAsync(v.VersionId);
        Assert.Equal(3, audit.Count);
        Assert.Equal(v.Columns.Take(3).Order(), audit.Order());

        // «— none —»: та сама форма, value = null, з новою ревізією.
        await using (var db = Context())
        {
            await Handler(db, allowed: true).PatchAsync(
                v.VersionId, AssignPatch(null, v.Columns[0], v.Columns[1], v.Columns[2]), 9,
                expectedPresentationRevision: before + 1, CancellationToken.None);
        }

        Assert.Equal(before + 2, await RevisionAsync(v.VersionId));
        Assert.All(v.Columns, id => Assert.Null(GroupOf(id)));
        Assert.Equal(6, (await ColumnAuditAsync(v.VersionId)).Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.2")]
    public async Task Розрив_групи_в_пачці_відкочує_всю_пачку()
    {
        var v = await VersionWithGroupAsync();
        var before = await RevisionAsync(v.VersionId);

        // A, B і D — C стоїть між ними без групи: група не підряд.
        await using var db = Context();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler(db, allowed: true).PatchAsync(
            v.VersionId, AssignPatch(v.Group, v.Columns[0], v.Columns[1], v.Columns[3]), 9,
            expectedPresentationRevision: before, CancellationToken.None));

        Assert.Equal("ECR-TMPL-4229", ex.ErrorCode);
        Assert.Equal("err.ECR-TMPL-4229.notContiguous", ex.Details?["messageKey"]);

        // ⛔ Все або нічого: A і B (самі по собі законні) теж не змінились.
        Assert.All(v.Columns, id => Assert.Null(GroupOf(id)));
        Assert.Equal(before, await RevisionAsync(v.VersionId));
        Assert.Empty(await ColumnAuditAsync(v.VersionId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "A7-53")]
    public async Task Без_Template_Edit_відмова_навіть_із_правильною_ревізією()
    {
        var v = await VersionWithGroupAsync();
        var before = await RevisionAsync(v.VersionId);

        await using var db = Context();
        await Assert.ThrowsAsync<AccessDeniedException>(() => Handler(db, allowed: false).PatchAsync(
            v.VersionId, AssignPatch(v.Group, v.Columns[0], v.Columns[1], v.Columns[2]), 9,
            expectedPresentationRevision: before, CancellationToken.None));

        Assert.Equal(before, await RevisionAsync(v.VersionId));
        Assert.All(v.Columns, id => Assert.Null(GroupOf(id)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.2")]
    public async Task Чужа_правка_між_читанням_і_блокуванням_ловиться_під_блокуванням_і_відкочує_все()
    {
        // Ревізію піднімає «інша вкладка» ПІСЛЯ того, як обробник прочитав версію (швидка перевірка пройшла),
        // і ДО блокування рядка версії. Ловить це лише перевірка під блокуванням.
        var v = await VersionWithGroupAsync();
        var before = await RevisionAsync(v.VersionId);

        await using var db = Context();
        var handler = Handler(db, allowed: true, new BumpAfterRead(new Repository<TemplateVersion, int>(db), sql.ConnectionString));
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => handler.PatchAsync(
            v.VersionId, AssignPatch(v.Group, v.Columns[0], v.Columns[1], v.Columns[2]), 9,
            expectedPresentationRevision: before, CancellationToken.None));

        Assert.Equal("err.ECR-TMPL-0409.presentationRevisionConflict", ex.Details?["messageKey"]);
        Assert.Equal((before + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Details?["actualRevision"]);
        Assert.Equal(before + 1, await RevisionAsync(v.VersionId)); // лише чужий інкремент
        Assert.All(v.Columns, id => Assert.Null(GroupOf(id)));
        Assert.Empty(await ColumnAuditAsync(v.VersionId));
    }

    // ── допоміжне ────────────────────────────────────────────────────────────────────

    /// <summary>Читає версію як звичайне сховище, а потім піднімає її ревізію сирим SQL — «чужа вкладка».</summary>
    private sealed class BumpAfterRead(
        Ecr.Application.Ports.IRepository<TemplateVersion, int> inner, string connectionString)
        : Ecr.Application.Ports.IRepository<TemplateVersion, int>
    {
        public Task<TemplateVersion?> FindAsync(int id, CancellationToken ct) => inner.FindAsync(id, ct);

        public async Task<TemplateVersion> GetAsync(int id, CancellationToken ct)
        {
            var version = await inner.GetAsync(id, ct);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE cfg.TemplateVersion SET PresentationRevision = PresentationRevision + 1 WHERE Id = @id;";
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync(ct);
            return version;
        }

        public void Add(TemplateVersion entity) => inner.Add(entity);

        public void Remove(TemplateVersion entity) => inner.Remove(entity);
    }

    private static string AssignPatch(int? group, params int[] columns)
        => "[" + string.Join(",", columns.Select(id =>
            $$"""{"entityType":"ColumnDef","entityId":{{id}},"field":"HeaderGroupId","value":{{(group is { } g ? $"\"{g}\"" : "null")}}}""")) + "]";

    private PatchPresentationHandler Handler(
        EcrDbContext db, bool allowed, Ecr.Application.Ports.IRepository<TemplateVersion, int>? versions = null)
    {
        _user.UserId.Returns(9);
        var profile = new AccessBuilder { UserId = 9 };
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns((allowed ? profile.Permission("Template.Edit") : profile.Permission("Template.View")).Build());

        return new PatchPresentationHandler(
            versions ?? new Repository<TemplateVersion, int>(db), new TemplateVersionStore(db), new ChangeClassifier(), _cache,
            new AuditWriter(db), new UnitOfWork(db), new TestClock(Now), _access, _user);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private async Task<int> RevisionAsync(int versionId)
    {
        await using var fresh = Context();
        return (await fresh.TemplateVersions.AsNoTracking().SingleAsync(x => x.Id == versionId)).PresentationRevision;
    }

    private int? GroupOf(int columnId)
    {
        using var fresh = Context();
        return fresh.ColumnDefs.AsNoTracking().Single(c => c.Id == columnId).HeaderGroupId;
    }

    /// <summary>EntityId рядків <c>aud.StructureChange</c> про колонки цієї версії (групи пишуть свої рядки окремо).</summary>
    private async Task<List<int>> ColumnAuditAsync(int versionId)
    {
        var rows = new List<int>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EntityId FROM aud.StructureChange WHERE TemplateVersionId = @v AND EntityType = N'ColumnDef';";
        command.Parameters.AddWithValue("@v", versionId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetInt32(0));
        }

        return rows;
    }

    /// <summary>Версія з колонками A,B,C,D (ordinal 1..4) і однією групою заголовка G1 (ревізія вже 1).</summary>
    private async Task<Fixture> VersionWithGroupAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        int versionId, tableId;
        var columns = new List<ColumnDef>();

        await using (var db = Context())
        {
            var template = new Template(EcrCode.Create($"PB{tag}"), Name($"Template {tag}"), 1, Now);
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

            foreach (var (code, i) in ColumnCodes.Select((c, i) => (c, i)))
            {
                var column = new ColumnDef(table.Id, EcrCode.Create(code), Name(code), i + 1, CellDataType.Decimal);
                db.ColumnDefs.Add(column);
                columns.Add(column);
            }

            await db.SaveChangesAsync();
            versionId = version.Id;
            tableId = table.Id;
        }

        await using (var db = Context())
        {
            Handler(db, allowed: true);
            var groups = await new ReplaceHeaderGroupsHandler(
                    new TemplateVersionStore(db), _cache, new AuditWriter(db), new UnitOfWork(db), new TestClock(Now),
                    _access, _user, new Repository<TemplateVersion, int>(db))
                .HandleAsync(
                    versionId, tableId,
                    [new HeaderGroupInputDto("G1", null, 1, new Dictionary<string, string> { ["en"] = "January" })],
                    9, CancellationToken.None);

            return new Fixture(versionId, groups.Groups.Single().Id, [.. columns.Select(c => c.Id)]);
        }
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static readonly string[] ColumnCodes = ["A", "B", "C", "D"];

    private sealed record Fixture(int VersionId, int Group, IReadOnlyList<int> Columns);
}
