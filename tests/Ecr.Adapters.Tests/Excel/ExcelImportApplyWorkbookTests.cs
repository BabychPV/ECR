using System.Globalization;
using System.Text.Json;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
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
/// P8, застосування імпорту 3/3: <c>ExcelImporter.ApplyAsync</c> на реальній
/// базі — книга пише одним книжковим шляхом, тож звернень до бази не більшає з
/// кількістю таблиць; відмова називає таблицю й відкочує всю книгу.
/// </summary>
/// <remarks>
/// ⚠ Усе справжнє (сховища, <c>AccessDecisionService</c>, кеш метаданих,
/// блокування аркушів, одиниця роботи, сам <c>PatchCellsHandler</c>), крім
/// готового профілю прав, сховища переглядів (план подається напряму — тим
/// самим JSON, що й зберігає перегляд) і планувальника.
/// </remarks>
[Collection("SqlServer")]
public sealed class ExcelImportApplyWorkbookTests(SqlServerFixture sql) : IDisposable
{
    /// <summary>Період — не 2026-05…07 (їх архівують <c>ArchiveJobTests</c>).</summary>
    private const int PeriodKeyValue = 202610;

    private const string Token = "p8-apply";

    /// <summary>
    /// Звернень SqlClient на застосування книги з одного аркуша — однаково на 3
    /// і на 12 таблиць. До P8 (цикл поштучних PATCH) було 57 на 3 і 219 на 12.
    /// </summary>
    private const long ApplyExecutions = 19;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Застосування_імпорту_коштує_сталу_кількість_звернень_на_3_і_12_таблиць()
    {
        var world = await ArrangeAsync(12);

        await using var db = CreateContext();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        connection.StatisticsEnabled = true;

        long Executions()
        {
            var stats = connection.RetrieveStatistics();
            return Convert.ToInt64(stats["UnpreparedExecs"], CultureInfo.InvariantCulture)
                   + Convert.ToInt64(stats["PreparedExecs"], CultureInfo.InvariantCulture);
        }

        async Task<long> Measure(int tables)
        {
            var importer = Importer(db, Writer(world), await PlanAsync(world, tables));
            await using var tx = await db.Database.BeginTransactionAsync();
            connection.ResetStatistics();
            var response = await importer.ApplyAsync(world.Doc.DocumentId, Token, CancellationToken.None);
            var executions = Executions();
            await tx.RollbackAsync();

            // ⛔ Проти хибнозеленого: книга справді записала кожну таблицю.
            Assert.Equal(3 * tables, response.AppliedCells);
            return executions;
        }

        // Прогрів кешів процесу (знімок метаданих).
        await Measure(12);

        var three = await Measure(3);
        var twelve = await Measure(12);

        Assert.True(three == twelve, $"застосування імпорту: 3 → {three}; 12 → {twelve}");
        Assert.Equal(ApplyExecutions, twelve);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Заборона_на_таблицю_книги_відхиляє_імпорт_з_номером_таблиці_і_нічого_не_пише()
    {
        var world = await ArrangeAsync(4);
        var guilty = world.Tables[2];
        var profile = new AccessBuilder { UserId = 1 }
            .Grant(ResourceKind.Project, world.Doc.ProjectId, GrantLevel.Write)
            .Deny(ResourceKind.Table, guilty.TableDefId)
            .Build();
        var plan = await PlanAsync(world, 4);
        var before = await StateAsync(world);

        await using var db = CreateContext();
        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Importer(db, profile, plan).ApplyAsync(world.Doc.DocumentId, Token, CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", error.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.deniedCells", error.Details!["messageKey"]);
        Assert.Equal(guilty.TableInstanceId.ToString(CultureInfo.InvariantCulture), error.Details["tableInstanceId"]);
        Assert.Equal(before, await StateAsync(world));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Правка_після_перегляду_відхиляє_імпорт_з_номером_таблиці_і_лише_зміненими_комірками()
    {
        var world = await ArrangeAsync(4);
        var guilty = world.Tables[3];
        var plan = await PlanAsync(world, 4);

        // Хтось змінив рядок 0 четвертої таблиці після перегляду: остання
        // колонка — чужа правка, решта рядка — те, що перегляд бачив.
        await ExecuteAsync(
            $"UPDATE doc.TableRow SET ModifiedAt = SYSUTCDATETIME() WHERE PeriodKey = {PeriodKeyValue} AND Id = {guilty.RowIds[0]}");
        var before = await StateAsync(world);

        await using var db = CreateContext();
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Importer(db, Writer(world), plan).ApplyAsync(world.Doc.DocumentId, Token, CancellationToken.None));

        Assert.Equal("ECR-CELL-0409", error.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.batchStale", error.Details!["messageKey"]);
        Assert.Equal(guilty.TableInstanceId.ToString(CultureInfo.InvariantCulture), error.Details["tableInstanceId"]);

        // F-24: значення комірки не змінилося після перегляду — у переліку її немає.
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<CellConflictDto>>(error.Details["conflicts"]));
        Assert.Equal(before, await StateAsync(world));
    }

    /// <summary>
    /// MI-02 (в), F1d: черга в базі — задача перерахунку книги в транзакції
    /// імпорту: успіх — рівно одна, відкат після постановки — жодної.
    /// </summary>
    /// <remarks>
    /// Мутація: постановка після коміту — в транзакції задачі немає, червоне
    /// «до відкату стояла одна».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MI-02")]
    public async Task Черга_в_базі_імпорт_ставить_одну_задачу_в_транзакції_а_відкат_її_прибирає()
    {
        await PurgeQueueAsync();
        try
        {
            var world = await ArrangeAsync(3);

            await using (var db = CreateContext())
            {
                var inTransaction = -1;
                var uow = new FailingAfterBodyUnitOfWork(db, async () =>
                {
                    inTransaction = await db.JobProgresses.CountAsync(p => p.Lane != null);
                    throw new InvalidOperationException("F1D_IMPORT_FAULT");
                });
                var plan = await PlanAsync(world, 3);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Importer(db, Writer(world), plan, DatabaseJobs(db), uow)
                        .ApplyAsync(world.Doc.DocumentId, Token, CancellationToken.None));

                Assert.Equal(1, inTransaction);
            }

            Assert.Equal(0, await QueueCountAsync());

            await using (var db = CreateContext())
            {
                await Importer(db, Writer(world), await PlanAsync(world, 3), DatabaseJobs(db))
                    .ApplyAsync(world.Doc.DocumentId, Token, CancellationToken.None);
            }

            Assert.Equal(1, await QueueCountAsync());
        }
        finally
        {
            await PurgeQueueAsync();
        }
    }

