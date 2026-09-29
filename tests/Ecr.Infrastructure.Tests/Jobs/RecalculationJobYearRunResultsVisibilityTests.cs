// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobYearRunResultsVisibilityTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Результати методологій нічного перерахунку (<c>PeriodKey = null</c>) ВИДНО в
/// кожному реальному періоді тим шляхом, яким їх читають споживачі
/// (<see cref="CalculationResultStore.ReadCurrentAsync"/>), а ручний прогін
/// одного періоду перекриває нічний лише в ЦЬОМУ періоді.
/// </summary>
/// <remarks>
/// ⛔ Дефект, який доводять ці тести. <c>RecalculationJob</c> для прогону «на
/// весь рік» створював ОДИН <c>CalculationRun</c> із <c>PeriodKey = null</c>,
/// а оркестратора кликав поперіодно. Оркестратор пише результати через
/// <c>ICalculationResultStore.WriteResultsAsync(runId, outputs)</c> — без ключа
/// періоду, — і сховище брало його з прогону: <c>run.PeriodKey ?? 0</c>. Отже
/// кожне число нічного перерахунку лягало в «період 0», якого не читає ніхто:
/// <c>ReadCurrentAsync</c> і зріз <c>rpt.*</c> фільтрують за СПРАВЖНІМ ключем.
/// Нічний перерахунок «успішно» рахував і нічого не показував.
///
/// ⚠ Оркестратор тут — фейк, що пише через СПРАВЖНІЙ
/// <see cref="CalculationResultStore"/> рівно так, як це робить
/// <c>CalculationOutputWriter</c> (результати → <c>SaveChanges</c>): дефект у
/// тому, КУДИ лягає записане, а не в тому, що рахує методологія.
/// Завершення — справжній <see cref="RunCalculationHandler.CompleteAsync"/> над
/// тим самим сховищем: перемикання актуальності — друга половина твердження.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobYearRunResultsVisibilityTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    private static readonly PeriodKey February = new(202602);

    /// <summary>Мітка значень нічного прогону.</summary>
    private const decimal Nightly = 1_000m;

    /// <summary>Мітка значень ручного прогону.</summary>
    private const decimal Manual = 2_000m;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Результати_нічного_перерахунку_видно_в_КОЖНОМУ_періоді_разом_із_проміжними()
    {
        var (builder, document) = await ArrangeAsync();

        await RunJobAsync(builder, NightlyRequest(document), Nightly);

        await using var db = builder.CreateContext();
        var store = new CalculationResultStore(db, new TestClock(Now));

        var january = await store.ReadCurrentAsync(document.DocumentId, document.PeriodKey.Value, default);
        var february = await store.ReadCurrentAsync(document.DocumentId, February.Value, default);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ. До фіксу обидва списки порожні: усе записане
        // лежить у `PeriodKey = 0`.
        Assert.Equal(Expected(Nightly, document.PeriodKey), Shape(january));
        Assert.Equal(Expected(Nightly, February), Shape(february));

        // У «період 0» не лягло нічого — ключ, який не є періодом (`A7-28`).
        Assert.False(await db.CalculationResults.AsNoTracking().AnyAsync(
            r => r.DocumentId == document.DocumentId && r.PeriodKey == 0));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ручний_прогін_періоду_після_нічного_перекриває_ЛИШЕ_свій_період(bool documentScoped)
    {
        var (builder, document) = await ArrangeAsync();

        await RunJobAsync(builder, NightlyRequest(document), Nightly);

        // Ручний прогін січня: або всього проєкту (`RunCalculationHandler`,
        // `DocumentId = 0`), або кнопка документа (`RecalculateDocumentHandler`).
        await RunJobAsync(
            builder,
            new RecalculationRequest(
                ProjectId: document.ProjectId,
                DocumentId: documentScoped ? document.DocumentId : 0,
                PeriodKey: document.PeriodKey.Value,
                TriggeredByUserId: 7),
            Manual);

        await using var db = builder.CreateContext();
        var store = new CalculationResultStore(db, new TestClock(Now));

        var january = await store.ReadCurrentAsync(document.DocumentId, document.PeriodKey.Value, default);
        var february = await store.ReadCurrentAsync(document.DocumentId, February.Value, default);

        // ⛔ Січень — ЛИШЕ ручний прогін: не суміш двох актуальних прогонів
        // (подвоєні рядки), і не нічний, що «пересидів» новіший.
        Assert.Equal(Expected(Manual, document.PeriodKey), Shape(january));

        // ⛔ Лютий ручний прогін не чіпав — актуальним лишається нічний.
        Assert.Equal(Expected(Nightly, February), Shape(february));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Наступний_нічний_перерахунок_перекриває_ручний_прогін_періоду()
    {
        var (builder, document) = await ArrangeAsync();

        await RunJobAsync(
            builder,
            new RecalculationRequest(document.ProjectId, 0, document.PeriodKey.Value, 7),
            Manual);
        await RunJobAsync(builder, NightlyRequest(document), Nightly);

        await using var db = builder.CreateContext();
        var store = new CalculationResultStore(db, new TestClock(Now));

        var january = await store.ReadCurrentAsync(document.DocumentId, document.PeriodKey.Value, default);

        // Нічний новіший і рахував січень заново — актуальний саме він, один.
        Assert.Equal(Expected(Nightly, document.PeriodKey), Shape(january));
    }

    private static RecalculationRequest NightlyRequest(TestDocument document)
        => new(ProjectId: document.ProjectId, DocumentId: 0, PeriodKey: null, TriggeredByUserId: null);

    /// <summary>Очікуваний вміст одного періоду: вихід і проміжне значення.</summary>
    private static List<string> Expected(decimal label, PeriodKey period)
        =>
        [
            $"R-1|W_COMP|{Marker(label, period)}",
            $"R-1|tons|{Marker(label, period)}",
        ];

    private static decimal Marker(decimal label, PeriodKey period) => label + period.Sequence;

    private static List<string> Shape(IReadOnlyList<CalculationResultRow> rows)
        => [.. rows
            .Select(r => $"{r.SourceRowKey}|{r.OutputCode}|{r.Value:0.####}")
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Документ із двома періодами (січень і лютий), по екземпляру таблиці в
    /// кожному, і активна прив'язка методології до таблиці.
    /// </summary>
    private async Task<(TestDocumentBuilder Builder, TestDocument Document)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();

        var period = new Period(
            document.ProjectId, February, (byte)February.Sequence,
            new DateOnly(February.Year, February.Sequence, 1),
            new DateOnly(February.Year, February.Sequence, DateTime.DaysInMonth(February.Year, February.Sequence)));
        db.Periods.Add(period);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(
            February, instanceId, document.DocumentId, document.TableDefId, Now));

        var tag = Guid.NewGuid().ToString("N")[..8];
        var methodology = new Methodology(
            EcrCode.Create($"YRV_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        db.CalculationBindings.Add(new CalculationBinding(
            document.TableDefId, document.ColumnDefIds[1], methodology.Id, "tons", "{}"));

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        _methodologyVersionId = version.Id;
        _unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        return (builder, document);
    }

    private int _methodologyVersionId;

    private int _unitId;

    /// <summary>Проганяє задачу на окремому контексті — як один виклик черги.</summary>
    private async Task RunJobAsync(TestDocumentBuilder builder, RecalculationRequest request, decimal label)
    {
        await using var db = builder.CreateContext();
        var clock = new TestClock(Now);

        var job = new RecalculationJob(
            db,
            new WritingRunner(builder, _methodologyVersionId, _unitId, label),
            RunHandler(db, clock),
            FormulaService(),
            clock);

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);
    }

    /// <summary>Обробник завершення над СПРАВЖНІМ сховищем і тим самим контекстом, що в задачі.</summary>
    private static RunCalculationHandler RunHandler(EcrDbContext db, TestClock clock)
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>());

    /// <summary>Фаза формул шаблону нічого не пише — тести про методології.</summary>
    private static RecalculationService FormulaService()
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableInstanceRef>>([]));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var header = Substitute.For<IDocumentHeaderStore>();
        header.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new(
            Substitute.For<ICellStore>(),
            rows,
            Substitute.For<IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<IRegistryStore>(),
            header,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IUnitOfWork>(), Substitute.For<ISheetEditGate>());
    }

    /// <summary>
    /// Фейк оркестратора: на кожну прив'язку пише вихід і проміжне значення
    /// через справжнє сховище, власним контекстом — як <c>CalculationOutputWriter</c>.
    /// Значення несе мітку прогону й номер періоду, у якому його «порахували».
    /// </summary>
    private sealed class WritingRunner(
        TestDocumentBuilder builder, int methodologyVersionId, int unitId, decimal label) : ICalculationRunner
    {
        public async Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            if (bindings.Count == 0)
            {
                return new ModuleProfile();
            }

            var value = Marker(label, periodKey);

            await using var db = builder.CreateContext();
            var store = new CalculationResultStore(db, new TestClock(Now));

            await store.WriteResultsAsync(
                calculationRunId,
                [
                    new CalculationOutput(documentId, "R-1",
                    [
                        new CalculationOutputValue(methodologyVersionId, null, "tons", value, unitId),
                        new CalculationOutputValue(
                            methodologyVersionId, null, "W_COMP", value, unitId, CalculationResultKind.Intermediate),
                    ], []),
                ],
                ct);
            await db.SaveChangesAsync(ct);

            return new ModuleProfile();
        }
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
