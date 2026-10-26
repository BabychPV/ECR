using System.Text.Json;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// P2-A (слово людини 26.10): застосування імпорту книги в колонку місяця k таблиці <c>MonthsInColumns</c> підлягає
/// стану СВОГО періоду k — так само, як PATCH: закритий/поданий/погоджений місяць відхиляє ВСЮ книгу
/// (<c>ECR-ACCS-0403</c>, «все або нічого», нічого не записано), відкритий і майбутній — проходять.
/// </summary>
/// <remarks>
/// ⚠ Усе справжнє, як у <c>ExcelImportApplyWorkbookTests</c> (служба доступу, обробник запису, блокування аркушів);
/// план подається напряму — він і є «перегляд, що застарів», коли місяць закрили/подали між переглядом і
/// застосуванням. Перегляд книги бере ті самі рішення (<c>CanEditSlicesAsync</c>) — їх рівність доводить
/// <c>MonthColumnPeriodStateDecisionTests</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class ExcelImportMonthColumnPeriodStateTests(SqlServerFixture sql) : IDisposable
{
    private const int Oct = 202610;
    private const string Token = "p2a-apply";

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    /// <summary>Колонки: 0 — рядкова, 1 — M3 (Closed), 2 — M9 (Open + Submitted), 3 — M11 (Open), 4 — M12 (Scheduled).</summary>
    private sealed record World(TestDocument Doc, string RowKey, IReadOnlyDictionary<int, string> Codes);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Книга_із_закритим_місяцем_відхиляється_цілком_нічого_не_записано()
    {
        var world = await ArrangeAsync();
        var plan = await PlanAsync(world, (1, 33m), (3, 1m));
        var before = await StateAsync(world);

        await using var db = CreateContext();
        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Importer(db, plan, world.Doc.ProjectId).ApplyAsync(world.Doc.DocumentId, Token, CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", error.ErrorCode);
        Assert.Equal("PeriodClosed", error.Details!["reason"]);
        Assert.Equal(before, await StateAsync(world));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Книга_з_поданим_місяцем_відхиляється_цілком_нічого_не_записано()
    {
        var world = await ArrangeAsync();
        var plan = await PlanAsync(world, (2, 99m), (3, 11m));
        var before = await StateAsync(world);

        await using var db = CreateContext();
        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Importer(db, plan, world.Doc.ProjectId).ApplyAsync(world.Doc.DocumentId, Token, CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", error.ErrorCode);
        Assert.Equal("DocumentSubmitted", error.Details!["reason"]);
        Assert.Equal(before, await StateAsync(world));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Книга_з_відкритим_і_майбутнім_місяцем_застосовується_внесення_наперед_не_ламається()
    {
        var world = await ArrangeAsync();
        var plan = await PlanAsync(world, (3, 11m), (4, 12m));

        await using var db = CreateContext();
        var response = await Importer(db, plan, world.Doc.ProjectId).ApplyAsync(world.Doc.DocumentId, Token, CancellationToken.None);

        Assert.Equal(2, response.AppliedCells);
    }

    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(Oct, columnCount: 5, rowCount: 1, ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
        table.SetLayout(TableLayoutKind.MonthsInColumns);

        var columns = await db.ColumnDefs.Where(c => c.TableDefId == doc.TableDefId).ToDictionaryAsync(c => c.Id);
        byte[] months = [3, 9, 11, 12];
        for (var i = 0; i < months.Length; i++)
        {
            columns[doc.ColumnDefIds[i + 1]].SetMonth(true, months[i]);
        }

        (await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Oct))
            .AdvanceTo(PeriodState.Open, Now);

        foreach (var (month, state) in new (byte, PeriodState)[]
                 {
                     (3, PeriodState.Closed), (9, PeriodState.Open), (11, PeriodState.Open), (12, PeriodState.Scheduled),
                 })
        {
            var period = new Period(
                doc.ProjectId, new PeriodKey((2026 * 100) + month), month,
                new DateOnly(2026, month, 1), new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)));
            period.AdvanceTo(state, Now);
            db.Periods.Add(period);
        }

        var submitted = new ApprovalState(doc.DocumentId, doc.SheetDefId, 202609);
        submitted.Submit(userId: 1, Now);
        db.ApprovalStates.Add(submitted);
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
        await db.SaveChangesAsync();

        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.Id == doc.RowIds[0])
            .Select(r => r.RowKeyValue)
            .SingleAsync();

        return new World(doc, rowKey, columns.ToDictionary(c => c.Key, c => c.Value.Code));
    }

    /// <summary>План: нові значення колонок за індексом (1 — M3, 2 — M9, 3 — M11, 4 — M12) одного рядка.</summary>
    private async Task<string> PlanAsync(World world, params (int Column, decimal Value)[] changes)
    {
        await using var db = CreateContext();
        var rows = await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now))
            .GetRowsBatchAsync([world.Doc.TableInstanceId], new PeriodKey(Oct), CancellationToken.None);

        var diff = new TableDiff(
            world.Doc.TableInstanceId,
            Oct,
            [.. changes.Select(c => new ImportChange(world.RowKey, world.Codes[world.Doc.ColumnDefIds[c.Column]], null, c.Value))],
            [],
            rows[world.Doc.TableInstanceId].ToDictionary(s => s.RowKey, s => s.RowVersion, StringComparer.Ordinal));

        return JsonSerializer.Serialize(new ImportPlan(world.Doc.DocumentId, Oct, [diff]), Options);
    }

    private ExcelImporter Importer(EcrDbContext db, string plan, int projectId)
    {
        var clock = new TestClock(Now);
        var metadata = new MetadataCache(_memory, db);
        var real = new AccessDecisionService(
            db, metadata, new AccessProfileCache(_memory), clock, Substitute.For<ICurrentUser>(), new WorkflowStore(db));

        var profile = new AccessBuilder { UserId = 1 }
            .Grant(ResourceKind.Project, projectId, GrantLevel.Write)
            .Build();

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanReadDocumentAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.ReadScopeAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));
        access.CanEditCellsAsync(
                Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanEditCellsAsync(
                c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<PeriodKey>(2),
                c.ArgAt<IReadOnlyCollection<CellAddress>>(3), c.ArgAt<CancellationToken>(4)));
        access.CanCreateRowsAsync(
                Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanCreateRowsAsync(
                c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<IReadOnlyCollection<string>>(2), c.ArgAt<CancellationToken>(3)));
        access.CanEditCellsBatchAsync(
                Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<CellsAccessRequest>>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanEditCellsBatchAsync(
                c.ArgAt<AccessProfile>(0), c.ArgAt<IReadOnlyCollection<CellsAccessRequest>>(1), c.ArgAt<CancellationToken>(2)));
        access.CanCreateRowsBatchAsync(
                Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyDictionary<long, IReadOnlyCollection<string>>>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanCreateRowsBatchAsync(
                c.ArgAt<AccessProfile>(0), c.ArgAt<IReadOnlyDictionary<long, IReadOnlyCollection<string>>>(1),
                c.ArgAt<CancellationToken>(2)));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(1);

        var previews = Substitute.For<IImportPreviewStore>();
        previews.FindAsync(Token, Arg.Any<CancellationToken>()).Returns(plan);

        var cells = new NormalizedCellStore(db);
        var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), clock);
        var uow = new UnitOfWork(db, clock);
        var gate = new SheetEditGate(db);
        var registries = new RegistryStore(db);
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        var patch = new PatchCellsHandler(
            cells, rows, new DocumentStore(db), new PeriodStore(db), metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            new MethodologyStore(db), registries, new DocumentHeaderStore(db), new AuditWriter(db), new AuditReader(db),
            jobs, uow, user, clock, gate, new UnitCatalog(db));

        return new ExcelImporter(
            metadata, registries, access, user, previews, patch, new ImportDiffBuilder(), cells, rows, uow, jobs, gate);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    /// <summary>Комірки й аудит документа — усе, що могла змінити книга.</summary>
    private async Task<List<string>> StateAsync(World world)
    {
        var lines = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT CONCAT('cell|', c.TableRowId, '|', c.ColumnDefId, '|', c.ValueNumeric) FROM doc.CellValue AS c
              JOIN doc.TableRow AS r ON r.PeriodKey = c.PeriodKey AND r.Id = c.TableRowId
             WHERE c.PeriodKey = {Oct} AND r.TableInstanceId = {world.Doc.TableInstanceId}
            UNION ALL
            SELECT CONCAT('audit|', a.Id) FROM aud.CellChange AS a WHERE a.DocumentId = {world.Doc.DocumentId}
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return [.. lines.Order(StringComparer.Ordinal)];
    }
}
