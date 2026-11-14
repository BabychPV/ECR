// tests/Ecr.Application.Tests/Calculations/RelationDefaultValueTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// ✎ C-5 для зв'язків Rollup (B67): агрегат над колонкою джерела бачить <c>ColumnDef.DefaultValue</c> незаповненої
/// комірки (02b §6.3), а явна порожнеча дефолту не отримує. Мутація-доказ: прибрати <c>defaultSet.Complete</c> у
/// <c>RelationRecalculator.ComputeForDocumentAsync</c> — обидва тести червоніють.
/// </summary>
public sealed class RelationDefaultValueTests
{
    private static readonly PeriodKey Period = PeriodKey.Parse(209901);

    [Fact]
    [Trait("Requirement", "C-5")]
    public async Task Rollup_рахує_дефолт_незаповненої_комірки_джерела()
    {
        var write = Assert.Single(await RunAsync(withDefaults: true, emptyCellForRow2: false));

        Assert.Equal(7m, write.Record.Value.ValueNumeric); // 5 (збережене) + 2 (дефолт колонки)
    }

    [Fact]
    [Trait("Requirement", "C-5")]
    public async Task Rollup_явна_порожнеча_дефолту_не_отримує()
    {
        var write = Assert.Single(await RunAsync(withDefaults: true, emptyCellForRow2: true));

        Assert.Equal(5m, write.Record.Value.ValueNumeric);
    }

    [Fact]
    [Trait("Requirement", "C-5")]
    public async Task Rollup_без_ColumnDefaultValues_лишається_як_було()
    {
        var write = Assert.Single(await RunAsync(withDefaults: false, emptyCellForRow2: false));

        Assert.Equal(5m, write.Record.Value.ValueNumeric);
    }

    private static async Task<IReadOnlyList<RelationCellWrite>> RunAsync(bool withDefaults, bool emptyCellForRow2)
    {
        var b = new TemplateBuilder { TemplateVersionId = 7 };
        var sheet = b.Sheet("S");
        var src = b.Table(sheet, "SRC");
        var tgt = b.Table(sheet, "TGT");
        var srcKey = b.Column(src, "Key", CellDataType.String);
        var srcQty = b.Column(src, "Qty");
        srcQty.SetPresentation("2", null, null);
        var tgtKey = b.Column(tgt, "Key", CellDataType.String);
        var tgtTotal = b.Column(tgt, "Total");
        var snapshot = b.Build() with { HasActiveRollupOrCheck = true };

        var relation = new TableRelationDef(EcrCode.Create("REL1"), src.Id, tgt.Id, TableRelationKind.Rollup,
            """{"keys":[{"source":"Key","target":"Key"}]}""");
        relation.Update(src.Id, tgt.Id, TableRelationKind.Rollup, relation.MatchJson,
            """{"sourceColumn":"Qty","targetColumn":"Total","aggregate":"sum"}""", 0, true);
        var store = Substitute.For<ITemplateVersionStore>();
        store.ListTableRelationsAsync(7, Arg.Any<CancellationToken>()).Returns(new List<TableRelationDef> { relation });

        CellRecord Str(long row, int col, int table, string v)
            => new(new CellAddress(Period, row, col), table, new CellValueData { ValueString = v });
        var srcCells = new List<CellRecord>
        {
            Str(1001, srcKey.Id, src.Id, "A"),
            new(new CellAddress(Period, 1001, srcQty.Id), src.Id, new CellValueData { ValueNumeric = 5m }),
            Str(1002, srcKey.Id, src.Id, "A"),
        };
        if (emptyCellForRow2)
        {
            srcCells.Add(new(new CellAddress(Period, 1002, srcQty.Id), src.Id, new CellValueData { IsEmpty = true }));
        }

        var cells = Substitute.For<ICellStore>();
        cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [100] = srcCells,
                [200] = new[] { Str(2001, tgtKey.Id, tgt.Id, "A") },
            });

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(7, Arg.Any<CancellationToken>()).Returns(snapshot);
        ColumnDefaultValues? defaults = withDefaults ? new(metadata, Substitute.For<IRegistryStore>()) : null;

        var input = new RelationRunInput(
            snapshot, 7, Period,
            [new TableInstanceRef(100, 1, src.Id, 7, Period.Value), new TableInstanceRef(200, 1, tgt.Id, 7, Period.Value)],
            new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [100] = new Dictionary<string, long> { ["R1"] = 1001, ["R2"] = 1002 },
                [200] = new Dictionary<string, long> { ["T1"] = 2001 },
            },
            new Dictionary<CellAddress, CellRecord>());

        _ = tgtTotal;
        return await new RelationRecalculator(store, cells, defaults: defaults).ComputeForDocumentAsync(input, CancellationToken.None);
    }
}
