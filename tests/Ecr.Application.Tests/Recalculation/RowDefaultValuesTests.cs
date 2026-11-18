// tests/Ecr.Application.Tests/Recalculation/RowDefaultValuesTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// ✎ B175: дефолти комірок фіксованого рядка (<see cref="RowDef.CellDefaults"/>) у <see cref="ColumnDefaultSet"/> —
/// тому самому доповненні зрізу, яким користуються формули, валідація, подання й методології (D-283 Q10=A: лише читання).
/// </summary>
/// <remarks>
/// Пріоритет на пару (рядок, колонка): записана комірка (зокрема явна порожнеча) &gt; дефолт рядка &gt; дефолт колонки;
/// <c>""</c> рядка прибирає дефолт колонки. Мутації: прибрати мапу рядків у конструкторі <see cref="ColumnDefaultSet"/>
/// — червоніють перший і другий; поміняти пріоритет (дефолт колонки поверх рядка) — другий; прибрати перевірку
/// «present» у <see cref="ColumnDefaultSet.Complete"/> — третій.
/// </remarks>
public sealed class RowDefaultValuesTests
{
    private const int Version = 1;
    private const int Registry = 900;
    private const long Running = 77;
    private const long Removed = 78;
    private static readonly PeriodKey Period = PeriodKey.Create(2026, 1);

    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "B175")]
    public async Task Lookup_дефолт_рядка_доповнює_зріз_ідентифікатором_запису()
    {
        var (table, status, _) = Arrange(r1: new() { ["Status"] = "RUNNING" });

        var set = await new ColumnDefaultValues(_metadata, _registries).ForVersionAsync(Version, CancellationToken.None);
        var cells = set.Complete([], table.Id, RowIds(), Period);

        var r1 = Assert.Single(cells, c => c.Address.TableRowId == 1001 && c.Address.ColumnDefId == status.Id);
        Assert.Equal(Running, r1.Value.ValueRegistryEntryId);
        Assert.DoesNotContain(cells, c => c.Address.TableRowId == 1002 && c.Address.ColumnDefId == status.Id);
        Assert.False(set.HasUnresolvedLookup);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "B175")]
    public async Task Дефолт_рядка_перекриває_дефолт_колонки_а_порожній_рядок_прибирає_його()
    {
        var (table, _, qty) = Arrange(
            r1: new() { ["Qty"] = "2" },
            r2: new() { ["Qty"] = "" },
            qtyDefault: "1.5");

        var set = await new ColumnDefaultValues(_metadata, _registries).ForVersionAsync(Version, CancellationToken.None);
        var cells = set.Complete([], table.Id, RowIds(), Period);

        Assert.Equal(2m, Assert.Single(cells, c => c.Address.TableRowId == 1001 && c.Address.ColumnDefId == qty.Id).Value.ValueNumeric);
        Assert.DoesNotContain(cells, c => c.Address.TableRowId == 1002 && c.Address.ColumnDefId == qty.Id);
        Assert.Equal(1.5m, Assert.Single(cells, c => c.Address.TableRowId == 1003 && c.Address.ColumnDefId == qty.Id).Value.ValueNumeric);

        // OfRow — те саме для споживачів без Complete (RecalculationService, RowLocalPreview).
        Assert.Equal(2m, set.OfRow(table.Id, "R1").Single(d => d.ColumnDefId == qty.Id).Value.ValueNumeric);
        Assert.DoesNotContain(set.OfRow(table.Id, "R2"), d => d.ColumnDefId == qty.Id);
        Assert.Equal(1.5m, set.OfRow(table.Id, "R3").Single(d => d.ColumnDefId == qty.Id).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "B175")]
    public async Task Записана_комірка_і_явна_порожнеча_перемагають_дефолт_рядка()
    {
        var (table, status, _) = Arrange(
            r1: new() { ["Status"] = "RUNNING" },
            r2: new() { ["Status"] = "RUNNING" });

        IReadOnlyList<CellRecord> stored =
        [
            new(new CellAddress(Period, 1001, status.Id), table.Id, new CellValueData { ValueRegistryEntryId = Removed }),
            new(new CellAddress(Period, 1002, status.Id), table.Id, new CellValueData { IsEmpty = true }),
        ];

        var set = await new ColumnDefaultValues(_metadata, _registries).ForVersionAsync(Version, CancellationToken.None);
        var cells = set.Complete(stored, table.Id, RowIds(), Period);

        Assert.Equal(Removed, Assert.Single(cells, c => c.Address.TableRowId == 1001 && c.Address.ColumnDefId == status.Id).Value.ValueRegistryEntryId);
        Assert.True(Assert.Single(cells, c => c.Address.TableRowId == 1002 && c.Address.ColumnDefId == status.Id).Value.IsEmpty);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "B175")]
    public void SeedColumns_бере_колонку_з_дефолтом_рядка()
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var table = builder.Table(builder.Sheet("S"), "T");
        var status = builder.Column(table, "Status", CellDataType.Lookup);
        status.SetLookup(Registry);
        builder.Column(table, "Qty");
        builder.Row(table, "R1").SetCellDefaults(new Dictionary<string, string> { ["Status"] = "RUNNING" });

        Assert.Equal([status.Id], ColumnDefaultValues.SeedColumns(table));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "B175")]
    public async Task Невідомий_код_дефолту_рядка_як_відсутній_і_кеш_короткий()
    {
        var (table, status, _) = Arrange(r1: new() { ["Status"] = "NOPE" });

        var set = await new ColumnDefaultValues(_metadata, _registries).ForVersionAsync(Version, CancellationToken.None);

        Assert.DoesNotContain(set.Complete([], table.Id, RowIds(), Period), c => c.Address.ColumnDefId == status.Id);
        Assert.True(set.HasUnresolvedLookup);
    }

    private static Dictionary<string, long> RowIds()
        => new(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002, ["R3"] = 1003 };

    private (TableDef Table, ColumnDef Status, ColumnDef Qty) Arrange(
        Dictionary<string, string>? r1 = null, Dictionary<string, string>? r2 = null, string? qtyDefault = null)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var table = builder.Table(builder.Sheet("S"), "T");
        var status = builder.Column(table, "Status", CellDataType.Lookup);
        status.SetLookup(Registry);
        var qty = builder.Column(table, "Qty");
        if (qtyDefault is not null)
        {
            qty.SetPresentation(qtyDefault, null, null);
        }

        builder.Row(table, "R1").SetCellDefaults(r1);
        builder.Row(table, "R2").SetCellDefaults(r2);
        builder.Row(table, "R3");

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());
        _registries.FindEntriesByCodesAsync(Registry, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryEntry>)
            [
                RegistryEntry.ForSnapshot(
                    Running, Registry, EcrCode.Create("RUNNING"),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "Running" }), 1),
                RegistryEntry.ForSnapshot(
                    Removed, Registry, EcrCode.Create("REMOVED"),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "Removed" }), 1),
            ]);

        return (table, status, qty);
    }
}
