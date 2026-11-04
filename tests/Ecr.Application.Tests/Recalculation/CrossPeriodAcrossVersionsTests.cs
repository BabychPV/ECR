// tests/Ecr.Application.Tests/Recalculation/CrossPeriodAcrossVersionsTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// RPT-STABLE Part B: міжперіодне посилання через межу версій шаблону. Формула версії 2 в періоді 5 читає
/// <c>[Period:-1].[C1]</c>, а період 4 лежить на версії 1: колонка шукається за КОДОМ у версії періоду; код, що змінився,
/// розвʼязується збереженою мапою перейменувань (псевдонім); колонка, якої в тій версії немає, дає NULL — не 0 і не помилку.
/// </summary>
/// <remarks>
/// Мутаційні докази: (1) у <c>RecalculationService.TranslateForeignInstancesAsync</c> не мапити колонки (<c>map</c> порожня) —
/// червоніють «той самий код» і «перейменована»; (2) прибрати гілку <c>forward/backward</c> — червоніє «перейменована».
/// </remarks>
public sealed class CrossPeriodAcrossVersionsTests
{
    private const long DocumentId = 700;
    private const int Year = 2026;
    private const int FormulaId = 555;
    private const byte CrossPeriod = 3;
    private const long RowId = 2001;

    private readonly ICellStore _cells = Substitute.For<ICellStore, IOwnerReadSession>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();

    private TableDef _tableV2 = null!;
    private TableDef _tableV1 = null!;
    private int _inputV2;
    private int _outV2;
    private int? _inputV1;
    private int _outV1;

