using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// Наскрізна межа глибини: пласка формула <c>[Mass]+1+1+…</c> на 50/90/100/150 доданків
/// дає значення у ВСІХ чотирьох колонках, а не лишає порожніми ті, що довші за
/// <see cref="EvaluationBudget.MaxNestingDepth"/>. Справжнє реальне ядро
/// (<see cref="RealFormulaEngine"/>) над заглушками сховищ, без БД.
/// </summary>
/// <remarks>
/// Доводить саме наслідок для користувача: кількість записаних комірок. До виправлення
/// (глибина рахувалась на вузол ліво-асоціативного гребеня) F100 і F150 лишалися
/// порожніми, перераховувались 2 комірки з 4.
/// </remarks>
public sealed class LongFlatFormulaRecalculationTests
{
    private const long DocumentId = 700;
    private const long MainInstance = 511;
    private const long RowId = 20000;
    private const int Version = 1;
    private static readonly PeriodKey Period = new(202601);

    private static string Chain(int terms) =>
        "[Mass]" + string.Concat(Enumerable.Repeat("+1", terms - 1));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Пласка_формула_100_і_150_доданків_перераховується_а_не_лишає_порожню_комірку()
    {
        int[] terms = [50, 90, 100, 150];

        var (written, values) = await Run(terms);

        Assert.True(written == terms.Length,
            $"Перераховано {written} комірок із {terms.Length}: довгі формули лишились порожніми.");
        foreach (var t in terms)
        {
            Assert.True(values.TryGetValue(t, out var actual),
                $"Колонка F{t} не отримала значення.");
            Assert.Equal((decimal)t, actual);
        }
    }

    private static async Task<(int Written, Dictionary<int, decimal> Values)> Run(int[] terms)
    {
        var cells = Substitute.For<ICellStore>();
        var rows = Substitute.For<IRowStore>();
        var metadata = Substitute.For<IMetadataCache>();
        var versions = Substitute.For<ITemplateVersionStore>();
        var units = Substitute.For<IUnitCatalog>();
        var uow = Substitute.For<IUnitOfWork>();
        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("Waste");
        var main = builder.Table(sheet, "Stacks");
        var mass = builder.Column(main, "Mass");
        builder.Row(main, "r0000", 1);
        var columnIds = new Dictionary<int, int>();
        foreach (var t in terms)
        {
            var column = builder.Column(main, $"F{t}");
            columnIds[t] = column.Id;
            builder.Formula(main, Chain(t), FormulaScope.Column, column: column);
        }

        metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(MainInstance, DocumentId, main.Id, Version, Period.Value)]);
        rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [MainInstance] = new Dictionary<string, long> { ["r0000"] = RowId },
            });
        cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [MainInstance] =
                [
                    new CellRecord(
                        new CellAddress(Period, RowId, mass.Id), main.Id, new CellValueData { ValueNumeric = 1m }),
                ],
            });
        versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns([]);
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        var service = new RecalculationService(
            cells, rows, periods, metadata, versions, new RealFormulaEngine(), units,
            Substitute.For<IRegistryStore>(), headers, Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), uow,
            Substitute.For<ISheetEditGate>());

        var written = await service.RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        var applied = cells.ReceivedCalls()
            .SingleOrDefault(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync));
        var values = new Dictionary<int, decimal>();
        if (applied is not null)
        {
            var upserts = ((IReadOnlyCollection<CellChangeSet>)applied.GetArguments()[0]!)
                .SelectMany(s => s.Upserts);
            foreach (var upsert in upserts)
            {
                foreach (var (termCount, columnId) in columnIds)
                {
                    if (upsert.Address.ColumnDefId == columnId && upsert.Value.ValueNumeric is { } number)
                    {
                        values[termCount] = number;
                    }
                }
            }
        }

        return (written, values);
    }
}
