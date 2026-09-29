using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
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

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// P8, застосування імпорту 2/3: книжковий шлях запису
/// (<see cref="PatchCellsHandler.HandleWorkbookAsync"/>) дає той самий
/// результат, що й послідовність поштучних <see cref="PatchCellsHandler.HandleAsync"/>
/// в одній транзакції (так застосовує книгу <c>ExcelImporter</c>), відмовляє
/// тими самими кодами з номером винного екземпляра і відкочує все, а коштує
/// сталу кількість звернень до бази незалежно від кількості таблиць.
/// </summary>
/// <remarks>
/// ⚠ Усе справжнє, крім профілю прав (готовий, з <see cref="AccessBuilder"/>) і
/// планувальника задач: сховища, <c>AccessDecisionService</c>, кеш метаданих,
/// блокування аркушів, одиниця роботи. Порівнюються ДВА однакові світи;
/// ідентифікатори в них різні, тому стан зводиться до індексів.
/// </remarks>
[Collection("SqlServer")]
public sealed class PatchCellsWorkbookTests(SqlServerFixture sql) : IDisposable
{
    /// <summary>Період — не 2026-05…07 (їх архівують <c>ArchiveJobTests</c>).</summary>
    private const int PeriodKeyValue = 202610;

    private const string NewRowKey = "NEW_1";

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Книжковий_шлях_на_5_таблицях_дає_те_саме_що_поштучні_виклики()
    {
        var bookWorld = await ArrangeAsync([2, 2]);
        var singleWorld = await ArrangeAsync([2, 2]);
        Assert.Equal(5, bookWorld.Tables.Count);

        var bookRequests = Requests(bookWorld, await VersionsAsync(bookWorld));
        var singleRequests = Requests(singleWorld, await VersionsAsync(singleWorld));
        var bookBefore = await StateAsync(bookWorld);
        var singleBefore = await StateAsync(singleWorld);
        Assert.Equal(singleBefore, bookBefore);

        var book = await RunWorkbookAsync(bookWorld, Writer(bookWorld), bookRequests);
        var single = await RunSequentialAsync(singleWorld, Writer(singleWorld), singleRequests);

        var bookState = await StateAsync(bookWorld);
        Assert.Equal(await StateAsync(singleWorld), bookState);
        Assert.Equal(await AuditAsync(singleWorld), await AuditAsync(bookWorld));
        Assert.Equal(await DocumentTouchAsync(singleWorld), await DocumentTouchAsync(bookWorld));
        Assert.Equal(await ResponsesAsync(singleWorld, single.Responses), await ResponsesAsync(bookWorld, book.Responses));
        Assert.Equal(Seeds(singleWorld, single.Seeds), Seeds(bookWorld, book.Seeds));

        // ⛔ Проти хибнозеленого: запис справді відбувся в кожній таблиці.
        Assert.NotEqual(bookBefore, bookState);
        Assert.Contains(bookState, line => line.StartsWith($"0|{NewRowKey}|", StringComparison.Ordinal));
        Assert.Contains(bookState, line => line.Contains("|Київ|", StringComparison.Ordinal));
        for (var ti = 0; ti < bookWorld.Tables.Count; ti++)
        {
            Assert.Contains($"{ti}|r2|c{bookWorld.Tables[ti].ColumnIds.Count - 1}|∅|{200 + ti}|False|False", bookState);
        }

        var audit = await AuditAsync(bookWorld);
        Assert.Equal(bookWorld.Tables.Count, audit.Select(a => a[..a.IndexOf('|', StringComparison.Ordinal)]).Distinct().Count());
        Assert.Equal(Touched, await DocumentTouchAsync(bookWorld));
        Assert.All(book.Responses, r => Assert.True(r.AppliedCells > 0));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Заборона_на_четверту_таблицю_відхиляє_книгу_тією_самою_відмовою_з_номером_екземпляра()
    {
        var world = await ArrangeAsync([2, 2]);
        var guilty = world.Tables[3];
        var profile = new AccessBuilder { UserId = 1 }
            .Grant(ResourceKind.Project, world.Doc.ProjectId, GrantLevel.Write)
            .Deny(ResourceKind.Table, guilty.TableDefId)
            .Build();
        var requests = Requests(world, await VersionsAsync(world));

        await AssertRejectedAsync<AccessDeniedException>(
            world, profile, requests, guilty, "ECR-ACCS-0403", "err.ECR-ACCS-0403.deniedCells");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Застаріла_версія_третьої_таблиці_відхиляє_книгу_з_номером_екземпляра()
    {
        var world = await ArrangeAsync([2, 2]);
        var requests = Requests(world, await VersionsAsync(world));
        await BumpAsync(world.Tables[2].RowIds[0]);

        await AssertRejectedAsync<ConcurrencyConflictException>(
            world, Writer(world), requests, world.Tables[2], "ECR-CELL-0409", "err.ECR-CELL-0409.batchStale");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Конфлікт_версії_зі_сховища_називає_винний_екземпляр_і_рядок()
    {
        var world = await ArrangeAsync([2, 2]);
        var guilty = world.Tables[3];
        var requests = Requests(world, await VersionsAsync(world));
        var before = await SnapshotAsync(world);

        // Чужий запис МІЖ усіма перевірками й записом — у транзакції книги.
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => RunWorkbookAsync(
            world, Writer(world), requests,
            cells: inner => new BumpingCellStore(inner, () => BumpAsync(guilty.RowIds[0]))));

        Assert.Equal("ECR-CELL-0409", error.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.batchStale", error.Details!["messageKey"]);
        Assert.Equal(guilty.InstanceId.ToString(CultureInfo.InvariantCulture), error.Details["tableInstanceId"]);
        var conflict = Assert.Single((IEnumerable<CellConflictDto>)error.Details["conflicts"]!);
        Assert.Equal(guilty.RowKeys[0], conflict.RowKey);

        await AssertNothingWrittenAsync(world, before, bumpedRowKey: guilty.RowKeys[0]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Lookup_на_запис_чужого_довідника_відхиляє_книгу_з_номером_екземпляра()
    {
        var world = await ArrangeAsync([2, 2]);
        var guilty = world.Tables[4];
        var foreignEntryId = await LookupToForeignRegistryAsync(world, guilty.ColumnIds[0]);

        var requests = Requests(world, await VersionsAsync(world));
        var guiltyIndex = requests.FindIndex(r => r.TableInstanceId == guilty.InstanceId);
        var row = requests[guiltyIndex].Rows[0];
        requests[guiltyIndex] = requests[guiltyIndex] with
        {
            Rows = [row with { Cells = [.. row.Cells, new PatchCell(guilty.ColumnCodes[0], foreignEntryId)] }, .. requests[guiltyIndex].Rows.Skip(1)],
        };

        await AssertRejectedAsync<BusinessRuleException>(
            world, Writer(world), requests, guilty, "ECR-CELL-4223", "err.ECR-CELL-4223.foreignRegistry");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Стеля_рядків_відхиляє_книгу_з_номером_екземпляра()
    {
        var world = await ArrangeAsync([2, 2], maxDynamicRows: 3);

        // Таблиця зі стелею — остання в книзі, не перша.
        var requests = Requests(world, await VersionsAsync(world));
        requests.Add(requests[0]);
        requests.RemoveAt(0);

        await AssertRejectedAsync<BusinessRuleException>(
            world, Writer(world), requests, world.Tables[0], "ECR-ROW-0409", "err.ECR-ROW-0409.dynamicRowLimit");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Стеля_рядків_під_винятковим_замком_C3b_ловить_рядок_що_зявився_після_читання()
    {
        // 3 рядки + 1 новий = 4 = стеля: швидкий шлях пропускає.
        var world = await ArrangeAsync([2, 2], maxDynamicRows: 4);
        var baseTable = world.Tables[0];
        var requests = Requests(world, await VersionsAsync(world));
        requests.Add(requests[0]);
        requests.RemoveAt(0);

        var before = await StateAsync(world);
        var exclusive = 0;

        // Сусід додає рядок у ту саму таблицю, поки книга чекає на виняткове блокування.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => RunWorkbookAsync(
            world, Writer(world), requests,
            gate: inner => new InterceptingGate(inner, async () =>
            {
                exclusive++;
                await using var db = world.Builder.CreateContext();
                await Rows(db).CreateRowsAsync(
                    baseTable.InstanceId, Period, [RowKey.Create("RIVAL")], ordinal: 0, CancellationToken.None);
            })));

        Assert.Equal(1, exclusive);
        Assert.Equal("err.ECR-ROW-0409.dynamicRowLimit", error.Details!["messageKey"]);
        Assert.Equal("4", error.Details["existing"]);
        Assert.Equal(baseTable.InstanceId.ToString(CultureInfo.InvariantCulture), error.Details["tableInstanceId"]);

        var after = await StateAsync(world);
        Assert.Contains(after, line => line.StartsWith("0|RIVAL|", StringComparison.Ordinal));
        Assert.DoesNotContain(after, line => line.Contains(NewRowKey, StringComparison.Ordinal));
        Assert.Equal(before, after.Where(line => !line.StartsWith("0|RIVAL|", StringComparison.Ordinal)).ToList());
        Assert.Empty(await AuditAsync(world));
    }

    /// <summary>
    /// Храповик: книга на 3 і на 12 таблиць (один аркуш) коштує ОДНАКОВУ
    /// кількість звернень до бази; поштучно — росте з кількістю таблиць.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Книжковий_шлях_коштує_сталу_кількість_звернень_на_3_і_12_таблиць()
    {
        var world = await ArrangeAsync([12], includeBase: false, mixedExtras: true);
        Assert.Equal(12, world.Tables.Count);
        var requests = Requests(world, await VersionsAsync(world), newRowsEverywhere: true);
        var profile = Writer(world);

        await using var db = world.Builder.CreateContext();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        connection.StatisticsEnabled = true;
        var handler = Handler(db, profile);

        // ⚠ SqlClient рахує ВСІ виконані команди з'єднання — і EF, і сирі
        // SqlCommand сховищ (MERGE, аудит, applock), яких не бачить інтерсептор EF.
        long Executions()
        {
            var stats = connection.RetrieveStatistics();
            return Convert.ToInt64(stats["UnpreparedExecs"], CultureInfo.InvariantCulture)
                   + Convert.ToInt64(stats["PreparedExecs"], CultureInfo.InvariantCulture);
        }

        async Task<long> Measure(Func<Task> write)
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            connection.ResetStatistics();
            await write();
            var executions = Executions();
            await tx.RollbackAsync();
            return executions;
        }

        Task Book(int count)
            => handler.HandleWorkbookAsync([.. requests.Take(count)], new List<RecalculationSeed>(), CancellationToken.None);

        // Прогрів кешів процесу (знімок метаданих, довідник одиниць).
        await Measure(() => Book(12));

        var three = await Measure(() => Book(3));
        var twelve = await Measure(() => Book(12));
        var sequential = await Measure(async () =>
        {
            foreach (var request in requests)
            {
                await handler.HandleAsync(request, CancellationToken.None);
            }
        });

        Assert.True(three == twelve, $"книга: 3 → {three}; 12 → {twelve}; поштучно 12 → {sequential}");
        Assert.Equal(BookExecutions, twelve);
        // Поштучно на цьому світі — 312 (26 на таблицю).
        Assert.True(sequential >= 10 * twelve, $"поштучно 12 → {sequential}, книга → {twelve}");
    }

    /// <summary>
    /// Звернень на книгу одного аркуша з оновленнями й новими рядками в кожній
    /// таблиці (див. храповик). Кожен наступний аркуш додає два (applock + стан).
    /// </summary>
    private const long BookExecutions = 26;

    private async Task AssertRejectedAsync<TException>(
        World world, AccessProfile profile, List<PatchCellsRequest> requests, Table guilty, string code, string messageKey)
        where TException : EcrException
    {
        var before = await SnapshotAsync(world);

        var error = await Assert.ThrowsAsync<TException>(() => RunWorkbookAsync(world, profile, requests));

        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(messageKey, error.Details!["messageKey"]);
        Assert.Equal(guilty.InstanceId.ToString(CultureInfo.InvariantCulture), error.Details["tableInstanceId"]);

        // DAT-05: нічого з жодної таблиці.
        await AssertNothingWrittenAsync(world, before, bumpedRowKey: null);

        // Та сама відмова, що дав би поштучний PATCH винного екземпляра.
        var alone = await Assert.ThrowsAsync<TException>(() => RunSequentialAsync(
            world, profile, [requests.Single(r => r.TableInstanceId == guilty.InstanceId)]));
        Assert.Equal(alone.ErrorCode, error.ErrorCode);
        Assert.Equal(alone.Message, error.Message);
        Assert.False(alone.Details!.ContainsKey("tableInstanceId"));
        Assert.Equal(
            alone.Details.Keys.Order(StringComparer.Ordinal),
            error.Details.Keys.Where(k => k != "tableInstanceId").Order(StringComparer.Ordinal));
        foreach (var (key, value) in alone.Details.Where(p => p.Value is string))
        {
            Assert.Equal(value, error.Details[key]);
        }

        await AssertNothingWrittenAsync(world, before, bumpedRowKey: null);
    }

    private async Task AssertNothingWrittenAsync(
        World world, (List<string> Cells, Dictionary<long, Dictionary<string, string>> Versions) before, string? bumpedRowKey)
    {
        Assert.Equal(before.Cells, await StateAsync(world));

        // Жодна версія не піднялася — крім рядка, який змінив «чужий» запис.
        var after = await VersionsAsync(world);
        foreach (var (instanceId, versions) in before.Versions)
        {
            foreach (var (rowKey, version) in versions.Where(v => v.Key != bumpedRowKey))
            {
                Assert.Equal(version, after[instanceId][rowKey]);
            }
        }

        Assert.Empty(await AuditAsync(world));
        Assert.NotEqual(Touched, await DocumentTouchAsync(world));
    }

    private async Task<(List<string> Cells, Dictionary<long, Dictionary<string, string>> Versions)> SnapshotAsync(World world)
        => (await StateAsync(world), await VersionsAsync(world));

    private static readonly (DateTime? At, int? By) Touched = (Now, 1);

    // ── Прогони ──────────────────────────────────────────────────────────────

    private sealed record Run(IReadOnlyList<PatchCellsResponse> Responses, List<RecalculationSeed> Seeds);

    /// <summary>
    /// Книга так, як її застосує <c>ExcelImporter</c>: одна транзакція, першою
    /// дією — спільні блокування всіх аркушів, далі — книжковий шлях (повторне
    /// спільне блокування того самого власника видається одразу).
    /// </summary>
    private async Task<Run> RunWorkbookAsync(
        World world,
        AccessProfile profile,
        List<PatchCellsRequest> requests,
        Func<ICellStore, ICellStore>? cells = null,
        Func<ISheetEditGate, ISheetEditGate>? gate = null)
    {
        await using var db = world.Builder.CreateContext();
        var handler = Handler(db, profile, cells, gate);
        var seeds = new List<RecalculationSeed>();
        IReadOnlyList<PatchCellsResponse> responses = [];

        await new UnitOfWork(db).ExecuteInTransactionAsync(
            async ct =>
            {
                foreach (var sheet in world.Tables.Select(t => t.SheetDefId).Distinct().Order())
                {
                    _ = await new SheetEditGate(db).EnterEditAsync(world.Doc.DocumentId, sheet, Period, ct);
                }

                seeds.Clear();
                responses = await handler.HandleWorkbookAsync(requests, seeds, ct);
            },
            CancellationToken.None);

        return new Run(responses, seeds);
    }

    /// <summary>Поштучні виклики в одній транзакції — як сьогодні <c>ExcelImporter</c>.</summary>
    private async Task<Run> RunSequentialAsync(World world, AccessProfile profile, List<PatchCellsRequest> requests)
    {
        await using var db = world.Builder.CreateContext();
        var seeds = new List<RecalculationSeed>();
        var responses = new List<PatchCellsResponse>();

        // ⚠ MI-02 (в): поштучний шлях сам ставить задачу — насіння береться з
        // її payload (`Cells`), тим самим переліком, що пішов би в чергу.
        // Ідентифікатор — null: відповіді книги його не несуть (одна задача на
        // книгу — в `ExcelImporter`), і порівнюються тут лише записані дані.
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueAsync<IFormulaRecalculationJob>(Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                var payload = call.ArgAt<object>(0);
                seeds.AddRange((IEnumerable<RecalculationSeed>)payload.GetType().GetProperty("Cells")!.GetValue(payload)!);
                return (string)null!;
            });
        var handler = Handler(db, profile, jobs: jobs);

        await new UnitOfWork(db).ExecuteInTransactionAsync(
            async ct =>
            {
                seeds.Clear();
                responses.Clear();
                foreach (var request in requests)
                {
                    responses.Add(await handler.HandleAsync(request, ct));
                }
            },
            CancellationToken.None);

        return new Run(responses, seeds);
    }

    private PatchCellsHandler Handler(
        EcrDbContext db,
        AccessProfile profile,
        Func<ICellStore, ICellStore>? cells = null,
        Func<ISheetEditGate, ISheetEditGate>? gate = null,
        IBackgroundJobScheduler? jobs = null)
    {
        var clock = new TestClock(Now);
        var metadata = new MetadataCache(_memory, db);
        var real = new AccessDecisionService(
            db, metadata, new AccessProfileCache(_memory), clock, Substitute.For<ICurrentUser>(), new WorkflowStore(db));

        // ⚠ Профіль — готовий; решта рішень — справжня служба.
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanReadDocumentAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));
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

        // ⚠ S6: межі читання (конфлікт версії, фільтр повідомлень) — теж
        // справжня служба, інакше заглушка віддала б `null`.
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.ReadScopeAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(1);

        ICellStore cellStore = new NormalizedCellStore(db);
        ISheetEditGate sheetGate = new SheetEditGate(db);

        return new PatchCellsHandler(
            cells?.Invoke(cellStore) ?? cellStore,
            Rows(db, clock),
            new DocumentStore(db),
            new PeriodStore(db),
            metadata,
            access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            new MethodologyStore(db),
            new RegistryStore(db),
            new DocumentHeaderStore(db),
            new AuditWriter(db),
            new AuditReader(db),
            jobs ?? Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db, clock),
            user,
            clock,
            gate?.Invoke(sheetGate) ?? sheetGate,
            new UnitCatalog(db));
    }

