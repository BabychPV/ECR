using System.Text.Json;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
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
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Q-17.5, сценарій 10: застосування імпорту книги з увімкненим <c>Collection:MonthColumnRouting</c> пише значення
/// колонок з іншим власником у ЕКЗЕМПЛЯР ВЛАСНИКА (місячний m — період m; не-місячний — якір), закритий/поданий місяць
/// відхиляє всю книгу, а блокування періодів книги беруться за зростанням.
/// </summary>
/// <remarks>
/// ⚠ Те саме обладнання, що й <c>ExcelImportMonthColumnPeriodStateTests</c> (справжні служба доступу, обробник, блокування
/// аркушів, план подається напряму), плюс роутер, декоратор сховища комірок, <c>RowStore</c> з роутером.
/// </remarks>
[Collection("SqlServer")]
public sealed class ExcelImportMonthRoutingTests(SqlServerFixture sql) : IDisposable
{
    private const int Oct = 202610;
    private const string Token = "q17-routing-apply";

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    /// <summary>Колонки: 0 — рядкова, 1 — звичайна (PLAIN), 2.. — M1..M12 за номером.</summary>
    private sealed record World(TestDocument Doc, string RowKey, IReadOnlyDictionary<int, string> Codes, TestPeriodInstance November, TestPeriodInstance January);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Книга_пише_M11_в_екземпляр_листопада_а_не_жовтня_і_звичайний_стовпець_в_якір()
    {
        var world = await ArrangeAsync(closedMonths: []);
        var plan = await PlanAsync(world, ("M11", 5m), ("PLAIN", 9m));

        await using var db = CreateContext();
        var response = await Importer(db, plan, world.Doc.ProjectId, new List<(char, int)>()).ApplyAsync(world.Doc.DocumentId, Token, CancellationToken.None);

        Assert.Equal(2, response.AppliedCells);

