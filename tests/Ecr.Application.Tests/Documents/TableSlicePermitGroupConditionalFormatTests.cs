using Ecr.Application.Documents;
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
/// Permits PRMG: умовне форматування за ТЕКСТОМ коду запису для колонки-ГРУПИ. У комірці лежить Id запису реєстру-члена,
/// а прихований реєстр групи записів не має - без розгортання складу правило «номер = N100» не спрацьовувало б ніколи.
/// </summary>
/// <remarks>
/// Мутація: у <c>GetTableSliceHandler</c> повернути <c>registries.ListEntriesAsync(registryId)</c> без
/// <c>LookupTargetEntries.ListAsync</c> - тест групи червоний; Land-колонка (звичайна ціль) читає рівно свій реєстр.
/// </remarks>
public sealed class TableSlicePermitGroupConditionalFormatTests
{
    private const long Document = 700;
    private const long TableInstance = 500;
    private const int TableDef = 3;
    private const int Period = 202601;
    private const int PermitId = 12;
    private const int GroupRegistry = 900;
    private const int MemberRegistry = 10;
    private const long MemberEntry = 2001;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IConditionalFormatStore _formats = Substitute.For<IConditionalFormatStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly FakePermitGroupResolver _groups = new FakePermitGroupResolver().WithGroup(GroupRegistry, [MemberRegistry]);

    public TableSlicePermitGroupConditionalFormatTests()
    {
        var permit = new ColumnDef(TableDef, EcrCode.Create("PermitNumber"), Text("PermitNumber"), 1, CellDataType.Lookup);
        SetId(permit, PermitId);
        permit.SetLookup(GroupRegistry);

        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, TableDef);
        table.AddColumn(permit);
        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, Document, TableDef, 2, Period));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["R1"] = 1 });
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string>());
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());
        _cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            new CellRecord(new CellAddress(new PeriodKey(Period), 1, PermitId), TableDef, new CellValueData { ValueRegistryEntryId = MemberEntry }),
        ]);

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet], new Dictionary<int, ColumnDef> { [PermitId] = permit }, new Dictionary<(int, string), RowDef>()));

        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(async _ => ReadScopes.Everything(await _metadata.GetAsync(2, CancellationToken.None)));
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());

        var member = new RegistryDef(EcrCode.Create("PRM_WATER_A"), Text("PRM_WATER_A"), isTemporal: true);
        SetId(member, MemberRegistry);
        _registries.FindDefinitionByIdAsync(MemberRegistry, Arg.Any<CancellationToken>()).Returns(member);
        var entry = new RegistryEntry(MemberRegistry, EcrCode.Create("N100"), Text("N100"));
        SetId(entry, MemberEntry);
        _registries.ListEntriesAsync(MemberRegistry, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([entry]));
        _registries.ListEntriesAsync(GroupRegistry, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([]));
    }

    [Fact]
    public async Task Text_rule_on_a_group_column_compares_the_permit_number_of_the_member_registry_entry()
    {
        Rules(new ConditionalFormatRule(2, "PermitNumber", 1, "textEq", "N100", null, "#ff9900", null, false, "column", "Main"));

        var slice = await Handler(_groups).HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Equal("#ff9900", slice.CellFormats!["R1:PermitNumber"].BackgroundHex);
    }

    [Fact]
    public async Task Without_group_expansion_the_rule_would_never_match_documented_baseline()
    {
        // Без резолвера груп реєстр групи порожній: правило не спрацьовує (саме те, що виправляє розгортання складу).
        Rules(new ConditionalFormatRule(2, "PermitNumber", 1, "textEq", "N100", null, "#ff9900", null, false, "column", "Main"));

        var slice = await Handler(null).HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Empty(slice.CellFormats!);
    }

    private void Rules(params ConditionalFormatRule[] rules)
        => _formats.GetAsync(2, Arg.Any<CancellationToken>(), Arg.Any<string?>())
                   .Returns(Task.FromResult<IReadOnlyList<ConditionalFormatRule>>(rules));

    private GetTableSliceHandler Handler(Application.Permits.Groups.IPermitGroupResolver? groups)
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
            conditionalFormats: _formats, registries: _registries, groups: groups);
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

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<TId>(Entity<TId> entity, TId id)
        where TId : struct, IEquatable<TId>
        => typeof(Entity<TId>).GetProperty("Id")!.SetValue(entity, id);
}
