// tests/Ecr.Calculations.Tests/MethodologyReadsPerBindingTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Склад версії методології читається РАЗ НА ПРИВ'ЯЗКУ, а не на кожен рядок
/// таблиці (`CAL-06`).
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>GenericCalculationModule.ExecuteAsync</c> сам ходив по
/// формули, речовини й виходи версії та по межі періоду — чотири звернення, —
/// а оркестратор кликав його на КОЖЕН вхідний рядок. Відповідь на всі чотири
/// питання в межах прив'язки «методологія × період» не змінюється за
/// побудовою: версія опублікована, період той самий. На таблиці в 300 рядків
/// це 1200 запитів по одну й ту саму відповідь, і жодна перевірка цього не
/// бачила — прогін давав правильні числа, просто робив це вчетверо-довше
/// потрібного.
///
/// ⚠ Тест міряє прогін ЦІЛКОМ, через <see cref="CalculationOrchestrator"/>, а
/// не модуль окремо. Перевірити сам модуль означало б довести, що він **вміє**
/// прийняти готовий контекст, — а вада була в тому, хто його кличе.
/// </remarks>
public sealed class MethodologyReadsPerBindingTests
{
    private const int MethodologyId = 42;
    private const int VersionId = 71;
    private const long DocumentId = 700;
    private const long TableInstance = 500;
    private const int TemplateVersion = 3;
    private const int Period = 202601;
    private const int Rows = 100;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Прогін_на_сто_рядків_читає_версію_один_раз_а_не_сто()
    {
        var store = Substitute.For<IMethodologyStore>();
        var periods = Substitute.For<IPeriodStore>();
        var provider = Arrange(store, periods);

        var profile = await Run(provider, periods);

        // ⛔ Головне: рядків справді було сто. Без цієї перевірки «одне
        // читання» досягалося б і тим, що прогін не порахував нічого, — і
        // тест підтверджував би зламаний продукт.
        Assert.Equal(Rows, profile.Stats.Single().Rows);

        // ⛔ Три читання складу версії — РІВНО ПО ОДНОМУ. До `CAL-06` тут було
        // по сто.
        await store.Received(1).GetFormulasAsync(VersionId, Arg.Any<CancellationToken>());
        await store.Received(1).GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>());
        await store.Received(1).GetOutputsAsync(VersionId, Arg.Any<CancellationToken>());

        // ⛔ Межі періоду — двічі на прогін, і обидва рази названі: один похід
        // робить оркестратор (дата, на яку резолвиться версія, ФВ-9.3), один —
        // підготовка контексту прив'язки (тривалість періоду, ФВ-16.11). До
        // `CAL-06` другий із них множився на кількість рядків.
        await periods.Received(2).FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Чужий_контекст_прив_язки_відхиляється_а_не_рахується()
    {
        // ⛔ Ціна відсутності цієї перевірки — не виняток, а ПРАВДОПОДІБНЕ
        // ЧИСЛО: рядок порахувався б формулами іншої версії або поділився б
        // на дні іншого періоду, і побачити це можна було б лише звіркою з
        // чинною системою через місяці (той самий клас дефекту, що `D-112`).
        var store = Substitute.For<IMethodologyStore>();
        var periods = Substitute.For<IPeriodStore>();
        var provider = Arrange(store, periods);

        using var scope = provider.CreateScope();
        var module = scope.ServiceProvider.GetRequiredService<ICalculationModule>();

        var descriptor = Descriptor();
        var context = await module.PrepareAsync(
            descriptor, DocumentId, new PeriodKey(Period), CancellationToken.None);

        var foreignRow = new CalculationInput(
            descriptor,
            DocumentId: DocumentId + 1,
            TableInstanceId: TableInstance,
            PeriodKey: new PeriodKey(Period),
            SourceRowKey: "R000",
            Arguments: []);

        var thrown = await Assert.ThrowsAsync<ArgumentException>(
            () => module.ExecuteAsync(context, foreignRow, CancellationToken.None));

        Assert.Equal("binding", thrown.ParamName);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Прив_язка_без_жодного_рядка_не_читає_версію_взагалі()
    {
        // ⚠ Контрольний випадок до першого тесту: «раз на прив'язку» не сміє
        // перетворитися на «завжди раз». Порожній набір рядків коштував нуль
        // звернень і до зміни (цикл просто не виконувався) — і має коштувати
        // нуль після неї.
        var store = Substitute.For<IMethodologyStore>();
        var periods = Substitute.For<IPeriodStore>();
        var provider = Arrange(store, periods, rowCount: 0);

        var profile = await Run(provider, periods);

        Assert.Equal(0, profile.Stats.Single().Rows);

        await store.DidNotReceiveWithAnyArgs().GetFormulasAsync(default, default);
        await store.DidNotReceiveWithAnyArgs().GetSubstancesAsync(default, default);
        await store.DidNotReceiveWithAnyArgs().GetOutputsAsync(default, default);
        await periods.Received(1).FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>());
    }

