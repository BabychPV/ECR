// tests/Ecr.Calculations.Tests/CategoryRuleLookupEntryIdTests.cs
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
/// L2-3 (регресія Land RC12): правило категорії у <c>Legacy</c>-версії бачить id запису довідника
/// <c>Lookup</c>-колонки (число), а формули версії — як і раніше <c>Null</c> (D-161 для них діє).
/// </summary>
/// <remarks>
/// Було: <c>CalculationInputBuilder</c> не віддавав <c>EntryId</c> у Legacy, <c>@Fuel</c> у правилі = <c>Null</c>,
/// правило давало порожній ключ (<c>categoryRuleFailed</c>), рядок відхилявся.
/// Мутація 1: у <c>CalculationInputBuilder</c> повернути <c>entryRefs ? … : null</c> — перший тест червоний.
/// Мутація 2: у <c>GenericCalculationModule</c> віддати <c>EntryId</c> і формулам Legacy — другий тест червоний.
/// </remarks>
public sealed class CategoryRuleLookupEntryIdTests
{
    private const long DocumentId = 700;
    private const long TableInstance = 500;
    private const int TemplateVersion = 3;
    private const int Period = 202601;
    private const int FuelColumn = 11;
    private const int Methodology = 42;
    private const int Version = 71;
    private const long DieselEntryId = 5;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Legacy_правило_категорії_бачить_id_запису_Lookup_і_рядок_не_відхиляється()
    {
        var (profile, _) = await RunAsync(NumericMode.Legacy, [DieselEntryId, DieselEntryId]);

        Assert.Empty(profile.RejectedRows);
        Assert.Equal(2, profile.Stats.Single().Rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Legacy_формула_версії_бачить_Lookup_як_Null_D161_не_змінено()
    {
        // Охоронний (зелений до і після фіксу): без правила категорії, лише формула.
        var (_, written) = await RunAsync(NumericMode.Legacy, [DieselEntryId], withRule: false);

        // Формула `@Fuel`: у Legacy аргумент Lookup = Null (D-161), вихід не виробляється; id (5) у формулу не потрапляє.
        Assert.DoesNotContain(written.SelectMany(o => o.Values), v => v.OutputCode == "Total");
    }

    private static async Task<(ModuleProfile Profile, List<CalculationOutput> Written)> RunAsync(
        NumericMode mode, long[] entryIdByRow, bool withRule = true)
    {
        var written = new List<CalculationOutput>();
        using var provider = Arrange(mode, entryIdByRow, written, withRule);
        var orchestrator = new CalculationOrchestrator(
            provider.GetRequiredService<MethodologyResolver>(),
            provider.GetRequiredService<IPeriodStore>(),
            provider.GetRequiredService<IServiceScopeFactory>());

        var profile = await orchestrator.RunAsync(
            calculationRunId: 1,
            DocumentId,
            new PeriodKey(Period),
            [new CalculationBindingRef(TableInstance, Methodology)],
            NoOpProgress.Instance,
            CancellationToken.None);

        return (profile, written);
    }

    private static ServiceProvider Arrange(NumericMode mode, long[] entryIdByRow, List<CalculationOutput> written, bool withRule)
    {
        var store = Substitute.For<IMethodologyStore>();
        var periods = Substitute.For<IPeriodStore>();

        var formula = new MethodologyFormula(Version, EcrCode.Create("Total"), "@Fuel");
        formula.SetEvaluationOrder(1);
        formula.SetScope(MethodologyFormulaScope.Row);
        var output = new MethodologyOutput(Version, EcrCode.Create("Total"), unitId: 1);
        output.SetPerSubstance(false);

        store.GetPublishedVersionsAsync(Methodology, Arg.Any<CancellationToken>())
             .Returns([PublishedVersion(Methodology, Version, mode)]);
        store.GetRulesAsync(Version, Arg.Any<CancellationToken>())
             .Returns([new MethodologyRule(Version, EcrCode.Create("RULE"), "{}", priority: 10)]);
        store.GetFormulasAsync(Version, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetSubstancesAsync(Version, Arg.Any<CancellationToken>()).Returns([]);
        store.GetOutputsAsync(Version, Arg.Any<CancellationToken>())
             .Returns([output]);

        // Правило порівнює id запису довідника з числом: у Legacy раніше @Fuel = Null -> гілка ' ' (порожній ключ після Trim) -> categoryRuleFailed.
        store.GetCategoryRuleAsync(Version, Arg.Any<CancellationToken>()).Returns(withRule ? "if(@Fuel = 5, 'Diesel', ' ')" : null);

        periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(TableInstance, DocumentId, 3, TemplateVersion, Period));
        var rowIds = new Dictionary<string, long>();
        var records = new List<CellRecord>();
        for (var i = 0; i < entryIdByRow.Length; i++)
        {
            rowIds[$"R{i:000}"] = 1000 + i;
            records.Add(new CellRecord(
                new CellAddress(new PeriodKey(Period), 1000 + i, FuelColumn),
                TableDefId: 3,
                new CellValueData { ValueRegistryEntryId = entryIdByRow[i] }));
        }

        rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(rowIds);

        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(records);

        var fuel = new ColumnDef(
            tableDefId: 3,
            EcrCode.Create("Fuel"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Fuel" }),
            ordinal: 0,
            CellDataType.Lookup);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersion, 0, [],
                new Dictionary<int, ColumnDef> { [FuelColumn] = fuel },
                new Dictionary<(int TableDefId, string RowKey), RowDef>()));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var results = Substitute.For<ICalculationResultStore>();
        results.WriteResultsAsync(Arg.Any<long>(), Arg.Any<IReadOnlyList<CalculationOutput>>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   written.AddRange((IReadOnlyList<CalculationOutput>)call[1]);
                   return Task.CompletedTask;
               });

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(periods);
        services.AddSingleton(rows);
        services.AddSingleton(cells);
        services.AddSingleton(metadata);
        services.AddSingleton(units);
        services.AddSingleton(Substitute.For<IConstantStore>());
        services.AddSingleton(Substitute.For<ICalculationBindingStore>());
        services.AddSingleton(results);
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

    private static MethodologyVersion PublishedVersion(int methodologyId, int versionId, NumericMode mode)
    {
        var utcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var version = new MethodologyVersion(
            methodologyId, "1.0.0.0", CalculationLevel.Configuration, createdByUserId: 1, utcNow);

        version.SetModes(mode, version.CalendarMode, version.TraceLevel);
        version.Publish(
            publishedByUserId: 2,
            changeReason: "стенд тесту",
            effectiveFrom: new DateOnly(2025, 1, 1),
            testsPassed: true,
            utcNow);

        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, versionId);
        return version;
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