    private static PeriodKey Key(int sequence) => PeriodKey.Create(Year, sequence);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "RPT-STABLE")]
    [InlineData("C1", false, 40)]       // той самий код у версії 1
    [InlineData("C1_OLD", true, 40)]    // перейменована: мапа cfg.ColumnRename - псевдонім
    [InlineData("C1_OLD", false, null)] // перейменована без мапи: колонки з таким кодом у v2 немає - NULL, не 0
    [InlineData(null, false, null)]     // у версії 1 колонки взагалі немає - NULL
    public async Task Посилання_на_попередній_період_іншої_версії_знаходить_колонку_за_кодом_або_псевдонімом_інакше_null(
        string? oldCode, bool withRenameMap, int? expected)
    {
        Arrange(oldCode);
        var renames = Substitute.For<IColumnRenameMap>();
        var forward = new Dictionary<int, int>();
        var backward = new Dictionary<int, int>();
        if (withRenameMap && _inputV1 is { } oldId)
        {
            forward[oldId] = _inputV2;
            backward[_inputV2] = oldId;
        }

        renames.GetAsync(Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyDictionary<int, int>)forward, (IReadOnlyDictionary<int, int>)backward));

        var service = Service(renames);
        await service.RecalculateAllAsync(DocumentId, Key(5), CancellationToken.None);

        var applied = _cells.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync))
            .SelectMany(c => ((IReadOnlyCollection<CellChangeSet>)c.GetArguments()[0]!).SelectMany(set => set.Upserts))
            .Where(r => r.Address.ColumnDefId == _outV2)
            .ToList();

        if (expected is { } value)
        {
            Assert.Equal(value, Assert.Single(applied).Value.ValueNumeric);
        }
        else
        {
            // NULL, а не 0: або запису немає, або він порожній без числа.
            Assert.All(applied, r => Assert.Null(r.Value.ValueNumeric));
        }
    }

    private RecalculationService Service(IColumnRenameMap renames)
    {
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(Year, 1, 1), new DateOnly(Year, 1, 31)));

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Ecr.Expressions.Evaluation.ExpressionValue>());

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _cells, _rows, periods, _metadata, _versions, new RealFormulaEngine(), _units,
            Substitute.For<IRegistryStore>(), headers, Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(Year, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            _uow, Substitute.For<ISheetEditGate>(), columnRenames: renames);
    }

    private void Arrange(string? oldCode)
    {
        // Версія 1: ті самі аркуш/таблиця, колонка входу під oldCode (або без неї). Ідентифікатори зсунуто, щоб не збігались з v2.
        var b1 = new TemplateBuilder { TemplateVersionId = 1 };
        for (var i = 0; i < 50; i++)
        {
            b1.Sheet("PAD" + i);
        }

        var sheet1 = b1.Sheet("S");
        _tableV1 = b1.Table(sheet1, "T1");
        if (oldCode is not null)
        {
            _inputV1 = b1.Column(_tableV1, oldCode, isMonthColumn: true).Id;
        }

        b1.Row(_tableV1, "R1", 1);
        _outV1 = b1.Column(_tableV1, "Out", CellDataType.Formula).Id;

        var b2 = new TemplateBuilder { TemplateVersionId = 2 };
        var sheet2 = b2.Sheet("S");
        _tableV2 = b2.Table(sheet2, "T1");
        _inputV2 = b2.Column(_tableV2, "C1", isMonthColumn: true).Id;
        b2.Row(_tableV2, "R1", 1);
        _outV2 = b2.Column(_tableV2, "Out", CellDataType.Formula).Id;

        var formula = new FormulaDef(_tableV2.Id, FormulaScope.Column, "[Period:-1].[C1]", ExpressionDialect.Template);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(formula, FormulaId);
        formula.AssignColumn(_outV2);
        _tableV2.AddFormula(formula);

        _metadata.GetAsync(1, Arg.Any<CancellationToken>()).Returns(b1.Build());
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(b2.Build());

        // Періоди 1-4 - версія 1; 5 і далі - версія 2.
        TableInstanceRef InstanceOf(PeriodKey key)
            => key.Sequence >= 5
                ? new TableInstanceRef(1000 + key.Sequence, DocumentId, _tableV2.Id, 2, key.Value)
                : new TableInstanceRef(1000 + key.Sequence, DocumentId, _tableV1.Id, 1, key.Value);

        _rows.ResolveTableInstanceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => InstanceOf(Key((int)(call.ArgAt<long>(0) - 1000))));
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<TableInstanceRef>)[InstanceOf(call.ArgAt<PeriodKey>(1))]);
        _rows.GetRowIdsBatchAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>>)
                call.ArgAt<IReadOnlyList<long>>(0).ToDictionary(
                    id => id,
                    id => (IReadOnlyDictionary<string, long>)new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = RowId }));

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var period = call.ArgAt<PeriodKey>(1);
                return (IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>)
                    call.ArgAt<IReadOnlyList<long>>(0).ToDictionary(id => id, id => (IReadOnlyList<CellRecord>)Slice(period));
            });

        // Ребро читання: формула v2 читає C1 попереднього періоду (граф публікується по версії власника формули).
        _versions.ListFormulaDependenciesAsync(2, Arg.Any<CancellationToken>())
            .Returns([FormulaDependency.ForFormula(FormulaId, CrossPeriod, _tableV2.Id, "R1", _inputV2, null, -1, 0)]);
    }

    private List<CellRecord> Slice(PeriodKey period)
    {
        if (period.Sequence >= 5)
        {
            return
            [
                new(new CellAddress(period, RowId, _inputV2), _tableV2.Id, new CellValueData { ValueNumeric = period.Sequence * 10 }),
                new(new CellAddress(period, RowId, _outV2), _tableV2.Id, new CellValueData { ValueNumeric = -1m, IsCalculated = true }),
            ];
        }

        var cells = new List<CellRecord>
        {
            new(new CellAddress(period, RowId, _outV1), _tableV1.Id, new CellValueData { IsEmpty = true }),
        };
        if (_inputV1 is { } input)
        {
            cells.Add(new CellRecord(
                new CellAddress(period, RowId, input), _tableV1.Id, new CellValueData { ValueNumeric = period.Sequence * 10 }));
        }

        return cells;
    }
}
