// tests/Ecr.Calculations.Tests/RecalculationLimitsTests.cs
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
/// ФВ-9.8 (<c>D-205</c>): прогін методологій обмежений паралелізмом і бюджетом
/// комірок входу прив'язки з конфігурації (<see cref="CalculationLimits"/>).
/// </summary>
/// <remarks>
/// ⚠ Справжні резолвер, збирач входів і писар; модуль — лічильник, бо предмет тут
/// не число, а те, скільки прив'язок іде одночасно і чи дійшла прив'язка до
/// модуля взагалі.
///
/// Мутаційні докази: прибрати перевірку бюджету в <c>CalculationOrchestrator</c> —
/// <see cref="Прив_язка_понад_бюджет_відмовляє_до_модуля_і_до_запису"/> червоний;
/// <c>&gt;</c> → <c>&gt;=</c> — <see cref="Прив_язка_рівно_на_бюджеті_проходить"/>
/// червоний; <c>MaxDegreeOfParallelism = int.MaxValue</c> або стала 4 замість
/// конфігурації — <see cref="Паралелізм_не_перевищує_ліміту_з_конфігурації"/> червоний.
///
/// ⚠ Що відмова не ретраїться, доводить не цей файл: виняток тут — рівно
/// <see cref="DomainException"/> (не підклас), а <c>QuartzJobAdapter.IsWorthRetrying</c>
/// такі не повторює, і задача стає <c>Failed</c> з кодом —
/// <c>QuartzJobAdapterRetryTests.Доменна_відмова_провалює_задачу_з_першої_спроби_без_ретраю</c>.
/// </remarks>
public sealed class RecalculationLimitsTests
{
    private const long DocumentId = 700;
    private const int TemplateVersion = 3;
    private const int Period = 202601;
    private const int FirstMethodology = 40;
    private const int ColumnA = 21;
    private const int ColumnB = 22;
    private const int ColumnC = 23;

    // ——— бюджет комірок входу ———

    /// <remarks>
    /// Два рядки × три колонки = 6 комірок входу; бюджет 5. Відмова — до
    /// <c>PrepareAsync</c> (знімок довідників не вантажиться) і без жодного запису.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Прив_язка_понад_бюджет_відмовляє_до_модуля_і_до_запису()
    {
        var stand = new Stand(bindings: 1, rowsPerTable: 2);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => stand.RunAsync(new CalculationLimits { MaxInputCellsPerBinding = 5 }));

        Assert.Equal("ECR-CALC-4222", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-4222.inputCellsOverBudget", error.Details!["messageKey"]);
        Assert.Equal("6", error.Details["cells"]);
        Assert.Equal("5", error.Details["limit"]);

        Assert.Equal(0, stand.Module.Prepared);
        Assert.Equal(0, stand.Module.Executed);
        await stand.Results.DidNotReceiveWithAnyArgs().WriteResultsAsync(default, default!, default);
    }