    private RowStore Rows(EcrDbContext db, IClock? clock = null)
        => new(db, new BulkCellLoader(sql.ConnectionString, 1000), clock ?? new TestClock(Now));

    private static AccessProfile Writer(World world)
        => new AccessBuilder { UserId = 1 }.Grant(ResourceKind.Project, world.Doc.ProjectId, GrantLevel.Write).Build();

    // ── Світ ─────────────────────────────────────────────────────────────────

    private sealed record Table(
        long InstanceId,
        int TableDefId,
        int SheetDefId,
        IReadOnlyList<int> ColumnIds,
        IReadOnlyList<string> ColumnCodes,
        IReadOnlyList<long> RowIds,
        IReadOnlyList<string> RowKeys);

    private sealed record World(TestDocumentBuilder Builder, TestDocument Doc, IReadOnlyList<Table> Tables);

    /// <summary>
    /// Документ за 202610: базова таблиця <c>Mixed</c> (3 колонки, перша текстова)
    /// і таблиці додаткових аркушів (2 числові колонки); у кожній 3 рядки, у
    /// рядках 0 і 1 уже є значення останньої колонки (1 і 2).
    /// </summary>
    private async Task<World> ArrangeAsync(
        IReadOnlyList<int> tablesPerSheet, int? maxDynamicRows = null, bool includeBase = true, bool mixedExtras = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            PeriodKeyValue, columnCount: 3, rowCount: 3, rowMode: TableRowMode.Mixed, ct: CancellationToken.None);
        var extra = await MultiTableDocument.AddTablesAsync(
            builder, doc, tablesPerSheet, columnCount: 2, rowCount: 3, ct: CancellationToken.None);

