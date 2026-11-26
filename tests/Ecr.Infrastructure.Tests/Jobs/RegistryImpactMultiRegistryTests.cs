// tests/Ecr.Infrastructure.Tests/Jobs/RegistryImpactMultiRegistryTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ SGF-flaring AC-3: задача «довідники правки × рік» перераховує лише документи, методології яких читають змінені довідники
/// складу (сховище залежних — за <c>cfg.RegistryUse</c>), і КОЖЕН документ × період — один раз, навіть якщо він читає і кейс,
/// і склад. Без бази: сховище й тригер підставні.
/// </summary>
public sealed class RegistryImpactMultiRegistryTests
{
    private const int Case = 81;
    private const int Composition = 82;
    private const long ReadsBoth = 1001;
    private const long ReadsCaseOnly = 1002;
    private const long ReadsNeither = 1003;
    private const long ReadsCompositionOnly = 1004;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-3")]
    public async Task Одна_постановка_кейс_і_склад__кожен_залежний_документ_період_раз__незалежний_жодного()
    {
        var (impact, trigger) = Stores();

        await new RegistryImpactRecalculationJob(impact, trigger).ExecuteAsync(
            new RegistryImpactRecalculationRequest(Case, [], "gas version", 2026, AlsoRegistryDefIds: [Composition]),
            Substitute.For<IJobProgress>(), CancellationToken.None);

        await trigger.Received(1).RequestDependentsAsync(ReadsBoth, new PeriodKey(202603), Arg.Any<CancellationToken>());
        await trigger.Received(1).RequestDependentsAsync(ReadsBoth, new PeriodKey(202604), Arg.Any<CancellationToken>());
        await trigger.Received(1).RequestDependentsAsync(ReadsCaseOnly, new PeriodKey(202603), Arg.Any<CancellationToken>());
        await trigger.Received(1).RequestDependentsAsync(ReadsCompositionOnly, new PeriodKey(202605), Arg.Any<CancellationToken>());
        await trigger.DidNotReceive().RequestDependentsAsync(ReadsNeither, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        Assert.Equal(4, trigger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ICalculationTrigger.RequestDependentsAsync)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-3")]
    public async Task Стара_форма_payload_без_інших_довідників__лише_свій_довідник()
    {
        var (impact, trigger) = Stores();

        await new RegistryImpactRecalculationJob(impact, trigger).ExecuteAsync(
            new RegistryImpactRecalculationRequest(Composition, [], "old payload", 2026),
            Substitute.For<IJobProgress>(), CancellationToken.None);

        await impact.DidNotReceive().ListImpactedForYearAsync(Case, Arg.Any<int>(), Arg.Any<CancellationToken>());
        await trigger.DidNotReceive().RequestDependentsAsync(ReadsCaseOnly, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await trigger.Received(1).RequestDependentsAsync(ReadsBoth, new PeriodKey(202603), Arg.Any<CancellationToken>());
    }

    private static (IRegistryImpactStore Impact, ICalculationTrigger Trigger) Stores()
    {
        var impact = Substitute.For<IRegistryImpactStore>();

        // Залежні за cfg.RegistryUse: документ, що читає обидва довідники, приходить з ОБОХ (по рядку на методологію).
        impact.ListImpactedForYearAsync(Case, 2026, Arg.Any<CancellationToken>()).Returns(
        [
            Row(ReadsBoth, 202603, "FLERT_INT_SG"), Row(ReadsBoth, 202604, "FLERT_INT_SG"), Row(ReadsCaseOnly, 202603, "CASE_ONLY"),
        ]);
        impact.ListImpactedForYearAsync(Composition, 2026, Arg.Any<CancellationToken>()).Returns(
        [
            Row(ReadsBoth, 202603, "FLERT_INT_SG"), Row(ReadsBoth, 202604, "FLERT_INT_SG"), Row(ReadsBoth, 202603, "TOX_SG"),
            Row(ReadsCompositionOnly, 202605, "COMPOSITION_ONLY"),
        ]);

        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestDependentsAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns("job");
        return (impact, trigger);
    }

    private static RegistryImpactRow Row(long document, int period, string methodology)
        => new(document, $"DOC{document}", 1, period, PeriodState.Open, methodology);
}
