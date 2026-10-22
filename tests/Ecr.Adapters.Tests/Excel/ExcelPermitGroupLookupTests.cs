// tests/Ecr.Adapters.Tests/Excel/ExcelPermitGroupLookupTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Permits.Groups;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Permits PRMG: Excel для колонки, ціль якої - ГРУПА дозволів. Експорт друкує «номер — тип»; імпорт читає той самий підпис
/// або унікальний голий номер, а номер, що збігається в кількох типах, ВІДМОВЛЯЄ з переліком кандидатів (без пріоритету типу).
/// </summary>
/// <remarks>
/// Мутації: у <c>ExcelExporter.LookupsAsync</c> прибрати <c>LookupTargetEntries.ListAsync</c> (повернути <c>ListEntriesAsync</c>) - тест
/// експорту червоний (друкується Id); у <c>LookupTargetEntries.BuildImportCodes</c> віддати перший запис замість відмови - тест
/// неоднозначності червоний.
/// </remarks>
public sealed class ExcelPermitGroupLookupTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int ColumnId = 11;
    private const int Group = 900;
    private const int WaterA = 10;
    private const int WaterB = 11;
    private const string RowKey = "R1";
    private const long RowId = 1001;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly FakePermitGroupResolver _groups = new FakePermitGroupResolver().WithGroup(Group, [WaterA, WaterB]);
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    public ExcelPermitGroupLookupTests()
    {
        // N100 є в обох типах; N200 - лише в A; N300 - лише в B.
        Define(WaterA, "PRM_WATER_A", [Entry(WaterA, 2001, "N100"), Entry(WaterA, 2002, "N200")]);
        Define(WaterB, "PRM_WATER_B", [Entry(WaterB, 2101, "N100"), Entry(WaterB, 2102, "N300")]);
        _registries.ListEntriesAsync(Group, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Експорт_колонки_групи_друкує_номер_і_тип()
    {
        using var workbook = await ExportAsync(valueEntryId: 2101);

        Assert.Equal("N100 — WATER_B", workbook.Worksheet("Sheet").Cell(3, 1).GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Round_trip_підпис_з_експорту_імпортується_у_той_самий_запис_без_змін()
    {
        using var workbook = await ExportAsync(valueEntryId: 2101);
        var printed = workbook.Worksheet("Sheet").Cell(3, 1).GetString();

        // Імпорт тієї ж книги: значення в базі те саме -> змін немає, відмов немає.
        var diff = await ImportAsync(printed, existing: 2101);

        Assert.Empty(diff.Changes);
        Assert.Empty(diff.Rejected);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("N100 — WATER_A", 2001L)]
    [InlineData("N100 — WATER_B", 2101L)]
    [InlineData("n100 — water_b", 2101L)]
    public async Task Імпорт_за_підписом_номер_тип_розв_язується_однозначно(string typed, long expected)
    {
        var diff = await ImportAsync(typed, existing: null);

        var change = Assert.Single(diff.Changes);
        Assert.Equal(expected, change.NewValue);
        Assert.Empty(diff.Rejected);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("N200", 2002L)]
    [InlineData("N300", 2102L)]
    public async Task Імпорт_унікального_голого_номера_приймається(string typed, long expected)
    {
        var diff = await ImportAsync(typed, existing: null);

        Assert.Equal(expected, Assert.Single(diff.Changes).NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Імпорт_номера_що_збігається_в_кількох_типах_відмовляє_з_кандидатами_без_пріоритету_типу()
    {
        var diff = await ImportAsync("N100", existing: null);

        Assert.Empty(diff.Changes);
        var rejection = Assert.Single(diff.Rejected);
        Assert.Equal(ImportMessageKeys.AmbiguousPermitNumber, rejection.MessageKey);
        Assert.Equal("Permit", rejection.ColumnCode);
        Assert.Contains("N100 — WATER_A", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("N100 — WATER_B", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Імпорт_невідомого_номера_лишається_відмовою_ExpectsIdentifier()
    {
        var diff = await ImportAsync("N999", existing: null);

        var rejection = Assert.Single(diff.Rejected);
        Assert.Equal(ImportMessageKeys.ExpectsIdentifier, rejection.MessageKey);
    }

    private async Task<TableDiff> ImportAsync(string typed, long? existing)
    {
        // Те саме збирання кодів, що й у ExcelImporter.LookupsAsync.
        var entries = await LookupTargetEntries.ListAsync(_registries, _groups, Group, CancellationToken.None);
        LookupTargetEntries.BuildImportCodes(entries, out var codes, out var ambiguous);

        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var column = builder.Column(table, "Permit", CellDataType.Lookup);
        builder.Row(table, "7001001", 1);
        column.SetLookup(Group);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = typed;

        var block = new ExcelTableBlock(
            500, TableDefId: 0, "T0", "S0", HeaderRow: 1,
            Columns: [new ExcelColumnRef(column.Id, "Permit", 1, false, Group)],
            Rows: [new ExcelRowRef("7001001", 2)]);

        IReadOnlyList<CellRecord> current = existing is { } id
            ? [new CellRecord(new CellAddress(Period, 1001, column.Id), table.Id, new CellValueData { ValueRegistryEntryId = id })]
            : [];

        return new ImportDiffBuilder().Build(
            worksheet, block, PeriodKeyValue, table,
            new Dictionary<CellAddress, EditDecision>(),
            new Dictionary<int, IReadOnlyDictionary<string, long>> { [Group] = codes },
            new Dictionary<string, long>(StringComparer.Ordinal) { ["7001001"] = 1001 },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["7001001"] = "0x0A" },
            current,
            canReadColumn: null,
            culture: null,
            unusableLookups: null,
            ambiguousLookups: new Dictionary<int, IReadOnlyDictionary<string, string>> { [Group] = ambiguous });
    }

    private async Task<XLWorkbook> ExportAsync(long valueEntryId)
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        var column = new ColumnDef(TableId, EcrCode.Create("Permit"), Text("Permit"), 1, CellDataType.Lookup);
        SetId(column, ColumnId);
        column.SetLookup(Group);
        table.AddColumn(column);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [ColumnId] = column },
            new Dictionary<(int, string), RowDef>());

        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [InstanceId] = new Dictionary<string, long>(StringComparer.Ordinal) { [RowKey] = RowId },
            });

        var store = Substitute.For<ICellStore>();
        store.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [InstanceId] = [new(new CellAddress(Period, RowId, ColumnId), TableId, new CellValueData { ValueRegistryEntryId = valueEntryId })],
            });

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        var styles = Substitute.For<IStyleCatalog>();
        styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, StyleDef>());

        var exporter = new ExcelExporter(
            store, rows, metadata, styles, _registries,
            new StyleMapper(), new FormulaTranslator(), groups: _groups);

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: false, IncludeStyles: false, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        var copy = new MemoryStream();
        await output.CopyToAsync(copy);
        copy.Position = 0;

        return new XLWorkbook(copy);
    }

    private void Define(int id, string code, IReadOnlyList<RegistryEntry> entries)
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

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
