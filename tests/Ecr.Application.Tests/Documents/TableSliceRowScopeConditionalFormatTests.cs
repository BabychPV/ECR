using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
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
/// CELL-STYLE L6: слайс віддає результат правил за «статусом рядка» (колонка-довідник), областю рядків і
/// штрихом; приховану колонку-джерело правило НЕ читає.
/// </summary>
public sealed class TableSliceRowScopeConditionalFormatTests
{
    private const long Document = 700;
    private const long TableInstance = 500;
    private const int TableDef = 3;
    private const int Period = 202601;
    private const int VolumeId = 11;
    private const int StatusId = 12;
    private const int SecretId = 13;
    private const int StatusRegistry = 55;
    private const long RemovedEntry = 901;
    private const long RunningEntry = 902;
    private const long NumericCodeEntry = 77;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IConditionalFormatStore _formats = Substitute.For<IConditionalFormatStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    public TableSliceRowScopeConditionalFormatTests()
    {
        var volume = Column("Volume", 1, CellDataType.Decimal, VolumeId);
        var status = Column("Land_Status", 2, CellDataType.Lookup, StatusId);
        status.SetLookup(StatusRegistry);
        var secret = Column("Secret", 3, CellDataType.String, SecretId);
        secret.SetHidden(true);

        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, TableDef);
        table.AddColumn(volume);
        table.AddColumn(status);
        table.AddColumn(secret);
        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, Document, TableDef, 2, Period));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["R1"] = 1, ["R2"] = 2, ["R3"] = 3 });
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string>());
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());

        CellRecord Cell(long row, int column, CellValueData value)
            => new(new CellAddress(new PeriodKey(Period), row, column), TableDef, value);

        _cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            Cell(1, VolumeId, new CellValueData { ValueNumeric = 150m }),
            Cell(1, StatusId, new CellValueData { ValueRegistryEntryId = RemovedEntry }),
            Cell(2, VolumeId, new CellValueData { ValueNumeric = 5m }),
            Cell(2, StatusId, new CellValueData { ValueRegistryEntryId = RunningEntry }),
            Cell(3, VolumeId, new CellValueData { ValueNumeric = 7m }),
            // LP-30: запис Id 77 має КОД «E901»; Id запису REMOVED — 901: правило «901» — не про Id.
            Cell(3, StatusId, new CellValueData { ValueRegistryEntryId = NumericCodeEntry }),
            // Прихована колонка: у R3 її значення «Removed», у R1 — «Running».
            Cell(1, SecretId, new CellValueData { ValueString = "Running" }),
            Cell(3, SecretId, new CellValueData { ValueString = "Removed" }),
        ]);

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef> { [VolumeId] = volume, [StatusId] = status, [SecretId] = secret },
                new Dictionary<(int, string), RowDef>()));

        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(async _ => ReadScopes.Everything(await _metadata.GetAsync(2, CancellationToken.None)));
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());

        var removed = new RegistryEntry(StatusRegistry, EcrCode.Create("REMOVED"), Text("Removed"));
        SetId(removed, RemovedEntry);
        var running = new RegistryEntry(StatusRegistry, EcrCode.Create("RUNNING"), Text("Running"));
        SetId(running, RunningEntry);
        var numeric = new RegistryEntry(StatusRegistry, EcrCode.Create("E901"), Text("Numeric"));
        SetId(numeric, NumericCodeEntry);
        _registries.ListEntriesAsync(StatusRegistry, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([removed, running, numeric]));
    }

    private void Rules(params ConditionalFormatRule[] rules)
        => _formats.GetAsync(2, Arg.Any<CancellationToken>(), Arg.Any<string?>())
                   .Returns(Task.FromResult<IReadOnlyList<ConditionalFormatRule>>(rules));

    private async Task<TableSliceDto> Slice()
        => await Handler().HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

    [Fact]
    public async Task Removed_status_paints_every_visible_cell_of_the_row_by_the_registry_code()
    {
        // Excel: N($B5="Removed") → FF9900 по всьому рядку. «Removed» (регістр) збігається з кодом REMOVED.
        Rules(new ConditionalFormatRule(
            2, "*", 1, "textEq", "Removed", null, "#ff9900", null, false, "column", "Main", null, "Land_Status"));

        var slice = await Slice();

        Assert.Equal("#ff9900", slice.CellFormats!["R1:Volume"].BackgroundHex);
        Assert.Equal("#ff9900", slice.CellFormats["R1:Land_Status"].BackgroundHex);
        Assert.False(slice.CellFormats.ContainsKey("R2:Volume"));
        Assert.False(slice.CellFormats.ContainsKey("R2:Land_Status"));
        // R3 без статусу: textEq не збігається.
        Assert.False(slice.CellFormats.ContainsKey("R3:Volume"));
        // Прихована колонка не отримує формату — її немає в зрізі.
        Assert.DoesNotContain(slice.CellFormats.Keys, k => k.EndsWith(":Secret", StringComparison.Ordinal));
        await _registries.Received(1).ListEntriesAsync(StatusRegistry, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Text_rule_on_the_reference_column_itself_compares_the_entry_code_not_the_id()
    {
        // LP-30: «Removed» (FF9900) на САМІЙ колонці-довіднику без RefColumnCode: у комірці лежить Id 901,
        // правило порівнює код «REMOVED» — раніше long вважався «не текстом» і правило не спрацьовувало ніколи.
        Rules(new ConditionalFormatRule(
            2, "Land_Status", 1, "textEq", "REMOVED", null, "#ff9900", null, false, "column", "Main"));

        var slice = await Slice();

        Assert.Equal(["R1:Land_Status"], slice.CellFormats!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("#ff9900", slice.CellFormats["R1:Land_Status"].BackgroundHex);
    }

    [Fact]
    public async Task Text_rule_never_matches_the_entry_id_written_as_a_string_and_matches_by_code()
    {
        // Запис REMOVED має Id 901: правило «= 901» — про КОД, тож не збігається (Id.ToString() не порівнюється).
        Rules(new ConditionalFormatRule(
            2, "Land_Status", 1, "textEq", "901", null, "#112233", null, false, "column", "Main"));

        Assert.Empty((await Slice()).CellFormats!);

        // Запис Id 77 має код «E901»: за кодом збігається лише він.
        Rules(new ConditionalFormatRule(
            2, "Land_Status", 1, "textEq", "E901", null, "#112233", null, false, "column", "Main"));

        Assert.Equal(["R3:Land_Status"], (await Slice()).CellFormats!.Keys.Order(StringComparer.Ordinal));
    }
    [Fact]
    public async Task TextNe_on_the_reference_column_paints_the_other_entries_not_the_matching_one()
    {
        Rules(new ConditionalFormatRule(
            2, "Land_Status", 1, "textNe", "removed", null, "#00ff00", null, false, "column", "Main"));

        var slice = await Slice();

        Assert.Equal(["R2:Land_Status", "R3:Land_Status"], slice.CellFormats!.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task All_columns_text_rule_without_ref_column_reads_the_code_of_reference_cells_only()
    {
        Rules(new ConditionalFormatRule(
            2, "*", 1, "textEq", "RUNNING", null, "#0000ff", null, false, "column", "Main"));

        var slice = await Slice();

        // Decimal-колонка Volume текстового правила не задовольняє; Id довідника в Volume не потрапляє.
        Assert.Equal(["R2:Land_Status"], slice.CellFormats!.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Row_scope_text_rule_on_the_reference_column_compares_the_code()
    {
        Rules(new ConditionalFormatRule(
            2, "Land_Status", 1, "textEq", "REMOVED", null, "#ff9900", null, false, "row", "Main", ["R2", "R3"]));

        var slice = await Slice();

        // R1 — REMOVED, але поза областю рядків; R2/R3 в області, але не REMOVED.
        Assert.Empty(slice.CellFormats!);
    }

    [Fact]
    public async Task Numeric_operator_never_treats_a_registry_id_as_a_number()
    {
        Rules(new ConditionalFormatRule(
            2, "Land_Status", 1, "gt", "0", null, "#ff0000", null, false, "column", "Main"));

        var slice = await Slice();

        Assert.Empty(slice.CellFormats!);
    }

    [Fact]
    public async Task Ref_column_text_rule_still_matches_by_code_after_the_own_value_change()
    {
        // Регресія Inactive: колонка-джерело — довідник; клітинки рядка — Volume.
        Rules(new ConditionalFormatRule(
            2, "Volume", 1, "textEq", "RUNNING", null, "#cccccc", null, false, "column", "Main", null, "Land_Status"));

        var slice = await Slice();

        Assert.Equal(["R2:Volume"], slice.CellFormats!.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Hidden_source_column_is_never_read_by_a_rule()
    {
        // ⛔ Нерозкриття: R3 має Secret = "Removed", але колонка прихована — правило не застосовується
        // ні до R3 (textEq), ні до R1/R2 (textNe), хоч значення порожні/інші.
        Rules(
            new ConditionalFormatRule(2, "Volume", 1, "textEq", "Removed", null, "#ff0000", null, false, "column", "Main", null, "Secret"),
            new ConditionalFormatRule(2, "Volume", 2, "textNe", "Nothing", null, "#00ff00", null, false, "column", "Main", null, "Secret"));

        var slice = await Slice();

        Assert.Empty(slice.CellFormats!);
    }

    [Fact]
    public async Task Row_scope_limits_the_format_to_listed_rows_and_a_hatch_pattern_is_returned()
    {
        Rules(new ConditionalFormatRule(
            2, "Volume", 1, "notEmpty", null, null, null, null, false, "row", "Main", ["R2"], null, "hatch"));

        var slice = await Slice();

        Assert.Equal(new CellFormatDto(null, null, false, "hatch"), slice.CellFormats!["R2:Volume"]);
        Assert.Single(slice.CellFormats);
    }

    [Fact]
    public async Task Rule_of_another_table_is_ignored()
    {
        Rules(new ConditionalFormatRule(
            2, "Volume", 1, "gt", "0", null, "#ff0000", null, false, "column", "OtherTable"));

        var slice = await Slice();

        Assert.Empty(slice.CellFormats!);
    }

    [Fact]
    public async Task Legacy_column_rule_without_scope_still_paints_as_before()
    {
        Rules(new ConditionalFormatRule(2, "Volume", 1, "gt", "100", null, "#ff0000", null, false));

        var slice = await Slice();

        Assert.Equal("#ff0000", slice.CellFormats!["R1:Volume"].BackgroundHex);
        await _registries.DidNotReceive().ListEntriesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private GetTableSliceHandler Handler()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        var styles = Substitute.For<IStyleCatalog>();
        styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, StyleDef>());
        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(TableDef, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        return new GetTableSliceHandler(
            _rows, _cells, _metadata, units, _access, methodologies, Substitute.For<IPeriodStore>(), styles,
            conditionalFormats: _formats, registries: _registries);
    }

    private static ColumnDef Column(string code, int ordinal, CellDataType type, int id)
    {
        var column = new ColumnDef(TableDef, EcrCode.Create(code), Text(code), ordinal, type);
        SetId(column, id);

        return column;
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(StringComparer.Ordinal),
        RoleIds = new HashSet<int>(),
    };

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<TId>(Entity<TId> entity, TId id)
        where TId : struct, IEquatable<TId>
        => typeof(Entity<TId>).GetProperty("Id")!.SetValue(entity, id);
}