        var tables = new List<Table>();
        if (includeBase)
        {
            var codes = new List<string>();
            await QueryAsync(
                $"SELECT Code FROM cfg.ColumnDef WHERE TableDefId = {doc.TableDefId} ORDER BY Ordinal",
                reader => codes.Add(reader.GetString(0)));
            var keys = new List<string>();
            await QueryAsync(
                $"SELECT RowKey FROM doc.TableRow WHERE PeriodKey = {PeriodKeyValue} AND TableInstanceId = {doc.TableInstanceId} ORDER BY Ordinal",
                reader => keys.Add(reader.GetString(0)));
            tables.Add(new Table(doc.TableInstanceId, doc.TableDefId, doc.SheetDefId, doc.ColumnDefIds, codes, doc.RowIds, keys));
        }

        tables.AddRange(extra.Select(t => new Table(
            t.TableInstanceId, t.TableDefId, t.SheetDefId, t.ColumnDefIds, t.ColumnCodes, t.RowIds, t.RowKeys)));

        if (maxDynamicRows is { } max)
        {
            await ExecuteAsync($"UPDATE cfg.TableDef SET MaxDynamicRows = {max} WHERE Id = {doc.TableDefId}");
        }

        if (mixedExtras)
        {
            await ExecuteAsync(
                $"UPDATE cfg.TableDef SET RowMode = {(int)TableRowMode.Mixed} WHERE Id IN ({string.Join(",", extra.Select(t => t.TableDefId))})");
        }