    // ── Світ ─────────────────────────────────────────────────────────────────

    private static Ecr.Infrastructure.Jobs.DbBackgroundJobScheduler DatabaseJobs(EcrDbContext db)
        => new(
            new Ecr.Infrastructure.Jobs.DbJobQueue(db, new TestClock(Now)),
            new Ecr.Infrastructure.Jobs.QuartzJobScheduler(null, new JobProgressStore(db), new TestClock(Now)),
            new Ecr.Infrastructure.Jobs.JobQueueSignal());

    private async Task<int> QueueCountAsync()
    {
        await using var db = CreateContext();
        return await db.JobProgresses.CountAsync(p => p.Lane != null);
    }

    private async Task PurgeQueueAsync()
    {
        await using var db = CreateContext();
        await db.JobProgresses.Where(p => p.Lane != null).ExecuteDeleteAsync();
    }

    /// <summary>Справжня одиниця роботи; зовнішня транзакція після тіла кличе <c>fault</c> — до коміту.</summary>
    private sealed class FailingAfterBodyUnitOfWork(EcrDbContext db, Func<Task> fault) : IUnitOfWork
    {
        private readonly UnitOfWork inner = new(db);

        public Task<int> SaveChangesAsync(CancellationToken ct) => inner.SaveChangesAsync(ct);

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct) => inner.BeginTransactionAsync(ct);

        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
            => db.Database.CurrentTransaction is not null
                ? inner.ExecuteInTransactionAsync(operation, ct)
                : inner.ExecuteInTransactionAsync(
                    async c =>
                    {
                        await operation(c);
                        await fault();
                    },
                    ct);
    }

    private sealed record World(TestDocument Doc, IReadOnlyList<ExtraTable> Tables);

    /// <summary>Документ за 202610 і <paramref name="tables"/> таблиць на ОДНОМУ аркуші, період відкритий.</summary>
    private async Task<World> ArrangeAsync(int tables)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: 2, ct: CancellationToken.None);
        var extra = await MultiTableDocument.AddTablesAsync(
            builder, doc, [tables], columnCount: 2, rowCount: 3, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var period = await db.Periods.FirstAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue);
        period.AdvanceTo(PeriodState.Open, Now);
        await db.SaveChangesAsync();

        return new World(doc, extra);
    }

    /// <summary>Перегляд перших <paramref name="tables"/> таблиць: три нові значення останньої колонки в кожній.</summary>
    private async Task<string> PlanAsync(World world, int tables)
    {
        await using var db = CreateContext();
        var ids = world.Tables.Take(tables).Select(t => t.TableInstanceId).ToList();
        var rows = await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now))
            .GetRowsBatchAsync(ids, Period, CancellationToken.None);

        var diffs = world.Tables.Take(tables)
            .Select((table, i) => new TableDiff(
                table.TableInstanceId,
                PeriodKeyValue,
                [.. table.RowKeys.Select((key, r) => new ImportChange(key, table.ColumnCodes[^1], null, 100m + (10 * i) + r))],
                [],
                rows[table.TableInstanceId].ToDictionary(s => s.RowKey, s => s.RowVersion, StringComparer.Ordinal)))
            .ToList();

        return JsonSerializer.Serialize(new ImportPlan(world.Doc.DocumentId, PeriodKeyValue, diffs), Options);
    }

    private ExcelImporter Importer(
        EcrDbContext db, AccessProfile profile, string plan, IBackgroundJobScheduler? jobs = null, IUnitOfWork? uow = null)
    {
        var clock = new TestClock(Now);
        var metadata = new MetadataCache(_memory, db);
        var real = new AccessDecisionService(
            db, metadata, new AccessProfileCache(_memory), clock, Substitute.For<ICurrentUser>(), new WorkflowStore(db));

        // ⚠ Профіль — готовий; рішення — справжня служба (поштучні й пакетні).
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanReadDocumentAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));

        // S6: межі читання — справжні (конфлікт версії питає їх, щоб не назвати
        // значення прихованої колонки).
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
        uow ??= new UnitOfWork(db, clock);
        var gate = new SheetEditGate(db);
        var registries = new RegistryStore(db);
        jobs ??= Substitute.For<IBackgroundJobScheduler>();

        var patch = new PatchCellsHandler(
            cells, rows, new DocumentStore(db), new PeriodStore(db), metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            new MethodologyStore(db), registries, new DocumentHeaderStore(db), new AuditWriter(db), new AuditReader(db),
            jobs, uow, user, clock, gate, new UnitCatalog(db));

        return new ExcelImporter(
            metadata, registries, access, user, previews, patch, new ImportDiffBuilder(), cells, rows, uow, jobs, gate);
    }

    private static AccessProfile Writer(World world)
        => new AccessBuilder { UserId = 1 }.Grant(ResourceKind.Project, world.Doc.ProjectId, GrantLevel.Write).Build();

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    /// <summary>Комірки, версії рядків і аудит документа — усе, що могла змінити книга.</summary>
    private async Task<List<string>> StateAsync(World world)
    {
        var lines = new List<string>();
        var ids = string.Join(",", world.Tables.Select(t => t.TableInstanceId.ToString(CultureInfo.InvariantCulture)));
        await QueryAsync(
            $"""
            SELECT CONCAT('row|', r.Id, '|', CONVERT(bigint, r.RowVersion)) FROM doc.TableRow AS r
             WHERE r.PeriodKey = {PeriodKeyValue} AND r.TableInstanceId IN ({ids})
            UNION ALL
            SELECT CONCAT('cell|', c.TableRowId, '|', c.ColumnDefId, '|', c.ValueNumeric) FROM doc.CellValue AS c
              JOIN doc.TableRow AS r ON r.PeriodKey = c.PeriodKey AND r.Id = c.TableRowId
             WHERE c.PeriodKey = {PeriodKeyValue} AND r.TableInstanceId IN ({ids})
            UNION ALL
            SELECT CONCAT('audit|', a.Id) FROM aud.CellChange AS a
             WHERE a.DocumentId = {world.Doc.DocumentId} AND a.PeriodKey = {PeriodKeyValue}
            """,
            reader => lines.Add(reader.GetString(0)));
        return [.. lines.Order(StringComparer.Ordinal)];
    }

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
}
