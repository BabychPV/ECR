// tests/Ecr.Infrastructure.Tests/Persistence/PatchCellsTransactionalEnqueueTests.cs
using System.Diagnostics;
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Tests.Jobs;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// MI-02 (в), F1d: постановка перерахунку PATCH — у транзакції запису, коли
/// черга в базі, і після коміту, коли черга — Quartz у пам'яті.
/// </summary>
/// <remarks>
/// Справжні сховища, <see cref="UnitOfWork"/>, <see cref="DbJobQueue"/> і
/// <see cref="DbBackgroundJobScheduler"/>; метадані й права підмінені, як у
/// <c>PatchCellsAtomicityTests</c>. Межу «до коміту» дає <see cref="ProbingUnitOfWork"/>:
/// його зонд виконується всередині тієї самої транзакції, після тіла обробника.
/// <para>
/// Мутації: у режимі бази поставити після коміту — червоні «видима лише після
/// коміту» (усередині транзакції 0 рядків) і «відкат» (те саме); у Quartz
/// поставити всередину — червоний «Quartz після коміту».
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "MI-02")]
public sealed class PatchCellsTransactionalEnqueueTests(SqlServerFixture sql, ITestOutputHelper output)
    : DbJobQueueTestsBase(sql)
{
    private const string FaultMarker = "F1D_FAULT_AFTER_ENQUEUE";

    [Fact]
    public async Task Черга_в_базі_задача_в_транзакції_запису_невидима_іншому_зєднанню_до_коміту()
    {
        var doc = await ArrangeAsync();
        await using var db = Sql.CreateContext();
        var trace = new List<string>();
        var (ownBeforeCommit, claimedBeforeCommit) = (-1, (string?)"not-probed");

        var uow = new ProbingUnitOfWork(db, trace, async () =>
        {
            ownBeforeCommit = await db.JobProgresses.CountAsync(p => p.Lane != null);
            await using var other = NewHost();
            claimedBeforeCommit = (await other.ClaimAsync())?.Claim.JobId;
        });

        var response = await Handler(db, uow, DatabaseScheduler(db), doc)
            .HandleAsync(await RequestAsync(doc, 777m), CancellationToken.None);

        // Усередині транзакції задача вже є (своє з'єднання), але claim з
        // іншого (READPAST) її не бачить — рядок під X-локом до коміту.
        Assert.Equal(1, ownBeforeCommit);
        Assert.Null(claimedBeforeCommit);

        await using var after = NewHost();
        Assert.NotNull(response.RecalculationJobId);
        Assert.Equal(response.RecalculationJobId, (await after.ClaimAsync())?.Claim.JobId);
        Assert.Equal(1, await QueueCountAsync());
    }

    [Fact]
    public async Task Черга_в_базі_відкат_після_запису_й_постановки_не_лишає_задачі()
    {
        var doc = await ArrangeAsync();
        await using var db = Sql.CreateContext();
        var ownBeforeRollback = -1;

        var uow = new ProbingUnitOfWork(db, [], async () =>
        {
            ownBeforeRollback = await db.JobProgresses.CountAsync(p => p.Lane != null);
            throw new InvalidOperationException(FaultMarker);
        });

        var request = await RequestAsync(doc, 778m);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Handler(db, uow, DatabaseScheduler(db), doc).HandleAsync(request, CancellationToken.None));
        Assert.Equal(FaultMarker, thrown.Message);

        // ⛔ Проти хибнозеленого: до відкату задача справді стояла в транзакції.
        Assert.Equal(1, ownBeforeRollback);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task Quartz_ставить_після_коміту_а_на_відкаті_не_ставить_зовсім()
    {
        var doc = await ArrangeAsync();

        await using (var db = Sql.CreateContext())
        {
            var trace = new List<string>();
            var jobs = QuartzLike(db, trace);
            await Handler(db, new ProbingUnitOfWork(db, trace, () => Task.CompletedTask), jobs, doc)
                .HandleAsync(await RequestAsync(doc, 779m), CancellationToken.None);

            Assert.Equal(["probe", "commit", "enqueue:tx=False"], trace);
        }

        await using (var db = Sql.CreateContext())
        {
            var trace = new List<string>();
            var jobs = QuartzLike(db, trace);
            var request = await RequestAsync(doc, 780m);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Handler(db, new ProbingUnitOfWork(db, trace, () => throw new InvalidOperationException(FaultMarker)), jobs, doc)
                    .HandleAsync(request, CancellationToken.None));

            Assert.Equal(["probe", "rollback"], trace);
        }
    }

    /// <summary>
    /// Умова «Аудиту» 2: тривалість транзакції PATCH (BEGIN…COMMIT) у двох
    /// режимах — медіана з 20 після прогріву. Вимір, не поріг: числа — у вивід.
    /// </summary>
    [Fact]
    [Trait("Measurement", "F1d")]
    public async Task Вимір_тривалості_транзакції_PATCH_Quartz_проти_бази()
    {
        const int Samples = 20;
        const int Warmup = 3;
        var doc = await ArrangeAsync();
        var quartz = new List<double>();
        var database = new List<double>();
        var value = 1000m;

        for (var i = 0; i < Warmup + Samples; i++)
        {
            foreach (var enlist in new[] { false, true })
            {
                await using var db = Sql.CreateContext();
                var uow = new ProbingUnitOfWork(db, [], () => Task.CompletedTask);
                var jobs = enlist ? DatabaseScheduler(db) : QuartzLike(db, []);
                await Handler(db, uow, jobs, doc).HandleAsync(await RequestAsync(doc, value++), CancellationToken.None);

                if (i >= Warmup)
                {
                    (enlist ? database : quartz).Add(uow.LastTransactionMs);
                }
            }
        }

        static double Median(List<double> xs)
        {
            var s = xs.Order().ToList();
            return (s[(s.Count - 1) / 2] + s[s.Count / 2]) / 2;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"F1d tx PATCH ms, N={Samples}: Quartz median={Median(quartz):F2} (min {quartz.Min():F2}, max {quartz.Max():F2}); " +
            $"Database median={Median(database):F2} (min {database.Min():F2}, max {database.Max():F2})"));

        Assert.Equal(Samples, quartz.Count);
        Assert.Equal(Samples, database.Count);
    }

    // ── Світ ─────────────────────────────────────────────────────────────────

    private async Task<TestDocument> ArrangeAsync()
        => await new TestDocumentBuilder(Sql.ConnectionString)
            .BuildAsync(rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

    private static DbBackgroundJobScheduler DatabaseScheduler(EcrDbContext db)
        => new(
            new DbJobQueue(db, new SystemClock()),
            new QuartzJobScheduler(null, new JobProgressStore(db), new SystemClock()),
            new JobQueueSignal());

    /// <summary>Планувальник поза базою: фіксує виклик і чи був він у транзакції.</summary>
    private static IBackgroundJobScheduler QuartzLike(EcrDbContext db, List<string> trace)
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnlistsInCallerTransaction.Returns(false);
        jobs.EnqueueAsync<IFormulaRecalculationJob>(Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(_ =>
            {
                trace.Add($"enqueue:tx={db.Database.CurrentTransaction is not null}");
                return "IFormulaRecalculationJob-quartz";
            });
        return jobs;
    }

    private async Task<int> QueueCountAsync()
    {
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.CountAsync(p => p.Lane != null);
    }

    private async Task<PatchCellsRequest> RequestAsync(TestDocument doc, decimal value)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT TOP 1 RowKey, RowVersion FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} ORDER BY Id";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var rowKey = reader.GetString(0);
        var version = Convert.ToBase64String((byte[])reader[1]);

        return new PatchCellsRequest(doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(rowKey, version, [new PatchCell(CodeOf(doc, 2), value)])]);
    }

    private PatchCellsHandler Handler(
        EcrDbContext db, IUnitOfWork uow, IBackgroundJobScheduler jobs, TestDocument doc)
    {
        var column1 = ColumnDefFor(doc, 1, CellDataType.String);
        var column2 = ColumnDefFor(doc, 2, CellDataType.Decimal);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        var table = new TableDef(sheet.Id, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        table.AddColumn(column1);
        table.AddColumn(column2);
        sheet.AddTable(table);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [doc.ColumnDefIds[0]] = column1, [doc.ColumnDefIds[1]] = column2 },
            RowsByKey: new Dictionary<(int, string), RowDef>()));

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
            .Returns((PeriodState?)PeriodState.Open);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(1, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p", UserId = 1, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        });
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        access.CanEditCellsAsync(
                Arg.Any<AccessProfile>(), doc.TableInstanceId, Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(doc.RowIds
                .SelectMany(rowId => doc.ColumnDefIds.Select(columnId => new CellAddress(doc.PeriodKey, rowId, columnId)))
                .ToDictionary(address => address, _ => EditDecision.Allow()));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(1);

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(doc.TableDefId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        var registries = Substitute.For<IRegistryStore>();
        registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToHashSet());

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        var clock = new SystemClock();
        return new PatchCellsHandler(
            new NormalizedCellStore(db), new RowStore(db, new BulkCellLoader(Sql.ConnectionString, 1000), clock),
            new DocumentStore(db), periods, metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            methodologies, registries, headers, new AuditWriter(db), Substitute.For<IAuditReader>(),
            jobs, uow, user, clock, Substitute.For<ISheetEditGate>(), Substitute.For<IUnitCatalog>());
    }

    private static ColumnDef ColumnDefFor(TestDocument doc, int ordinal, CellDataType type)
    {
        var id = doc.ColumnDefIds[ordinal - 1];
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"C{ordinal}_{id}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = $"Col{ordinal}" }), ordinal, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, id);
        return column;
    }

    private static string CodeOf(TestDocument doc, int ordinal) => $"C{ordinal}_{doc.ColumnDefIds[ordinal - 1]}";

    /// <summary>
    /// Справжня <see cref="UnitOfWork"/>, чия ЗОВНІШНЯ транзакція після тіла
    /// викликає зонд — усе ще всередині транзакції, до коміту — і міряє
    /// тривалість від BEGIN до COMMIT.
    /// </summary>
    private sealed class ProbingUnitOfWork(EcrDbContext db, List<string> trace, Func<Task> probe) : IUnitOfWork
    {
        private readonly UnitOfWork inner = new(db);

        public double LastTransactionMs { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken ct) => inner.SaveChangesAsync(ct);

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct) => inner.BeginTransactionAsync(ct);

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
        {
            if (db.Database.CurrentTransaction is not null)
            {
                await inner.ExecuteInTransactionAsync(operation, ct);
                return;
            }

            var watch = Stopwatch.StartNew();
            try
            {
                await inner.ExecuteInTransactionAsync(
                    async c =>
                    {
                        await operation(c);
                        trace.Add("probe");
                        await probe();
                    },
                    ct);
            }
            catch
            {
                trace.Add("rollback");
                throw;
            }

            LastTransactionMs = watch.Elapsed.TotalMilliseconds;
            trace.Add("commit");
        }
    }
}