        await using var db = builder.CreateContext();

        // Період відкритий — інакше справжня служба прав відмовить усім (PeriodNotOpenYet).
        var period = await db.Periods.FirstAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue);
        period.AdvanceTo(PeriodState.Open, Now);
        await db.SaveChangesAsync();

        var store = new NormalizedCellStore(db);
        foreach (var table in tables)
        {
            await store.ApplyAsync(
                new CellChangeSet(
                    table.InstanceId,
                    [Cell(table, 0, 1m), Cell(table, 1, 2m)],
                    [],
                    [table.RowIds[0], table.RowIds[1]],
                    ChangedByUserId: 1,
                    IsLateEdit: false),
                CancellationToken.None);
        }

        return new World(builder, doc, tables);
    }

    private static CellRecord Cell(Table table, int row, decimal value)
        => new(
            new CellAddress(Period, table.RowIds[row], table.ColumnIds[^1]),
            table.TableDefId,
            new CellValueData { ValueNumeric = value });

    /// <summary>
    /// Той самий за формою батч на кожну таблицю: змінити комірку рядка 0 (у
    /// таблиці 2 — тим самим значенням, U-22), стерти рядка 1, нова комірка в
    /// рядку 2 (у таблиці 1 — ще й явна порожнеча); у базовій — новий рядок і
    /// текст.
    /// </summary>
    private static List<PatchCellsRequest> Requests(
        World world, Dictionary<long, Dictionary<string, string>> versions, bool newRowsEverywhere = false)
    {
        var result = new List<PatchCellsRequest>();
        for (var ti = 0; ti < world.Tables.Count; ti++)
        {
            var table = world.Tables[ti];
            var last = table.ColumnCodes[^1];
            var version = versions[table.InstanceId];

            var row2 = new List<PatchCell> { new(last, 200m + ti) };
            if (ti == 1)
            {
                row2.Add(new PatchCell(table.ColumnCodes[0], null, IsEmpty: true));
            }

            if (ti == 0 && !newRowsEverywhere)
            {
                row2.Add(new PatchCell(table.ColumnCodes[0], "Київ"));
            }

            var rows = new List<PatchRow>
            {
                new(table.RowKeys[0], version[table.RowKeys[0]], [new PatchCell(last, ti == 2 ? 1m : 100m + ti)]),
                new(table.RowKeys[1], version[table.RowKeys[1]], [new PatchCell(last, null)]),
                new(table.RowKeys[2], version[table.RowKeys[2]], row2),
            };

            if (ti == 0 || newRowsEverywhere)
            {
                rows.Add(new PatchRow(NewRowKey, null, [new PatchCell(last, 300m + ti)]));
            }

            result.Add(new PatchCellsRequest(table.InstanceId, PeriodKeyValue, "Import", rows));
        }

        return result;
    }

    private async Task<Dictionary<long, Dictionary<string, string>>> VersionsAsync(World world)
    {
        await using var db = world.Builder.CreateContext();
        var rows = await Rows(db).GetRowsBatchAsync([.. world.Tables.Select(t => t.InstanceId)], Period, CancellationToken.None);
        return rows.ToDictionary(p => p.Key, p => p.Value.ToDictionary(r => r.RowKey, r => r.RowVersion, StringComparer.Ordinal));
    }

    /// <summary>Два запис довідників і колонка <paramref name="columnDefId"/> як Lookup на перший; повертає запис другого.</summary>
    private async Task<long> LookupToForeignRegistryAsync(World world, int columnDefId)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = world.Builder.CreateContext();
        var own = new RegistryDef(EcrCode.Create($"WBOWN_{tag}"), Text("own"), isTemporal: false);
        var foreign = new RegistryDef(EcrCode.Create($"WBFOR_{tag}"), Text("foreign"), isTemporal: false);
        db.RegistryDefs.AddRange(own, foreign);
        await db.SaveChangesAsync();

        var entry = new RegistryEntry(foreign.Id, EcrCode.Create("E1"), Text("E1"));
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();

        await ExecuteAsync(
            $"UPDATE cfg.ColumnDef SET DataType = {(int)CellDataType.Lookup}, LookupRegistryDefId = {own.Id} WHERE Id = {columnDefId}");
        return entry.Id;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    // ── Стан у індексах ──────────────────────────────────────────────────────

    private async Task<List<string>> StateAsync(World world)
    {
        var lines = new List<string>();
        await QueryAsync(
            $"""
            SELECT r.TableInstanceId, r.RowKey, r.Ordinal, r.IsDeleted,
                   c.ColumnDefId, c.ValueString, c.ValueNumeric, c.IsEmpty, c.IsCalculated
            FROM doc.TableRow AS r
            LEFT JOIN doc.CellValue AS c ON c.PeriodKey = r.PeriodKey AND c.TableRowId = r.Id
            WHERE r.PeriodKey = {PeriodKeyValue} AND r.TableInstanceId IN ({InList(world)})
            """,
            reader =>
            {
                var ti = IndexOf(world, reader.GetInt64(0));
                var row = RowLabel(world.Tables[ti], reader.GetString(1));
                lines.Add($"{ti}|{row}|ord={reader.GetInt32(2)}|del={reader.GetBoolean(3)}");

                if (!reader.IsDBNull(4))
                {
                    var column = world.Tables[ti].ColumnIds.ToList().IndexOf(reader.GetInt32(4));
                    var text = reader.IsDBNull(5) ? "∅" : reader.GetString(5);
                    var number = reader.IsDBNull(6) ? "∅" : reader.GetDecimal(6).ToString("0.####", CultureInfo.InvariantCulture);
                    lines.Add($"{ti}|{row}|c{column}|{text}|{number}|{reader.GetBoolean(7)}|{reader.GetBoolean(8)}");
                }
            });

        // ⚠ Версії рядків тут немає: лічильник rowversion спільний на базу, тож
        // між світами вони різні. Їх перевіряють відповіді (== базі) і
        // «нічого не записано» (== до спроби).
        return [.. lines.Distinct().Order(StringComparer.Ordinal)];
    }

    private async Task<List<string>> AuditAsync(World world)
    {
        var lines = new List<string>();
        await QueryAsync(
            $"""
            SELECT a.ColumnDefId, a.RowKey, a.OldValue, a.NewValue, a.ChangedByUserId, a.Origin, a.IsLateEdit, a.ChangedAt,
                   r.TableInstanceId, r.RowKey
            FROM aud.CellChange AS a
            JOIN doc.TableRow AS r ON r.PeriodKey = a.PeriodKey AND r.Id = a.TableRowId
            WHERE a.DocumentId = {world.Doc.DocumentId} AND a.PeriodKey = {PeriodKeyValue}
            """,
            reader =>
            {
                var ti = IndexOf(world, reader.GetInt64(8));
                var table = world.Tables[ti];
                Assert.Equal(reader.GetString(9), reader.GetString(1));
                lines.Add(string.Join(
                    "|",
                    ti,
                    RowLabel(table, reader.GetString(1)),
                    "c" + table.ColumnIds.ToList().IndexOf(reader.GetInt32(0)),
                    reader.IsDBNull(2) ? "∅" : reader.GetString(2),
                    reader.IsDBNull(3) ? "∅" : reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    reader.GetBoolean(6),
                    reader.GetDateTime(7).ToString("O", CultureInfo.InvariantCulture)));
            });

        return [.. lines.Order(StringComparer.Ordinal)];
    }

    private async Task<(DateTime? At, int? By)> DocumentTouchAsync(World world)
    {
        (DateTime?, int?) result = default;
        await QueryAsync(
            $"SELECT ModifiedAt, ModifiedByUserId FROM doc.Document WHERE Id = {world.Doc.DocumentId}",
            reader => result = (
                reader.IsDBNull(0) ? null : reader.GetDateTime(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1)));
        return result;
    }

    /// <summary>Відповіді в індексах; кожна повернута версія мусить бути версією в базі.</summary>
    private async Task<List<string>> ResponsesAsync(World world, IReadOnlyList<PatchCellsResponse> responses)
    {
        var current = await VersionsAsync(world);
        var lines = new List<string>();
        for (var ti = 0; ti < responses.Count; ti++)
        {
            var response = responses[ti];
            var table = world.Tables[ti];
            Assert.Null(response.RecalculationJobId);
            lines.Add($"{ti}|applied={response.AppliedCells}|validation={response.Validation.Count}");
            foreach (var (rowKey, version) in response.RowVersions)
            {
                Assert.Equal(current[table.InstanceId][rowKey], version);
                lines.Add($"{ti}|{RowLabel(table, rowKey)}");
            }
        }

        return [.. lines.Order(StringComparer.Ordinal)];
    }

    private static List<string> Seeds(World world, List<RecalculationSeed> seeds)
        => [.. seeds.Select(seed =>
        {
            var ti = world.Tables.ToList().FindIndex(t => t.ColumnIds.Contains(seed.ColumnDefId));
            var table = world.Tables[ti];
            var row = table.RowIds.ToList().IndexOf(seed.RowId);
            return $"{ti}|{(row >= 0 ? $"r{row}" : "new")}|c{table.ColumnIds.ToList().IndexOf(seed.ColumnDefId)}";
        })];

    private static int IndexOf(World world, long instanceId)
        => world.Tables.ToList().FindIndex(t => t.InstanceId == instanceId);

    private static string RowLabel(Table table, string rowKey)
    {
        var index = table.RowKeys.ToList().IndexOf(rowKey);
        return index >= 0 ? $"r{index}" : rowKey;
    }

    private static string InList(World world)
        => string.Join(",", world.Tables.Select(t => t.InstanceId.ToString(CultureInfo.InvariantCulture)));

    private async Task BumpAsync(long rowId)
        => await ExecuteAsync(
            $"UPDATE doc.TableRow SET ModifiedAt = SYSUTCDATETIME() WHERE PeriodKey = {PeriodKeyValue} AND Id = {rowId}");

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        Assert.True(await command.ExecuteNonQueryAsync() > 0, text);
    }

    private async Task QueryAsync(string text, Action<SqlDataReader> onRow)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            onRow(reader);
        }
    }

    /// <summary>Сховище комірок, перед пакетним записом якого хтось інший встигає змінити рядок.</summary>
    private sealed class BumpingCellStore(ICellStore inner, Func<Task> before) : ICellStore
    {
        public Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct)
            => inner.ReadSliceAsync(tableInstanceId, ct);

        public Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> ReadSlicesAsync(
            IReadOnlyList<long> tableInstanceIds, CancellationToken ct)
            => inner.ReadSlicesAsync(tableInstanceIds, ct);

        public Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
            IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
            => inner.ReadCellsAsync(addresses, ct);

        public Task<IReadOnlyDictionary<long, string>> ApplyAsync(CellChangeSet changes, CancellationToken ct)
            => inner.ApplyAsync(changes, ct);

        public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>>> ApplyBatchAsync(
            IReadOnlyCollection<CellChangeSet> changes, CancellationToken ct)
        {
            await before();
            return await inner.ApplyBatchAsync(changes, ct);
        }
    }

    /// <summary>Блокування аркуша, перед винятковим режимом якого хтось інший встигає додати рядок.</summary>
    private sealed class InterceptingGate(ISheetEditGate inner, Func<Task> beforeSubmit) : ISheetEditGate
    {
        public Task<DocumentStatus> EnterEditAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
            => inner.EnterEditAsync(documentId, sheetDefId, periodKey, ct);

        public async Task EnterSubmitAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        {
            await beforeSubmit();
            await inner.EnterSubmitAsync(documentId, sheetDefId, periodKey, ct);
        }
    }
}