        var stored = await StoredAsync(world);
        Assert.Contains((202611, 5m, world.Doc.MonthColumnDefIds[10]), stored);
        Assert.Contains((202601, 9m, world.Doc.ColumnDefIds[1]), stored);
        Assert.DoesNotContain(stored, s => s.Period == Oct);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Книга_із_закритим_M3_і_відкритим_M11_відхиляється_цілком_у_жодному_екземплярі_нічого()
    {
        var world = await ArrangeAsync(closedMonths: [3]);
        var plan = await PlanAsync(world, ("M3", 3m), ("M11", 11m));

        await using var db = CreateContext();
        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Importer(db, plan, world.Doc.ProjectId, new List<(char, int)>())
                .ApplyAsync(world.Doc.DocumentId, Token, CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", error.ErrorCode);
        Assert.Empty(await StoredAsync(world));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Обробник_книги_бере_блокування_періодів_за_зростанням()
    {
        var world = await ArrangeAsync(closedMonths: []);
        var seen = new List<(char Kind, int Period)>();

        // Прямо обробник книги (без попередніх блокувань імпортера): період книги 202610, колонки M11 і не-місячна
        // (якір 202601, річні періоди 1..12).
        await using var db = CreateContext();
        var (patch, uow) = Patch(db, world.Doc.ProjectId, seen);

        var rows = await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now))
            .GetRowsBatchAsync([world.Doc.TableInstanceId], new PeriodKey(Oct), CancellationToken.None);
        var version = rows[world.Doc.TableInstanceId].Single(r => r.RowKey == world.RowKey).RowVersion;

        await uow.ExecuteInTransactionAsync(
            async ct =>
            {
                await patch.HandleWorkbookAsync(
                    [new PatchCellsRequest(
                        world.Doc.TableInstanceId, Oct, CellChangeOrigins.Import,
                        [new PatchRow(world.RowKey, version,
                            [new PatchCell(world.Codes[CodeIndex("M11")], 1m), new PatchCell(world.Codes[CodeIndex("PLAIN")], 2m)])])],
                    new List<RecalculationSeed>(),
                    new List<RowWindowChange>(),
                    ct);
            },
            CancellationToken.None);

        Assert.NotEmpty(seen);
        // Перші взяття блокувань за зростанням; повторне спільне блокування вже тримаючи його тією ж транзакцією
        // (`EnsureColumnPeriodsEditableAsync` перечитує стан) нового очікування не створює.
        var collapsed = seen.Select(x => x.Period).Distinct().ToList();

        Assert.True(
            collapsed.SequenceEqual(collapsed.Distinct().Order()),
            $"Порядок замків книги не зростає: {string.Join(" → ", seen.Select(x => $"{x.Kind}{x.Period}"))}");
        Assert.Contains(202601, collapsed);
        Assert.Contains(202611, collapsed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Імпортер_разом_з_обробником_бере_замки_періодів_книги_за_зростанням_без_повторних_очікувань()
    {
        var world = await ArrangeAsync(closedMonths: []);
        var plan = await PlanAsync(world, ("M11", 5m), ("PLAIN", 9m));
        var seen = new List<(char Kind, int Period)>();

        await using var db = CreateContext();
        await Importer(db, plan, world.Doc.ProjectId, seen).ApplyAsync(world.Doc.DocumentId, Token, CancellationToken.None);

        Assert.NotEmpty(seen);

        // Перші взяття строго зростають, усі 12 місяців; наступні виклики обробника — повторне спільне блокування
        // вже тримане тією ж транзакцією.
        var firstAcquisitions = seen.Select(x => x.Period).Distinct().ToList();
        Assert.True(
            firstAcquisitions.SequenceEqual(firstAcquisitions.Order()),
            $"Порядок замків імпортера не зростає: {string.Join(" → ", seen.Select(x => $"{x.Kind}{x.Period}"))}");
        Assert.Equal(12, firstAcquisitions.Count);

        // Імпортер сам бере весь набір ДО обробника: перші 12 викликів — це 12 різних періодів по порядку.
        Assert.Equal(firstAcquisitions, seen.Take(12).Select(x => x.Period).ToList());
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static int CodeIndex(string alias) => alias == "PLAIN" ? 1 : 1 + int.Parse(alias[1..], System.Globalization.CultureInfo.InvariantCulture);

    private async Task<World> ArrangeAsync(int[] closedMonths)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            Oct, columnCount: 2, rowCount: 1, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12,
            ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            for (var m = 1; m <= 12; m++)
            {
                var key = (2026 * 100) + m;
                var period = key == Oct
                    ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Oct)
                    : db.Periods.Add(new Period(
                        doc.ProjectId, new PeriodKey(key), (byte)m,
                        new DateOnly(2026, m, 1), new DateOnly(2026, m, DateTime.DaysInMonth(2026, m)))).Entity;
                period.AdvanceTo(closedMonths.Contains(m) ? PeriodState.Closed : PeriodState.Open, Now);
            }

            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            await db.SaveChangesAsync();
        }

        var january = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202601));
        var november = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202611));
        foreach (var month in closedMonths)
        {
            await builder.AddPeriodInstanceAsync(doc, new PeriodKey((2026 * 100) + month));
        }

        await using var read = builder.CreateContext();
        var columns = await read.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId).ToDictionaryAsync(c => c.Id);
        var rowKey = await read.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync();

        // Індекс коду: 0 — рядкова, 1 — PLAIN, 1+m — M{m}.
        var codes = new Dictionary<int, string>();
        for (var i = 0; i < doc.ColumnDefIds.Count; i++)
        {
            codes[i] = columns[doc.ColumnDefIds[i]].Code;
        }

        return new World(doc, rowKey, codes, november, january);
    }

    /// <summary>План: нові значення за псевдонімами (PLAIN, M{m}) одного рядка в екземплярі перегляду.</summary>
    private async Task<string> PlanAsync(World world, params (string Alias, decimal Value)[] changes)
    {
        await using var db = CreateContext();
        var rows = await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now))
            .GetRowsBatchAsync([world.Doc.TableInstanceId], new PeriodKey(Oct), CancellationToken.None);

        var diff = new TableDiff(
            world.Doc.TableInstanceId,
            Oct,
            [.. changes.Select(c => new ImportChange(world.RowKey, world.Codes[CodeIndex(c.Alias)], null, c.Value))],
            [],
            rows[world.Doc.TableInstanceId].ToDictionary(s => s.RowKey, s => s.RowVersion, StringComparer.Ordinal));

        return JsonSerializer.Serialize(new ImportPlan(world.Doc.DocumentId, Oct, [diff]), Options);
    }

    private (PatchCellsHandler Patch, UnitOfWork Uow) Patch(EcrDbContext db, int projectId, List<(char, int)> seen)
    {
        var clock = new TestClock(Now);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
            .Build();
        var router = new MonthColumnRouter(db, configuration);
        var metadata = new MetadataCache(_memory, db);
        var real = new AccessDecisionService(
            db, metadata, new AccessProfileCache(_memory), clock, Substitute.For<ICurrentUser>(), new WorkflowStore(db), router);

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

        var cells = new MonthRoutedCellStore(new NormalizedCellStore(db), router, metadata, db);
        var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), clock, monthRouter: router);
        var uow = new UnitOfWork(db, clock);
        var gate = new RecordingGate(new SheetEditGate(db), seen);
        var registries = new RegistryStore(db);
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        var patch = new PatchCellsHandler(
            cells, rows, new DocumentStore(db), new PeriodStore(db), metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            new MethodologyStore(db), registries, new DocumentHeaderStore(db), new AuditWriter(db), new AuditReader(db),
            jobs, uow, user, clock, gate, new UnitCatalog(db), monthRouter: router);

        return (patch, uow);
    }

    private ExcelImporter Importer(EcrDbContext db, string plan, int projectId, List<(char, int)> seen)
    {
        var clock = new TestClock(Now);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
            .Build();
        var router = new MonthColumnRouter(db, configuration);
        var metadata = new MetadataCache(_memory, db);
        var (patch, uow) = Patch(db, projectId, seen);

        var profile = new AccessBuilder { UserId = 1 }
            .Grant(ResourceKind.Project, projectId, GrantLevel.Write)
            .Build();
        var real = new AccessDecisionService(
            db, metadata, new AccessProfileCache(_memory), clock, Substitute.For<ICurrentUser>(), new WorkflowStore(db), router);
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

        var cells = new MonthRoutedCellStore(new NormalizedCellStore(db), router, metadata, db);
        var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), clock, monthRouter: router);
        var gate = new RecordingGate(new SheetEditGate(db), seen);
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        return new ExcelImporter(
            metadata, new RegistryStore(db), access, user, previews, patch, new ImportDiffBuilder(), cells, rows, uow, jobs, gate,
            monthRouter: router);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    /// <summary>Фізичні введені комірки таблиці документа: (період, значення, колонка).</summary>
    private async Task<List<(int Period, decimal Value, int ColumnDefId)>> StoredAsync(World world)
    {
        var result = new List<(int, decimal, int)>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT c.PeriodKey, c.ValueNumeric, c.ColumnDefId FROM doc.CellValue AS c
              JOIN doc.TableRow AS r ON r.PeriodKey = c.PeriodKey AND r.Id = c.TableRowId
              JOIN doc.TableInstance AS i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
             WHERE i.DocumentId = {world.Doc.DocumentId} AND i.TableDefId = {world.Doc.TableDefId} AND c.ValueNumeric IS NOT NULL
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetInt32(0), reader.GetDecimal(1), reader.GetInt32(2)));
        }

        return result;
    }

    /// <summary>Шлюз, що пише послідовність блокувань аркушів.</summary>
    private sealed class RecordingGate(ISheetEditGate inner, List<(char, int)> seen) : ISheetEditGate
    {
        public async Task<DocumentStatus> EnterEditAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        {
            seen.Add(('E', periodKey.Value));
            return await inner.EnterEditAsync(documentId, sheetDefId, periodKey, ct);
        }

        public async Task<DocumentStatus> EnterEditNoWaitAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        {
            seen.Add(('N', periodKey.Value));
            return await inner.EnterEditNoWaitAsync(documentId, sheetDefId, periodKey, ct);
        }

        public async Task EnterSubmitAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        {
            seen.Add(('X', periodKey.Value));
            await inner.EnterSubmitAsync(documentId, sheetDefId, periodKey, ct);
        }

        public Task<int?> EnterStructureAsync(long documentId, bool exclusive, CancellationToken ct)
            => inner.EnterStructureAsync(documentId, exclusive, ct);

        public Task EnterHeaderAsync(long documentId, bool exclusive, CancellationToken ct)
            => inner.EnterHeaderAsync(documentId, exclusive, ct);
    }
}
