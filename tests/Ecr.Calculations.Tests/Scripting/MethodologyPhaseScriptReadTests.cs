// tests/Ecr.Calculations.Tests/Scripting/MethodologyPhaseScriptReadTests.cs
using System.Text;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// PSS-08 на шляху скрипта: дві версії-скрипти на одній таблиці з таблицею-псевдонімом (<c>table G = T2</c>) крізь
/// справжній <see cref="CalculationOrchestrator"/> — псевдонім читається раз на виклик, і числа ті самі, що без кешу.
/// </summary>
/// <remarks>
/// ⛔ Таблиця-псевдонім з ІНШОГО періоду, ніж прогін: її входи будуються як і до PSS-08 — зріз за періодом екземпляра,
/// рядки за періодом ПРОГОНУ (<see cref="CalculationInputBuilder.BuildAsync"/>). Готове читання таблиці
/// (<c>MethodologyResolver.ReadTableAsync</c>, рядки за періодом екземпляра) тут дало б інший набір рядків — тому
/// <c>ScriptRuntime.LoadAliasAsync</c> бере його лише для того самого періоду.
/// </remarks>
public sealed class MethodologyPhaseScriptReadTests
{
    private const long DocumentId = 209_901_500;
    private const int RunPeriod = 209901;
    private const int OtherPeriod = 209900;
    private const int TemplateVersionId = 1;
    private const long MainInstance = 500;
    private const long AliasInstance = 501;
    private const int MainDef = 10;
    private const int AliasDef = 11;
    private const int MainK = 101;
    private const int AliasK = 201;
    private const int AliasV = 202;
    private const string Script = "rows from T1;\ntable G = T2;\noutput S: kg = Sum(G.V where G.K == row.K);";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "PSS-08")]
    public async Task Псевдонім_іншого_періоду_бере_рядки_періоду_прогону_як_без_кешу_і_читається_раз()
    {
        // Рядки екземпляра T2 за періодом прогону — G1, G2; за власним періодом екземпляра — лише G1.
        var stand = new Stand(aliasPeriod: OtherPeriod);

        var written = await stand.RunAsync();

        // Обидві версії: Sum(V) по G1 і G2 = 1 + 3. Готове читання за періодом екземпляра дало б 1.
        Assert.Equal(2, written.Count);
        Assert.All(written, o => Assert.Equal(4m, o.Values.Single().Value));

        await stand.Cells.Received(1).ReadSliceAsync(AliasInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await stand.Rows.Received(1).GetRowIdsAsync(AliasInstance, new PeriodKey(RunPeriod), Arg.Any<CancellationToken>());
        await stand.Rows.Received(1).ResolveTableInstanceAsync(AliasInstance, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "PSS-08")]
    public async Task Псевдонім_того_самого_періоду_читається_раз_на_виклик_а_не_на_прив_язку()
    {
        var stand = new Stand(aliasPeriod: RunPeriod);

        var written = await stand.RunAsync();

        Assert.Equal(2, written.Count);
        Assert.All(written, o => Assert.Equal(4m, o.Values.Single().Value));

        // До PSS-08: зріз і рядки псевдоніма — на кожну прив'язку, екземпляр — двічі на прив'язку (рантайм і збирач входів).
        await stand.Cells.Received(1).ReadSliceAsync(AliasInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await stand.Cells.Received(1).ReadSliceAsync(MainInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await stand.Rows.Received(1).GetRowIdsAsync(AliasInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await stand.Rows.Received(1).ResolveTableInstanceAsync(AliasInstance, Arg.Any<CancellationToken>());

        // Екземпляри документа: 1 — рантайми обох прив'язок з кешу виклику; ще 2 — побудова графа залежностей
        // (`ScriptCalculationModule.ReadsBindingsAsync`, власний scope до прогону, поза кешем). До PSS-08: 2 + 2.
        await stand.Rows.Received(3).GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Стенд: справжні оркестратор, резолвер, селектор, збирач входів і модуль скрипта; сховища — підміни.</summary>
    private sealed class Stand
    {
        private readonly ServiceProvider _provider;
        private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
        private readonly ICalculationResultStore _results = Substitute.For<ICalculationResultStore>();

        public Stand(int aliasPeriod)
        {
            var store = Substitute.For<IMethodologyStore>();
            foreach (var (methodologyId, versionId) in Versions)
            {
                var version = PublishedScriptVersion(methodologyId, versionId);
                store.GetPublishedVersionsAsync(methodologyId, Arg.Any<CancellationToken>()).Returns([version]);
                store.GetScriptAsync(versionId, Arg.Any<CancellationToken>())
                    .Returns(new MethodologyScript(Script, null, MethodologyEngine.Script, false, version.NumericMode));
                store.ResolveImportsAsync(versionId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns([]);
                store.GetOutputsAsync(versionId, Arg.Any<CancellationToken>())
                    .Returns([new MethodologyOutput(versionId, EcrCode.Create("S"), 1, 1)]);
                store.GetSubstancesAsync(versionId, Arg.Any<CancellationToken>()).Returns([]);
                store.GetRulesAsync(versionId, Arg.Any<CancellationToken>()).Returns([]);
            }

            store.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<ColumnResultBinding>)[]);

            _periods.FindPeriodBoundsAsync(DocumentId, RunPeriod, Arg.Any<CancellationToken>())
                .Returns(new PeriodBounds(new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 31)));

            var main = new TableInstanceRef(MainInstance, DocumentId, MainDef, TemplateVersionId, RunPeriod);
            var alias = new TableInstanceRef(AliasInstance, DocumentId, AliasDef, TemplateVersionId, aliasPeriod);
            Rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([main, alias]);
            Rows.ResolveTableInstanceAsync(MainInstance, Arg.Any<CancellationToken>()).Returns(main);
            Rows.ResolveTableInstanceAsync(AliasInstance, Arg.Any<CancellationToken>()).Returns(alias);
            Rows.GetRowIdsAsync(MainInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 5001 });
            Rows.GetRowIdsAsync(AliasInstance, new PeriodKey(RunPeriod), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, long>(StringComparer.Ordinal) { ["G1"] = 5011, ["G2"] = 5012 });
            if (aliasPeriod != RunPeriod)
            {
                Rows.GetRowIdsAsync(AliasInstance, new PeriodKey(aliasPeriod), Arg.Any<CancellationToken>())
                    .Returns(new Dictionary<string, long>(StringComparer.Ordinal) { ["G1"] = 5011 });
            }

            Cells.ReadSliceAsync(MainInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns([Cell(RunPeriod, 5001, MainK, MainDef, new CellValueData { ValueString = "a" })]);
            Cells.ReadSliceAsync(AliasInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns(
                [
                    Cell(aliasPeriod, 5011, AliasK, AliasDef, new CellValueData { ValueString = "a" }),
                    Cell(aliasPeriod, 5011, AliasV, AliasDef, new CellValueData { ValueNumeric = 1m }),
                    Cell(aliasPeriod, 5012, AliasK, AliasDef, new CellValueData { ValueString = "a" }),
                    Cell(aliasPeriod, 5012, AliasV, AliasDef, new CellValueData { ValueNumeric = 3m }),
                ]);

            var metadata = Substitute.For<IMetadataCache>();
            metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(Snapshot());

            var units = Substitute.For<IUnitCatalog>();
            units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
                new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
                new Dictionary<string, int>(StringComparer.Ordinal)));

            var bindingStore = Substitute.For<ICalculationBindingStore>();
            bindingStore.ListOutputScalesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyDictionary<string, byte?>)new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase));

            var services = new ServiceCollection();
            services.AddSingleton(store);
            services.AddSingleton(_periods);
            services.AddSingleton(Rows);
            services.AddSingleton(Cells);
            services.AddSingleton(metadata);
            services.AddSingleton(units);
            services.AddSingleton(bindingStore);
            services.AddSingleton(_results);
            services.AddSingleton(Substitute.For<IRegistryStore>());
            services.AddSingleton(Substitute.For<IUnitOfWork>());
            services.AddSingleton(new Evaluator(new FunctionRegistry()));
            services.AddSingleton<ScriptProgramCache>();
            services.AddSingleton<CalendarContext>();
            services.AddScoped<ScriptRunContext>();
            services.AddScoped<MethodologyResolver>();
            services.AddScoped<CalculationInputBuilder>();
            services.AddScoped<CalculationOutputWriter>();
            services.AddScoped<ScriptRowSelector>();
            services.AddScoped<ScriptCalculationModule>();
            services.AddScoped<ICalculationModule>(p => p.GetRequiredService<ScriptCalculationModule>());
            _provider = services.BuildServiceProvider();
        }

        public IRowStore Rows { get; } = Substitute.For<IRowStore>();

        public ICellStore Cells { get; } = Substitute.For<ICellStore>();

        private static (int MethodologyId, int VersionId)[] Versions => [(71, 7101), (72, 7102)];

        public async Task<List<CalculationOutput>> RunAsync()
        {
            var orchestrator = new CalculationOrchestrator(
                _provider.GetRequiredService<MethodologyResolver>(),
                _periods,
                _provider.GetRequiredService<IServiceScopeFactory>());

            await orchestrator.RunAsync(
                1,
                DocumentId,
                new PeriodKey(RunPeriod),
                [.. Versions.Select(v => new CalculationBindingRef(MainInstance, v.MethodologyId))],
                NoOpProgress.Instance,
                CancellationToken.None);

            return [.. _results.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ICalculationResultStore.WriteResultsAsync))
                .SelectMany(c => c.GetArguments())
                .OfType<IEnumerable<CalculationOutput>>()
                .SelectMany(o => o)];
        }

        private static MethodologyVersion PublishedScriptVersion(int methodologyId, int versionId)
        {
            var utcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var version = new MethodologyVersion(
                methodologyId, "1.0.0.0", CalculationLevel.Configuration, createdByUserId: 1, utcNow);
            version.SetScript(Script, Encoding.UTF8.GetBytes(Script));
            version.SwitchEngine(MethodologyEngine.Script);
            version.Publish(
                publishedByUserId: 2,
                changeReason: "стенд тесту",
                effectiveFrom: new DateOnly(2025, 1, 1),
                testsPassed: true,
                utcNow);

            typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, versionId);
            return version;
        }

        private static TemplateVersionSnapshot Snapshot()
        {
            var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
            SetId(sheet, 1);
            var columns = new Dictionary<int, ColumnDef>();
            foreach (var (defId, code, specs) in new (int, string, (int Id, string Code, CellDataType Type)[])[]
                     {
                         (MainDef, "T1", [(MainK, "K", CellDataType.String)]),
                         (AliasDef, "T2", [(AliasK, "K", CellDataType.String), (AliasV, "V", CellDataType.Decimal)]),
                     })
            {
                var def = new TableDef(
                    sheet.Id, EcrCode.Create(code), Text(code), defId, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
                SetId(def, defId);
                for (var i = 0; i < specs.Length; i++)
                {
                    var column = new ColumnDef(defId, EcrCode.Create(specs[i].Code), Text(specs[i].Code), i + 1, specs[i].Type);
                    SetId(column, specs[i].Id);
                    def.AddColumn(column);
                    columns[column.Id] = column;
                }

                sheet.AddTable(def);
            }

            return new TemplateVersionSnapshot(
                TemplateVersionId, 1, [sheet], columns, new Dictionary<(int, string), RowDef>());
        }

        private static CellRecord Cell(int period, long rowId, int columnId, int tableDefId, CellValueData value)
            => new(new CellAddress(new PeriodKey(period), rowId, columnId), tableDefId, value);

        private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

        private static void SetId<T>(T entity, int id)
            where T : Entity<int>
            => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
