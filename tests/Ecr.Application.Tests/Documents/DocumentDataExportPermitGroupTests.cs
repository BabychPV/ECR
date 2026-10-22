using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Permits PRMG: експорт даних/шапки (CSV/JSON) для колонки/поля, ціль якого - ГРУПА дозволів. Прихований реєстр групи
/// записів не має, тож без розгортання складу (<c>LookupTargetEntries</c>) експорт друкував би Id запису замість номера.
/// </summary>
/// <remarks>
/// Мутації: у <c>DocumentDataExporter.LookupsAsync</c> / <c>DocumentHeaderExport.ReadAsync</c> повернути
/// <c>registries.ListEntriesAsync(registryId)</c> без <c>LookupTargetEntries.ListAsync</c> - тести групи червоні (друкується Id);
/// Land-тест лишається зеленим і доводить, що звичайна ціль читається як раніше (0 розгортань складу).
/// </remarks>
public sealed class DocumentDataExportPermitGroupTests
{
    private const long DocumentId = 700;
    private const long Instance = 500;
    private const int Period = 202601;
    private const int Group = 900;
    private const int WaterA = 10;
    private const int WaterB = 11;
    private const int Land = 5;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();
    private readonly FakePermitGroupResolver _groups = new FakePermitGroupResolver().WithGroup(Group, [WaterA, WaterB]);

    public DocumentDataExportPermitGroupTests()
    {
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, 3);
        var permit = new ColumnDef(3, EcrCode.Create("Permit"), Text("Permit"), 1, CellDataType.Lookup);
        permit.SetLookup(Group);
        SetId(permit, 12);
        var substance = new ColumnDef(3, EcrCode.Create("Substance"), Text("Substance"), 2, CellDataType.Lookup);
        substance.SetLookup(Land);
        SetId(substance, 13);
        table.AddColumn(permit);
        table.AddColumn(substance);
        sheet.AddTable(table);

        var headerPermit = new HeaderFieldDef(2, EcrCode.Create("PERMIT"), Text("Permit"), 1, CellDataType.Lookup);
        headerPermit.SetLookup(Group);
        SetId(headerPermit, 21);

        var snapshot = new TemplateVersionSnapshot(
            2, 0, [sheet],
            new Dictionary<int, ColumnDef> { [12] = permit, [13] = substance },
            new Dictionary<(int, string), RowDef>())
        {
            HeaderFields = [headerPermit],
        };

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(Instance, DocumentId, 3, 2, Period)]);
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [Instance] = new Dictionary<string, long> { ["r1"] = 101 } });
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), new PeriodKey(Period), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [Instance] =
                [
                    new(new CellAddress(new PeriodKey(Period), 101, 12), 3, new CellValueData { ValueRegistryEntryId = 2002 }),
                    new(new CellAddress(new PeriodKey(Period), 101, 13), 3, new CellValueData { ValueRegistryEntryId = 3001 }),
                ],
            });
        _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(
            (IReadOnlyDictionary<int, DocumentHeaderValueData>)new Dictionary<int, DocumentHeaderValueData>
            {
                [21] = new() { ValueRegistryEntryId = 2001 },
            });

        // Той самий номер N100 є в обох типах групи: у комірці - запис ТИПУ B (2002), у шапці - запису ТИПУ A (2001).
        Definition(WaterA, "PRM_WATER_A", [Entry(WaterA, 2001, "N100"), Entry(WaterA, 2003, "N200")]);
        Definition(WaterB, "PRM_WATER_B", [Entry(WaterB, 2002, "N100")]);
        _registries.ListEntriesAsync(Group, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([]));
        _registries.ListEntriesAsync(Land, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([Entry(Land, 3001, "CO2")]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Json_колонка_група_друкує_номер_і_тип_а_не_Id_і_Land_колонка_лишається_кодом()
    {
        var json = await Exporter().ExportAsync(DocumentId, Period, DocumentExportFormat.Json, false, null, null, CancellationToken.None, "en");

        using var doc = JsonDocument.Parse(json);
        var values = doc.RootElement.GetProperty("tables")[0].GetProperty("rows")[0].GetProperty("values");

        // Номер N100 є в двох типах: підпис однозначний і не збігається з жодним Id.
        Assert.Equal("N100 — WATER_B", values.GetProperty("Permit").GetString());
        Assert.Equal("CO2", values.GetProperty("Substance").GetString());

        // Land: розгортання складу не виконувалось (жодного читання визначення реєстру-члена).
        await _registries.DidNotReceive().FindDefinitionByIdAsync(Land, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Json_поле_шапки_група_друкує_номер_і_тип()
    {
        var json = await Exporter().ExportAsync(DocumentId, Period, DocumentExportFormat.Json, false, null, null, CancellationToken.None, "en");

        using var doc = JsonDocument.Parse(json);
        var header = doc.RootElement.GetProperty("header")[0];

        Assert.Equal("PERMIT", header.GetProperty("code").GetString());
        Assert.Equal("N100 — WATER_A", header.GetProperty("value").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_резолвера_груп_поведінка_як_раніше_для_звичайної_цілі()
    {
        var bare = new DocumentDataExporter(_cells, _rows, _metadata, _registries, headers: _headers);

        var json = await bare.ExportAsync(DocumentId, Period, DocumentExportFormat.Json, false, null, null, CancellationToken.None, "en");

        using var doc = JsonDocument.Parse(json);
        var values = doc.RootElement.GetProperty("tables")[0].GetProperty("rows")[0].GetProperty("values");
        Assert.Equal("CO2", values.GetProperty("Substance").GetString());
    }

    private DocumentDataExporter Exporter()
        => new(_cells, _rows, _metadata, _registries, headers: _headers, groups: _groups);

    private void Definition(int id, string code, IReadOnlyList<RegistryEntry> entries)
    {
        var definition = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal: true);
        SetId(definition, id);
        _registries.FindDefinitionByIdAsync(id, Arg.Any<CancellationToken>()).Returns(definition);
        _registries.ListEntriesAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromResult(entries));
    }

    private static RegistryEntry Entry(int registryId, long id, string code)
    {
        var entry = new RegistryEntry(registryId, EcrCode.Create(code), Text(code));
        typeof(Entity<long>).GetProperty("Id")!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
