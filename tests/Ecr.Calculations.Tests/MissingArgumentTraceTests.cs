// tests/Ecr.Calculations.Tests/MissingArgumentTraceTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Трейс невдалого розрахунку розрізняє «аргумент не знайдено взагалі» від
/// «аргумент є, а значення легітимно <c>null</c>» (друга лінія захисту поруч
/// із публікаційною перевіркою <c>ECR-CALC-0438</c>).
/// </summary>
/// <remarks>
/// ⛔ До фіксу обидва випадки давали ту саму <c>null</c> у
/// <c>MethodologyEvaluationContext.GetArgument</c>, і трейс невдалого виходу
/// показував один і той самий <c>#NULL</c> — той, хто дивиться на трейс, не
/// міг відрізнити «таблицю змінили ПІСЛЯ публікації, колонки більше немає» від
/// «клітинку легітимно лишили порожньою».
/// </remarks>
public sealed class MissingArgumentTraceTests
{
    private const int VersionId = 71;
    private const long DocumentId = 800;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Відсутній_аргумент_дає_в_трейсі_ARG_а_не_NULL()
    {
        var output = await RunAsync(argumentPresent: false);

        // ⚠ Формула «Total» — водночас і вихід «Total» (проста фікстура), тож
        // трейс несе ДВА кроки з тим самим кодом: крок формули (`Evaluate`) і
        // крок виходу без числа (`ExecuteAsync`) — обидва мають нести ОДИН і
        // той самий, розрізнюваний код помилки.
        var steps = output.Trace.Where(s => s.StepCode == "Total").ToList();
        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.Equal(ExpressionErrors.ArgumentNotFound, s.TraceJson));
        Assert.All(steps, s => Assert.NotEqual("#NULL", s.TraceJson));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Легітимно_порожній_аргумент_дає_в_трейсі_NULL_а_не_ARG()
    {
        // ⚠ Контрольний випадок: та сама формула, той самий вихід, але
        // аргумент присутній у наборі — клітинку просто лишили порожньою
        // (Value і ValueString обидва null). Це НЕ структурна вада, і трейс
        // не повинен показувати те саме, що для відсутнього аргументу.
        var output = await RunAsync(argumentPresent: true);

        var step = Assert.Single(output.Trace, s => s.StepCode == "Total");
        Assert.Equal("#NULL", step.TraceJson);
        Assert.NotEqual(ExpressionErrors.ArgumentNotFound, step.TraceJson);
    }

    /// <summary>Виконує методологію з формулою <c>@Jan + 1</c>.</summary>
    /// <param name="argumentPresent">
    /// <c>true</c> — аргумент <c>Jan</c> зібраний, але з порожнім значенням
    /// (легітимна порожня клітинка); <c>false</c> — аргументу немає в наборі
    /// взагалі (структурна вада: колонки з таким кодом немає в таблиці).
    /// </param>
    private static async Task<CalculationOutput> RunAsync(bool argumentPresent)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Total"), "@Jan + 1");
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Total"), unitId: 1)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            Units(),
            Periods());

        var descriptor = new MethodologyDescriptor(
            MethodologyId: 9,
            MethodologyVersionId: VersionId,
            Code: "TRACE_TEST",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Strict,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.ErrorsOnly);

        var arguments = argumentPresent
            ? new List<CalculationArgument> { new("Jan", null, null, null) }
            : [];

        var input = new CalculationInput(
            descriptor,
            DocumentId,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "7001001",
            Arguments: arguments);

        return await module.ExecuteAsync(input, CancellationToken.None);
    }

    private static IPeriodStore Periods()
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        return periods;
    }

    private static IUnitCatalog Units()
    {
        var catalog = Substitute.For<IUnitCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["kg"] = new(1, "kg", 1, 1m),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        return catalog;
    }
}
