// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobRegistryAsOfTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Задача перерахунку передає оркестратору момент знімка довідників СВОГО прогону —
/// <c>CalculationRun.RegistryAsOfUtc</c> (RT-23a, FEATURE-REGISTRY-TABLES §5.7, AC-7).
/// </summary>
/// <remarks>
/// ⛔ Без цього оркестратор читав би довідники на поточний момент, і повтор прогону
/// після правки складу дав би інші числа — саме те, що <c>RegistryAsOfUtc</c> мав
/// унеможливити (<c>D-158</c>). Другу половину ланцюжка — що оркестратор вантажить
/// знімок саме на цей момент — доводить <c>Hse301GoldenTests.Прогін_читає_знімок_один_раз_на_момент_прогону</c>.
///
/// ⚠ На реальному SQL: момент порівнюється зі збереженим у <c>calc.CalculationRun</c>
/// (<c>datetime2(3)</c>), а не з годинником тесту.
///
/// Мутація: передавати в оркестратор <c>null</c> замість <c>run.RegistryAsOfUtc</c> —
/// тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobRegistryAsOfTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Методології_рахуються_на_момент_знімка_прогону()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: null);

        var started = new DateTime(2026, 2, 3, 10, 15, 30, 123, DateTimeKind.Utc);
        var runner = new RecordingRunner();

        await using (var db = builder.CreateContext())
        {
            var job = new RecalculationJob(db, runner, RunHandler(), Formulas(), new TestClock(started));
            await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);
        }

        await using var verify = builder.CreateContext();
        var stored = await verify.CalculationRuns
            .AsNoTracking()
            .Where(r => r.ProjectId == document.ProjectId)
            .Select(r => r.RegistryAsOfUtc)
            .SingleAsync();

        Assert.Equal(started, stored);

        // Оркестратор викликано на названий період — і щоразу з моментом прогону.
        Assert.NotEmpty(runner.Moments);
        Assert.All(runner.Moments, moment => Assert.Equal(stored, moment));
    }

    /// <summary>Служба перерахунку формул шаблону над підставними портами.</summary>
    private static Ecr.Application.Recalculation.RecalculationService Formulas()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var header = Substitute.For<IDocumentHeaderStore>();
        header.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<string, ExpressionValue>());

        return new(
            Substitute.For<ICellStore>(),
            Substitute.For<IRowStore>(),
            Substitute.For<IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<IRegistryStore>(),
            header,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<ISheetEditGate>());
    }

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            Substitute.For<ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            new TestClock(DateTime.UtcNow),
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>());

    /// <summary>Оркестратор, що запам'ятовує момент знімка кожного виклику.</summary>
    /// <remarks>
    /// ⚠ Перевантаження без моменту записує окреме значення-маркер: якщо задача
    /// кличе саме його, момент загублено, і тест мусить це побачити.
    /// </remarks>
    private sealed class RecordingRunner : ICalculationRunner
    {
        private static readonly DateTime NoMoment = DateTime.MinValue;

        public List<DateTime?> Moments { get; } = [];

        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            Moments.Add(NoMoment);
            return Task.FromResult(new ModuleProfile());
        }

        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress,
            DateTime? registryAsOfUtc, CancellationToken ct)
        {
            Moments.Add(registryAsOfUtc);
            return Task.FromResult(new ModuleProfile());
        }
    }

    /// <summary>Канал прогресу, що нічого не робить.</summary>
    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
