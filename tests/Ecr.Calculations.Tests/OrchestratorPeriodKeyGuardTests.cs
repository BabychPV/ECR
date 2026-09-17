// tests/Ecr.Calculations.Tests/OrchestratorPeriodKeyGuardTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Оркестратор відмовляє ключу, який не є періодом, — і КАЖЕ, у чому річ.
/// </summary>
/// <remarks>
/// ⛔ Чому цей сторож існує. <c>RecalculationJob</c> для прогону «на весь рік»
/// (<c>PeriodKey = null</c> — нічний розклад) віддавав сюди
/// <c>new PeriodKey(0)</c>. Оркестратор ішов із цим нулем у
/// <c>IPeriodStore.FindPeriodBoundsAsync</c>, не знаходив меж і кидав
/// <c>ECR-PRD-0404</c>: «періоду 0 для документа N не існує». Твердження було
/// правдиве й водночас вказувало НЕ НА ТОГО: воно описувало неіснуючий період,
/// тобто виглядало як зіпсований календар документа, тоді як зламаний був
/// викликач, який не розклав рік на періоди. Помилка, що називає не той
/// суб'єкт, коштує дорожче за відсутню: за нею йдуть шукати не туди.
///
/// ⚠ Сторож лишається потрібним і після того, як задачу виправлено: межа
/// контракту належить тому, хто її вимагає. Оркестратор — єдиний, хто знає, що
/// йому потрібен САМЕ період (версія методології резолвиться за ДАТОЮ періоду),
/// а не будь-яке ціле число.
/// </remarks>
public sealed class OrchestratorPeriodKeyGuardTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Ключ_що_не_є_періодом_відмовляється_названою_причиною()
    {
        var periods = Substitute.For<IPeriodStore>();
        var orchestrator = new CalculationOrchestrator(
            new MethodologyResolver(
                Substitute.For<IMethodologyStore>(),
                Substitute.For<ICellStore>(),
                Substitute.For<IRowStore>()),
            periods,
            Substitute.For<IServiceScopeFactory>());

        // ⚠ Прив'язка ОБОВ'ЯЗКОВА: на порожньому списку оркестратор законно
        // повертається одразу, нічого не рахуючи, — і саме тому дефект прожив
        // так довго (проєкт без методологій нічної помилки не бачив).
        var thrown = await Record.ExceptionAsync(() => orchestrator.RunAsync(
            calculationRunId: 1,
            documentId: 700,
            new PeriodKey(0),
            [new CalculationBindingRef(TableInstanceId: 500, MethodologyId: 42)],
            NoOpProgress.Instance,
            CancellationToken.None));

        var error = Assert.IsType<ArgumentOutOfRangeException>(thrown);
        Assert.Equal("periodKey", error.ParamName);
        Assert.Equal(0, error.ActualValue);

        // ⛔ Причина названа словами, а не лише типом винятку: у повідомленні
        // мусить стояти рік, який не розклали на періоди, бо саме це читає той,
        // хто розбирає нічне падіння.
        Assert.Contains("ВЕСЬ РІК", error.Message, StringComparison.Ordinal);

        // ⛔ І головне — у сховище періодів по неіснуючий період НІХТО не
        // ходив: старе `ECR-PRD-0404` народжувалося саме там, і поки цей
        // виклик існує, помилка знову називатиме не того суб'єкта.
        await periods.DidNotReceiveWithAnyArgs()
            .FindPeriodBoundsAsync(default, default, default);
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
