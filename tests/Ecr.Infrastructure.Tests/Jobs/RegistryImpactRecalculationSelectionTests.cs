// tests/Ecr.Infrastructure.Tests/Jobs/RegistryImpactRecalculationSelectionTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// AN-105 / D2-04: задача «перерахувати зачеплені довідником» звіряє саме ВИБРАНІ документи, а не
/// першу тисячу рядків глобального переліку.
/// </summary>
/// <remarks>
/// ⚠ Сховище — підробка, що поводиться як справжнє: глобальний перелік (<c>ListImpactedAsync</c>)
/// віддає рівно <see cref="IRegistryImpactStore.MaxRows"/> рядків ІНШИХ документів із меншими Id, а
/// вибраний документ є лише поза стелею. 1001 справжній документ у спільній базі заради цього —
/// хвилини підготовки без жодної додаткової перевірки: запит звуження перевіряють
/// <see cref="RegistryImpactRecalculationJobTests"/> на SQL Server.
/// </remarks>
public sealed class RegistryImpactRecalculationSelectionTests
{
    private const int RegistryId = 77;
    private const long Chosen = 50_000;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "D2-04")]
    public async Task Документ_поза_першою_тисячею_глобального_переліку_ставиться_на_перерахунок()
    {
        var impact = Substitute.For<IRegistryImpactStore>();

        // Перша тисяча глобального переліку — чужі документи з меншими Id.
        impact.ListImpactedAsync(RegistryId, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RegistryImpactRow>>(
                [.. Enumerable.Range(1, IRegistryImpactStore.MaxRows).Select(i => Row(i, 202601))]));

        // Звуження до вибраних — вибраний документ у двох відкритих періодах.
        impact.ListImpactedForDocumentsAsync(
                RegistryId, Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new[] { Chosen })),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RegistryImpactRow>>(
                [Row(Chosen, 202601), Row(Chosen, 202601, "M2"), Row(Chosen, 202602)]));

        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("recalc-1"));

        var progress = Substitute.For<IJobProgress>();
        string? done = null;
        progress.ReportAsync(100, Arg.Do<string?>(m => done = m), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await new RegistryImpactRecalculationJob(impact, trigger).ExecuteAsync(
            new RegistryImpactRecalculationRequest(RegistryId, [Chosen], "test"), progress, CancellationToken.None);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути в задачу `ListImpactedAsync(…, projectIds: null, MaxRows, …)` →
        // вибраного документа в першій тисячі немає, `queued = 0`, `gone = 1`, червоний.
        await trigger.Received(1).RequestAsync(Chosen, new PeriodKey(202601), Arg.Any<CancellationToken>());
        await trigger.Received(1).RequestAsync(Chosen, new PeriodKey(202602), Arg.Any<CancellationToken>());
        await trigger.DidNotReceive().RequestAsync(
            Arg.Is<long>(id => id != Chosen), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());

        Assert.NotNull(done);
        Assert.Contains("\"queued\":\"2\"", done, StringComparison.Ordinal);
        Assert.Contains("\"gone\":\"0\"", done, StringComparison.Ordinal);
    }

    private static RegistryImpactRow Row(long documentId, int periodKey, string methodology = "M1")
        => new(documentId, $"D{documentId}", 1, periodKey, PeriodState.Open, methodology);
}
