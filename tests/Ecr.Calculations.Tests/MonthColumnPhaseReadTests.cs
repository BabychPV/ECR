// tests/Ecr.Calculations.Tests/MonthColumnPhaseReadTests.cs
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
/// PSS-08 для модуля «місяці в колонках» (рівень <c>Module</c>, HSE400): кожна прив'язка читала ВСІ таблиці аркуша
/// (рядки й зріз, <c>MonthColumnArgumentsModule.LoadTablesAsync</c>) і перелік екземплярів документа сама. У прогоні
/// оркестратора — раз на виклик, і числа ті самі.
/// </summary>
public sealed class MonthColumnPhaseReadTests
{
    private const long DocumentId = 902;
    private const int TemplateVersion = 9;
    private const int Period = 202601;
    private const long InstanceA = 611;
    private const long InstanceB = 612;
    private const int TableA = 41;
    private const int TableB = 42;
    private const int OneUnit = 7;

    private static readonly Dictionary<string, long> RowsA = new(StringComparer.Ordinal)
    {
        ["Methane"] = 1, ["F_Sum"] = 2, ["F_Triple"] = 3,
    };

    private static readonly Dictionary<string, long> RowsB = new(StringComparer.Ordinal) { ["Flow"] = 11, ["F_Scaled"] = 12 };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "PSS-08")]
    public async Task Дві_прив_язки_рівня_Module_читають_таблиці_аркуша_раз_на_виклик()
    {
        var rows = Substitute.For<IRowStore>();
        var cells = Substitute.For<ICellStore>();
        var results = Substitute.For<ICalculationResultStore>();
        var periods = Substitute.For<IPeriodStore>();
        var provider = Arrange(rows, cells, results, periods);

        await new CalculationOrchestrator(
                provider.GetRequiredService<MethodologyResolver>(), periods, provider.GetRequiredService<IServiceScopeFactory>())
            .RunAsync(
                1,
                DocumentId,
                new PeriodKey(Period),
                [new CalculationBindingRef(InstanceA, 40), new CalculationBindingRef(InstanceA, 50)],
                NoOpProgress.Instance,
                CancellationToken.None);

        // Числа: Methane = 10 (M01), 2 (M02) → F_Sum = ×2, F_Triple = ×3.
        var values = results.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICalculationResultStore.WriteResultsAsync))
            .SelectMany(c => c.GetArguments())
            .OfType<IEnumerable<CalculationOutput>>()
            .SelectMany(o => o)
            .SelectMany(o => o.Values.Select(v => (o.SourceRowKey, v.OutputCode, v.Value)))
            .OrderBy(v => v.SourceRowKey, StringComparer.Ordinal)
            .ThenBy(v => v.OutputCode, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            [("F_Sum", "M01", 20m), ("F_Sum", "M02", 4m), ("F_Triple", "M01", 30m), ("F_Triple", "M02", 6m)],
            values);

        // До PSS-08: таблиця сусіда TB — по разу на прив'язку, екземпляри документа — теж.
        await cells.Received(1).ReadSliceAsync(InstanceB, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await rows.Received(1).GetRowIdsAsync(InstanceB, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await rows.Received(1).GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());

        // Власна таблиця: зіставлення правил і модуль ділять одне читання.
        await cells.Received(1).ReadSliceAsync(InstanceA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await rows.Received(1).GetRowIdsAsync(InstanceA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    private static ServiceProvider Arrange(
        IRowStore rows, ICellStore cells, ICalculationResultStore results, IPeriodStore periods)
    {
        var store = Substitute.For<IMethodologyStore>();
        foreach (var (methodologyId, versionId, code, factor) in new[] { (40, 400, "X400", 2), (50, 500, "X500", 3) })
        {
            var formula = new MethodologyFormula(
                versionId,
                EcrCode.Create(factor == 2 ? $"{code}_TA_Sum" : $"{code}_TA_Triple"),
                $"@GCV.{code}_TA_Methane * {factor}");
            formula.SetEvaluationOrder(1);

            store.GetPublishedVersionsAsync(methodologyId, Arg.Any<CancellationToken>())
                .Returns([PublishedVersion(methodologyId, versionId)]);
            store.GetRulesAsync(versionId, Arg.Any<CancellationToken>())
                .Returns([new MethodologyRule(versionId, EcrCode.Create("ALL"), "{}", priority: 10)]);
            store.GetFormulasAsync(versionId, Arg.Any<CancellationToken>()).Returns([formula]);
            store.GetOutputsAsync(versionId, Arg.Any<CancellationToken>()).Returns([]);
            store.GetSubstancesAsync(versionId, Arg.Any<CancellationToken>()).Returns([]);
            store.GetConstantsAsync(versionId, Arg.Any<CancellationToken>()).Returns([]);
        }

        periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var tableA = Table(TableA, "TA", firstColumnId: 100);
        var tableB = Table(TableB, "TB", firstColumnId: 200);
        var sheet = new SheetDef(TemplateVersion, EcrCode.Create("S1"), Name("S1"), 1);
        sheet.AddTable(tableA);
        sheet.AddTable(tableB);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            TemplateVersion,
            0,
            [sheet],
            sheet.Tables.SelectMany(t => t.Columns).ToDictionary(c => c.Id),
            new Dictionary<(int TableDefId, string RowKey), RowDef>()));

        var a = new TableInstanceRef(InstanceA, DocumentId, TableA, TemplateVersion, Period);
        var b = new TableInstanceRef(InstanceB, DocumentId, TableB, TemplateVersion, Period);
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([a, b]);
        rows.ResolveTableInstanceAsync(InstanceA, Arg.Any<CancellationToken>()).Returns(a);
        rows.ResolveTableInstanceAsync(InstanceB, Arg.Any<CancellationToken>()).Returns(b);
        rows.GetRowIdsAsync(InstanceA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(RowsA);
        rows.GetRowIdsAsync(InstanceB, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(RowsB);

        // Колонки: 100/200 — M01, 101/201 — M02.
        cells.ReadSliceAsync(InstanceA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            Cell(TableA, RowsA["Methane"], 100, 10m),
            Cell(TableA, RowsA["Methane"], 101, 2m),
        ]);
        cells.ReadSliceAsync(InstanceB, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            Cell(TableB, RowsB["Flow"], 200, 3m),
        ]);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["one"] = new(OneUnit, "one", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(periods);
        services.AddSingleton(rows);
        services.AddSingleton(cells);
        services.AddSingleton(metadata);
        services.AddSingleton(units);
        services.AddSingleton(results);
        services.AddSingleton(Substitute.For<IConstantStore>());
        services.AddSingleton(Substitute.For<ICalculationBindingStore>());
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton<IFormulaEngine, RealFormulaEngine>();
        services.AddSingleton<CalendarContext>();
        services.AddScoped<ConstantResolver>();
        services.AddScoped<MethodologyResolver>();
        services.AddScoped<CalculationInputBuilder>();
        services.AddScoped<CalculationOutputWriter>();
        services.AddScoped<ICalculationModule, MonthColumnArgumentsModule>();

        return services.BuildServiceProvider();
    }

    private static MethodologyVersion PublishedVersion(int methodologyId, int versionId)
    {
        var utcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var version = new MethodologyVersion(methodologyId, "1.0.0.0", CalculationLevel.Module, createdByUserId: 1, utcNow);
        version.Publish(
            publishedByUserId: 2,
            changeReason: "стенд тесту",
            effectiveFrom: new DateOnly(2025, 1, 1),
            testsPassed: true,
            utcNow);
        SetId(version, versionId);
        return version;
    }

    private static TableDef Table(int id, string code, int firstColumnId)
    {
        var table = new TableDef(1, EcrCode.Create(code), Name(code), id, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, id);

        for (byte month = 1; month <= 2; month++)
        {
            var column = new ColumnDef(id, EcrCode.Create($"M0{month}"), Name($"M0{month}"), month, CellDataType.Decimal);
            column.SetMonth(true, month);
            SetId(column, firstColumnId + month - 1);
            table.AddColumn(column);
        }

        return table;
    }

    private static CellRecord Cell(int tableDefId, long rowId, int columnId, decimal value)
        => new(new CellAddress(new PeriodKey(Period), rowId, columnId), tableDefId, new CellValueData { ValueNumeric = value });

    private static LocalizedText Name(string text) => new(new Dictionary<string, string> { ["en"] = text });

    private static void SetId(Entity<int> entity, int id)
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