    /// <summary>Запускає прогін по одній прив'язці.</summary>
    private static async Task<ModuleProfile> Run(ServiceProvider provider, IPeriodStore periods)
    {
        var orchestrator = new CalculationOrchestrator(
            provider.GetRequiredService<MethodologyResolver>(),
            periods,
            provider.GetRequiredService<IServiceScopeFactory>());

        return await orchestrator.RunAsync(
            calculationRunId: 1,
            DocumentId,
            new PeriodKey(Period),
            [new CalculationBindingRef(TableInstance, MethodologyId)],
            NoOpProgress.Instance,
            CancellationToken.None);
    }

    /// <summary>
    /// Стенд прогону: справжні резолвер, збирач входів, модуль і писар —
    /// підмінені лише сховища.
    /// </summary>
    /// <remarks>
    /// ⚠ Контейнер тут не церемонія: оркестратор бере залежності з ВЛАСНОГО
    /// scope на кожну гілку пакета (<c>Q-249</c>), і підміна
    /// <c>IServiceScopeFactory</c> заглушкою означала б, що тест не проходить
    /// тим шляхом, яким іде прогін.
    /// </remarks>
    private static ServiceProvider Arrange(
        IMethodologyStore store, IPeriodStore periods, int rowCount = Rows)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Total"), "1 + 1");
        formula.SetEvaluationOrder(1);

        store.GetPublishedVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
             .Returns([PublishedVersion()]);
        store.GetRulesAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyRule(VersionId, EcrCode.Create("ALL"), "{}", priority: 10)]);
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Total"), unitId: 1)]);

        periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(TableInstance, DocumentId, 3, TemplateVersion, Period));
        rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, rowCount).ToDictionary(i => $"R{i:D3}", i => (long)(1000 + i)));

        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns([]);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersion, 0, [],
                new Dictionary<int, ColumnDef>(),
                new Dictionary<(int TableDefId, string RowKey), RowDef>()));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(periods);
        services.AddSingleton(rows);
        services.AddSingleton(cells);
        services.AddSingleton(metadata);
        services.AddSingleton(units);
        services.AddSingleton(Substitute.For<IConstantStore>());
        services.AddSingleton(Substitute.For<ICalculationResultStore>());
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton<IFormulaEngine, RealFormulaEngine>();
        services.AddSingleton<CalendarContext>();
        services.AddScoped<ConstantResolver>();
        services.AddScoped<MethodologyResolver>();
        services.AddScoped<CalculationInputBuilder>();
        services.AddScoped<CalculationOutputWriter>();
        services.AddScoped<ICalculationModule, GenericCalculationModule>();

        return services.BuildServiceProvider();
    }

    private static MethodologyDescriptor Descriptor()
        => new(
            MethodologyId,
            VersionId,
            Code: "1.0.0.0",
            VersionNumber: "1.0.0.0",
            CalculationLevel.Configuration,
            NumericMode.Legacy,
            CalendarMode.Actual,
            TraceLevel.ErrorsOnly);

    /// <summary>Опублікована версія з проставленим ідентифікатором.</summary>
    /// <remarks>
    /// ⚠ Ідентифікатор проставляється рефлексією: його дає база, а тесту
    /// потрібна саме та версія, на яку налаштовані решта підмін. Публікація —
    /// справжня (<c>Publish</c>), бо резолвер відбирає версії за
    /// <c>EffectiveFrom</c>, і чернетка сюди просто не дійшла б.
    /// </remarks>
    private static MethodologyVersion PublishedVersion()
    {
        var utcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var version = new MethodologyVersion(
            MethodologyId, "1.0.0.0", CalculationLevel.Configuration, createdByUserId: 1, utcNow);

        version.Publish(
            publishedByUserId: 2,
            changeReason: "стенд тесту",
            effectiveFrom: new DateOnly(2025, 1, 1),
            testsPassed: true,
            utcNow);

        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, VersionId);

        return version;
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
