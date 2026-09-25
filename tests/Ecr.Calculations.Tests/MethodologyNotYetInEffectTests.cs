// tests/Ecr.Calculations.Tests/MethodologyNotYetInEffectTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Період раніше за першу чинну версію методології (F-04, четвертий раунд UX).
/// </summary>
/// <remarks>
/// ⛔ Відтворено на стенді: перерахунок документа 19 за 202504 падав цілком —
/// «Методологія 2 не має версії, чинної на 2025-12-31», — хоча методологія
/// запроваджена з 2026 року і для 2025-го просто ще не існувала. Рішення:
/// такий період методологія пропускає (порожня комірка, а не відмова), а
/// методологія БЕЗ жодної опублікованої версії лишається відмовою — англійською
/// і з ключем.
/// </remarks>
public sealed class MethodologyNotYetInEffectTests
{
    private const int MethodologyId = 42;

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IServiceScopeFactory _scopes = Substitute.For<IServiceScopeFactory>();

    /// <remarks>
    /// Мутація: повернути в <c>MethodologyResolver.ResolveVersionAsync</c> відмову
    /// на <c>version is null</c> — прогін падає з <c>ECR-CALC-0422</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Період_до_першої_версії_пропускається_а_не_валить_прогін()
    {
        _store.GetPublishedVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns([Published(new DateOnly(2026, 1, 1))]);
        _periods.FindPeriodBoundsAsync(700, 202512, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31)));

        var profile = await Orchestrator().RunAsync(
            calculationRunId: 1, documentId: 700, new PeriodKey(202512),
            [new CalculationBindingRef(TableInstanceId: 500, MethodologyId: MethodologyId)],
            NoOpProgress.Instance, CancellationToken.None);

        Assert.NotNull(profile);

        // Нічого не виконувалося: жодної гілки пакета (кожна просить свій scope).
        _scopes.DidNotReceiveWithAnyArgs().CreateScope();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Чинна_на_дату_версія_резолвиться_як_і_раніше()
    {
        var version = Published(new DateOnly(2026, 1, 1));
        _store.GetPublishedVersionsAsync(MethodologyId, Arg.Any<CancellationToken>()).Returns([version]);

        var descriptor = await Resolver().ResolveVersionAsync(
            MethodologyId, new DateOnly(2026, 1, 31), CancellationToken.None);

        Assert.NotNull(descriptor);
        Assert.Equal(version.Id, descriptor!.MethodologyVersionId);
    }

    /// <remarks>
    /// ⚠ Межа рішення: прив'язана методологія без ЖОДНОЇ опублікованої версії —
    /// незавершена конфігурація, і мовчазний пропуск сховав би її назавжди.
    /// Текст винятку йде в журнал задачі як є, тому — англійською.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Методологія_без_опублікованих_версій_відмовляє_англійською_з_ключем()
    {
        _store.GetPublishedVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns(new List<MethodologyVersion>());

        var error = await Assert.ThrowsAsync<DomainException>(() => Resolver().ResolveVersionAsync(
            MethodologyId, new DateOnly(2026, 1, 31), CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.noPublishedVersion", error.Details!["messageKey"]);
        Assert.Equal("42", error.Details!["methodologyId"]);
        Assert.StartsWith("Methodology 42 ", error.Message, StringComparison.Ordinal);
    }

    private MethodologyResolver Resolver()
        => new(_store, Substitute.For<ICellStore>(), Substitute.For<IRowStore>());

    private CalculationOrchestrator Orchestrator() => new(Resolver(), _periods, _scopes);

    private static MethodologyVersion Published(DateOnly effectiveFrom)
    {
        var version = new MethodologyVersion(
            MethodologyId, "1.0", CalculationLevel.Configuration, createdByUserId: 7,
            new DateTime(2025, 11, 1, 9, 0, 0, DateTimeKind.Utc));
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, 301);

        version.Publish(9, "R4 B1", effectiveFrom, testsPassed: true,
            new DateTime(2025, 11, 2, 9, 0, 0, DateTimeKind.Utc));

        return version;
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
