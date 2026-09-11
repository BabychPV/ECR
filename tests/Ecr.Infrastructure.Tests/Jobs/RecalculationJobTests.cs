// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Прогін перерахунку веде ДВА конвеєри в заданому порядку (директива №10
/// <c>W10.1</c>).
/// </summary>
/// <remarks>
/// ⛔ Конвеєрів справді два, і вони пишуть у різні таблиці: формули шаблону —
/// у <c>doc.CellValue</c> (<c>IsCalculated = 1</c>), методології — у
/// <c>calc.CalculationResult</c> (<c>D-69</c>). До цього пакета
/// <c>RecalculationJob</c> не мав доступу до першого взагалі: у його
/// залежностях не було <c>RecalculationService</c>.
///
/// ⛔ Порядок — не косметика. <c>CalculationInputBuilder</c> будує входи
/// методології через <c>ICellStore.ReadSliceAsync</c>, тобто з тієї самої
/// <c>doc.CellValue</c>. Методології, пораховані ПЕРШИМИ, взяли б входи до
/// того, як формули шаблону їх оновили, — і видали б новий прогін із новою
/// контрольною сумою від старих чисел. Помилка без жодної видимої ознаки
/// помилки.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobTests(SqlServerFixture sql)
{
    /// <summary>Мітка фази формул шаблону в журналі порядку.</summary>
    private const string Formulas = "Формули";

    /// <summary>Мітка фази методологій у журналі порядку.</summary>
    private const string Methodologies = "Методології";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Формули_шаблону_рахуються_ПЕРЕД_методологіями()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        var order = new List<string>();
        var rows = Rows(order);

        await using var db = builder.CreateContext();

        var job = new RecalculationJob(
            db,
            new RecordingRunner(order),
            RunHandler(),
            FormulaService(rows),
            new TestClock(DateTime.UtcNow));

        await job.ExecuteAsync(Request(document), NoOpProgress.Instance, CancellationToken.None);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ пакета: порядок гарантує КОД, а не порядок
        // викликів клієнта. Обидві фази справді відбулися, і саме в цьому
        // порядку.
        Assert.Equal([Formulas, Methodologies], order);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відмова_формул_шаблону_позначає_прогін_Failed_із_причиною()
    {
        // ⛔ Крок формул стоїть ВСЕРЕДИНІ `try` прогону. Поза ним виняток
        // полетів би повз catch, прогін лишився б `Running` назавжди, а
        // задача виглядала б як «дуже довго рахує» — рівно той дефект, який
        // у цьому файлі вже розбирали двічі (`D2-285`, `D2-286`).
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Формули шаблону не перерахувалися."));

        await using var db = builder.CreateContext();

        var job = new RecalculationJob(
            db,
            new RecordingRunner([]),
            RunHandler(),
            FormulaService(rows),
            new TestClock(DateTime.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.ExecuteAsync(Request(document), NoOpProgress.Instance, CancellationToken.None));

        await using var verify = builder.CreateContext();
        var run = await verify.CalculationRuns
            .AsNoTracking()
            .SingleAsync(r => r.ProjectId == document.ProjectId);

        Assert.Equal("Failed", run.Status);
        Assert.Equal("Формули шаблону не перерахувалися.", run.ErrorMessage);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогрес_називає_фазу_а_не_лише_відсоток()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        var order = new List<string>();
        var progress = new RecordingProgress();

        await using var db = builder.CreateContext();

        var job = new RecalculationJob(
            db,
            new RecordingRunner(order) { ReportPercent = 50 },
            RunHandler(),
            FormulaService(Rows(order)),
            new TestClock(DateTime.UtcNow));

        await job.ExecuteAsync(Request(document), progress, CancellationToken.None);

        // ⛔ Голе «40 %» не означає нічого: фаз дві, і перше, що питає той,
        // хто розбирає повільний прогін, — у якій саме.
        Assert.Contains(progress.Reports, r => r.Message!.Contains("формул шаблону", StringComparison.Ordinal));
        Assert.Contains(progress.Reports, r => r.Message!.Contains("методологій", StringComparison.Ordinal));

        // ⚠ Шкала оркестратора (0…100 від власного нуля) переведена в
        // залишок загальної: інакше вона стрибала б назад із 40 % на 5 %.
        var scaled = progress.Reports.Single(r => r.Message!.StartsWith("Методології:", StringComparison.Ordinal));
        Assert.Equal(70, scaled.Percent);

        // ⚠ Жоден звіт не йде назад: 0 → 40 → 40 → 70.
        Assert.Equal(progress.Reports.Select(r => r.Percent).Order(), progress.Reports.Select(r => r.Percent));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task DocumentId_нуль_перераховує_УСІ_документи_проєкту()
    {
        // ⛔ Q-151/Q-162: `RunCalculationHandler` кладе в чергу payload БЕЗ
        // `DocumentId` узагалі (проєкт-рівневий перерахунок) — при розборі в
        // non-nullable `long` це мовчки стає 0. До цього пакета `0` не
        // трактувався як «усі документи проєкту»: фільтр `DocumentId == 0`
        // просто не знаходив жодного екземпляра таблиці, і прогін чесно
        // звітував `Succeeded` над нулем документів.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        long secondDocumentId;
        await using (var seed = builder.CreateContext())
        {
            var second = new Document(
                document.ProjectId, "DOC-EXTRA", 1,
                new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc));
            seed.Documents.Add(second);
            await seed.SaveChangesAsync();
            secondDocumentId = second.Id;
        }

        var order = new List<string>();
        var runner = new RecordingRunner(order);

        await using var db = builder.CreateContext();

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: 0,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: null);

        var job = new RecalculationJob(
            db, runner, RunHandler(), FormulaService(Rows(order)), new TestClock(DateTime.UtcNow));

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ: обидва документи проєкту дійсно перерахувалися
        // — не лише перший, і не жоден.
        //
        // ⚠ `run.Status` тут НЕ перевіряється (той самий застережний коментар,
        // що й у `RecalculationJobProjectIdTests`): `ICalculationResultStore`,
        // який перемикає актуальність прогону на `Succeeded`, замоканий —
        // цей тест про те, ЯКІ документи дійшли до оркестратора, а не про
        // повний життєвий цикл прогону.
        Assert.Equal(
            new[] { document.DocumentId, secondDocumentId }.OrderBy(id => id),
            runner.DocumentIds.OrderBy(id => id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task DocumentId_нуль_іменує_документ_у_повідомленнях_прогресу()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using (var seed = builder.CreateContext())
        {
            seed.Documents.Add(new Document(
                document.ProjectId, "DOC-EXTRA-2", 1,
                new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)));
            await seed.SaveChangesAsync();
        }

        var order = new List<string>();
        var progress = new RecordingProgress();

        await using var db = builder.CreateContext();

        var job = new RecalculationJob(
            db, new RecordingRunner(order), RunHandler(), FormulaService(Rows(order)),
            new TestClock(DateTime.UtcNow));

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: 0,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: null);

        await job.ExecuteAsync(request, progress, CancellationToken.None);

        // ⚠ Одного документа замало для розбору: коли їх кілька, повідомлення
        // мусить називати, ПРО ЯКИЙ документ саме йдеться.
        Assert.Contains(progress.Reports, r => r.Message!.Contains("Документ ", StringComparison.Ordinal));

        // ⚠ Жоден звіт не йде назад навіть коли документів кілька.
        Assert.Equal(progress.Reports.Select(r => r.Percent).Order(), progress.Reports.Select(r => r.Percent));
    }

    /// <summary>Завдання на перерахунок цього документа за його період.</summary>
    private static RecalculationRequest Request(TestDocument document)
        => new(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: null);

    /// <summary>Джерело рядків, яке відмічає, що фаза формул почалася.</summary>
    /// <remarks>
    /// ⚠ <c>GetTableInstancesAsync</c> — ПЕРШЕ, що робить
    /// <c>RecalculationService.RecalculateAllAsync</c>. Порожній перелік
    /// означає «рахувати нема де», і фаза чесно завершується нулем комірок:
    /// цей тест про ПОРЯДОК фаз, а не про числа всередині них.
    /// </remarks>
    private static IRowStore Rows(List<string> order)
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableInstanceRef>>([]))
            .AndDoes(_ => order.Add(Formulas));

        return rows;
    }

    /// <summary>Служба перерахунку формул шаблону над підставними портами.</summary>
    private static RecalculationService FormulaService(IRowStore rows)
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        return new(
            Substitute.For<ICellStore>(),
            rows,
            Substitute.For<Ecr.Application.Ports.IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<Ecr.Application.Ports.IUnitOfWork>());
    }

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<Ecr.Application.Ports.IPeriodStore>(),
            Substitute.For<Ecr.Application.Ports.IWorkflowStore>(),
            Substitute.For<Ecr.Application.Ports.ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<Ecr.Application.Ports.IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            new TestClock(DateTime.UtcNow));

    /// <summary>Оркестратор, що відмічає свій виклик у журналі порядку.</summary>
    private sealed class RecordingRunner(List<string> order) : ICalculationRunner
    {
        /// <summary>Відсоток, який оркестратор повідомляє про себе.</summary>
        public int? ReportPercent { get; init; }

        /// <summary>Документи, для яких оркестратор справді був викликаний.</summary>
        public List<long> DocumentIds { get; } = [];

        public async Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            order.Add(Methodologies);
            DocumentIds.Add(documentId);

            if (ReportPercent is { } percent)
            {
                await progress.ReportAsync(percent, "Пакет 1 із 2", ct).ConfigureAwait(false);
            }

            return new ModuleProfile();
        }
    }

    /// <summary>Канал прогресу, що запам'ятовує все, що йому сказали.</summary>
    private sealed class RecordingProgress : IJobProgress
    {
        public List<(int Percent, string? Message)> Reports { get; } = [];

        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            Reports.Add((percent, message));

            return Task.CompletedTask;
        }
    }

    /// <summary>Канал прогресу, що нічого не робить.</summary>
    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