    /// <remarks>
    /// Межа включна: бюджет — «найбільше дозволене», а не «перше заборонене».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Прив_язка_рівно_на_бюджеті_проходить()
    {
        var stand = new Stand(bindings: 1, rowsPerTable: 2);

        var profile = await stand.RunAsync(new CalculationLimits { MaxInputCellsPerBinding = 6 });

        Assert.Equal(2, profile.Stats.Single().Rows);
        Assert.Equal(2, stand.Written.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Типові_ліміти_це_4_гілки_і_300_тисяч_комірок()
    {
        // ⚠ Літералами, не через константи: твердження проти константи того самого
        // класу поїхало б разом із нею.
        var limits = new CalculationLimits();

        Assert.Equal(4, limits.MaxParallelism);
        Assert.Equal(300_000, limits.MaxInputCellsPerBinding);
    }

    // ——— паралелізм ———

    /// <remarks>
    /// Шість незалежних методологій — один пакет. З лімітом 2 одночасно в модулі
    /// рівно дві гілки: не більше (межа) і не менше (паралельність не зникла —
    /// бюджет 10 хв, ПРД-13, тримається саме на ній).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Паралелізм_не_перевищує_ліміту_з_конфігурації()
    {
        var stand = new Stand(bindings: 6, rowsPerTable: 1);

        await stand.RunAsync(new CalculationLimits { MaxParallelism = 2 });

        Assert.Equal(6, stand.Module.Prepared);
        Assert.Equal(2, stand.Module.MaxConcurrent);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Без_конфігурації_паралелізм_типовий()
    {
        var stand = new Stand(bindings: 6, rowsPerTable: 1);

        await stand.RunAsync(limits: null);

        Assert.Equal(4, stand.Module.MaxConcurrent);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.8")]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    public void Недійсний_ліміт_не_приймається_оркестратором(int parallelism, int cells)
    {
        var stand = new Stand(bindings: 1, rowsPerTable: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => stand.Orchestrator(
            new CalculationLimits { MaxParallelism = parallelism, MaxInputCellsPerBinding = cells }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Реєстрація_віддає_оркестраторові_задані_ліміти()
    {
        var limits = new CalculationLimits { MaxParallelism = 3, MaxInputCellsPerBinding = 7 };

        var services = new ServiceCollection().AddEcrCalculations(limits);

        var registered = Assert.Single(services, d => d.ServiceType == typeof(CalculationLimits));
        Assert.Same(limits, registered.ImplementationInstance);
        Assert.Equal(ServiceLifetime.Singleton, registered.Lifetime);
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Модуль-лічильник: скільки прив'язок одночасно в підготовці.</summary>
    private sealed class CountingModule : ICalculationModule
    {
        private int _current;
        private int _max;
        private int _prepared;
        private int _executed;

        public string Code => "COUNTING";

        public CalculationLevel Level => CalculationLevel.Configuration;

        public int MaxConcurrent => Volatile.Read(ref _max);

        public int Prepared => Volatile.Read(ref _prepared);

        public int Executed => Volatile.Read(ref _executed);

        public bool CanHandle(MethodologyDescriptor methodology) => true;

        public async Task<CalculationBindingContext> PrepareAsync(
            MethodologyDescriptor methodology, long documentId, PeriodKey periodKey, CancellationToken ct)
        {
            Interlocked.Increment(ref _prepared);
            var now = Interlocked.Increment(ref _current);

            int seen;
            while ((seen = Volatile.Read(ref _max)) < now && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            // ⚠ Досить довго, щоб гілки пакета гарантовано перекрилися: інакше
            // «максимум 1» на ліміті 2 був би правдою про швидкий модуль, а не про межу.
            await Task.Delay(TimeSpan.FromMilliseconds(150), ct);

            Interlocked.Decrement(ref _current);

            // Контекст модулю-лічильнику не потрібен: ExecuteAsync його не читає.
            return null!;
        }

        public Task<CalculationOutput> ExecuteAsync(
            CalculationBindingContext binding, CalculationInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref _executed);
            return Task.FromResult(new CalculationOutput(input.DocumentId, input.SourceRowKey, [], []));
        }

        public Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct)
            => ExecuteAsync(null!, input, ct);
    }

    /// <summary>
    /// Стенд: N методологій, кожна прив'язана до власної таблиці; у кожній таблиці
    /// задане число рядків по три заповнені колонки.
    /// </summary>
    private sealed class Stand
    {
        private readonly ServiceProvider _provider;
        private readonly int _bindings;

        public Stand(int bindings, int rowsPerTable)
        {
            _bindings = bindings;

            var store = Substitute.For<IMethodologyStore>();
            var rows = Substitute.For<IRowStore>();
            var cells = Substitute.For<ICellStore>();

            for (var i = 0; i < bindings; i++)
            {
                var methodologyId = FirstMethodology + i;
                var versionId = VersionOf(methodologyId);
                var table = TableOf(i);

                store.GetPublishedVersionsAsync(methodologyId, Arg.Any<CancellationToken>())
                     .Returns([PublishedVersion(methodologyId, versionId)]);
                store.GetRulesAsync(versionId, Arg.Any<CancellationToken>())
                     .Returns([new MethodologyRule(versionId, EcrCode.Create("ALL"), "{}", priority: 10)]);

                rows.ResolveTableInstanceAsync(table, Arg.Any<CancellationToken>())
                    .Returns(new TableInstanceRef(table, DocumentId, 3, TemplateVersion, Period));

                var rowIds = new Dictionary<string, long>();
                var slice = new List<CellRecord>();
                for (var r = 1; r <= rowsPerTable; r++)
                {
                    var rowId = (table * 100) + r;
                    rowIds[$"R{r}"] = rowId;
                    slice.Add(Cell(rowId, ColumnA, r));
                    slice.Add(Cell(rowId, ColumnB, r));
                    slice.Add(Cell(rowId, ColumnC, r));
                }

                rows.GetRowIdsAsync(table, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(rowIds);
                cells.ReadSliceAsync(table, Arg.Any<CancellationToken>()).Returns(slice);
            }

            var periods = Substitute.For<IPeriodStore>();
            periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
                   .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

            var metadata = Substitute.For<IMetadataCache>();
            metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
                new TemplateVersionSnapshot(
                    TemplateVersion,
                    0,
                    [],
                    new Dictionary<int, ColumnDef>
                    {
                        [ColumnA] = Column("A"),
                        [ColumnB] = Column("B"),
                        [ColumnC] = Column("C"),
                    },
                    new Dictionary<(int TableDefId, string RowKey), RowDef>()));

            Results.WriteResultsAsync(
                    Arg.Any<long>(),
                    Arg.Do<IReadOnlyList<CalculationOutput>>(outputs =>
                    {
                        lock (Written)
                        {
                            Written.AddRange(outputs);
                        }
                    }),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(store);
            services.AddSingleton(periods);
            services.AddSingleton(rows);
            services.AddSingleton(cells);
            services.AddSingleton(metadata);
            services.AddSingleton(Results);
            services.AddSingleton(Substitute.For<IUnitOfWork>());
            services.AddScoped<MethodologyResolver>();
            services.AddScoped<CalculationInputBuilder>();
            services.AddScoped<CalculationOutputWriter>();
            services.AddSingleton<ICalculationModule>(Module);
            _provider = services.BuildServiceProvider();
        }

        public CountingModule Module { get; } = new();

        public ICalculationResultStore Results { get; } = Substitute.For<ICalculationResultStore>();

        public List<CalculationOutput> Written { get; } = [];

        public CalculationOrchestrator Orchestrator(CalculationLimits? limits)
            => new(
                _provider.GetRequiredService<MethodologyResolver>(),
                _provider.GetRequiredService<IPeriodStore>(),
                _provider.GetRequiredService<IServiceScopeFactory>(),
                limits);

        public Task<ModuleProfile> RunAsync(CalculationLimits? limits)
            => Orchestrator(limits).RunAsync(
                calculationRunId: 1,
                DocumentId,
                new PeriodKey(Period),
                [.. Enumerable.Range(0, _bindings).Select(i => new CalculationBindingRef(TableOf(i), FirstMethodology + i))],
                NoOpProgress.Instance,
                CancellationToken.None);

        private static long TableOf(int index) => 500 + index;

        private static int VersionOf(int methodologyId) => methodologyId * 10;

        private static CellRecord Cell(long rowId, int columnId, decimal value)
            => new(
                new CellAddress(new PeriodKey(Period), rowId, columnId),
                TableDefId: 3,
                new CellValueData { ValueNumeric = value });

        private static ColumnDef Column(string code)
            => new(
                tableDefId: 3,
                EcrCode.Create(code),
                new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
                ordinal: 0,
                CellDataType.Decimal);

        private static MethodologyVersion PublishedVersion(int methodologyId, int versionId)
        {
            var utcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var version = new MethodologyVersion(
                methodologyId, "1.0.0.0", CalculationLevel.Configuration, createdByUserId: 1, utcNow);

            version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
            version.Publish(
                publishedByUserId: 2,
                changeReason: "стенд ФВ-9.8",
                effectiveFrom: new DateOnly(2025, 1, 1),
                testsPassed: true,
                utcNow);

            typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, versionId);

            return version;
        }
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
