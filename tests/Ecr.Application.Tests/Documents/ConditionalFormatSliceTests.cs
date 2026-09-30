using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Правила умовного форматування версії їдуть у зріз разом зі стилем колонки (<c>ФВ-2.7</c>).
/// </summary>
/// <remarks>
/// ⛔ Сітка документа читає правила ЗІ ЗРІЗУ, а не з <c>GET …/conditional-formats</c>:
/// той вимагає <c>Template.View</c>, якого в оператора може не бути. Фікстура — та
/// сама, що в <see cref="HiddenColumnSliceTests"/>, без прихованої колонки.
/// </remarks>
public sealed class ConditionalFormatSliceTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int VolumeId = 11;
    private const int NoteId = 12;
    private const int CubicMetre = 2;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IStyleCatalog _styles = Substitute.For<IStyleCatalog>();

    public ConditionalFormatSliceTests()
    {
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, 3);

        // Колонка з правилами і колонка без них — обидва випадки в одному зрізі.
        var volume = new ColumnDef(3, EcrCode.Create("Volume"), Text("Volume"), 1, CellDataType.Decimal);
        SetId(volume, VolumeId);
        volume.SetUnit(CubicMetre);
        table.AddColumn(volume);

        var note = new ColumnDef(3, EcrCode.Create("Note"), Text("Note"), 2, CellDataType.String);
        SetId(note, NoteId);
        table.AddColumn(note);

        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, 700, 3, 2, Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef> { [VolumeId] = volume, [NoteId] = note },
                new Dictionary<(int, string), RowDef>()));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long>());
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string>());
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());
        _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns([]);
        _cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([]);

        // ⛔ Читання зрізу тепер вимагає і права `Document.View`, і ГРАНТА на
        // проєкт (`A7-53`, `A7-55`). Фікстура видає обидва явно: предмет цих
        // тестів — вміст зрізу, а не доступ, і мовчазний дозвіл підмінив би
        // одне іншим.
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(async _ => ReadScopes.Everything(await _metadata.GetAsync(2, CancellationToken.None)));

        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["m3"] = new(CubicMetre, "m3", DimensionId: 2),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        // ⚠ Предмет цих тестів — умовне форматування, не методології: жодна
        // до таблиці не прив'язана.
        _methodologies.GetMethodologyIdsBoundToTableAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                      .Returns(new List<int>());

        // ⚠ Предмет цих тестів — умовне форматування, не стиль: порожній
        // каталог стилів (директива registry-lookup / cell-style, PR B2).
        _styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<int, StyleDef>());
    }

    private readonly IConditionalFormatStore _conditional = Substitute.For<IConditionalFormatStore>();

    private GetTableSliceHandler Handler()
        => new(_rows, _cells, _metadata, _units, _access, _methodologies, _periods, _styles,
               conditionalFormats: _conditional);

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View" },
        Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
        Denies = new HashSet<string>(StringComparer.Ordinal),
        RoleIds = new HashSet<int>(),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Колонка_несе_свої_правила_умовного_форматування_в_порядку_застосування()
    {
        // ⚠ Сховище віддає за колонкою, а не за порядком: сортування — на обробнику.
        _conditional.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
        [
            new ConditionalFormatRule(2, "Volume", 2, "lt", "0", null, "#0000ff", null, false),
            new ConditionalFormatRule(2, "Volume", 1, "gt", "100", null, "#ff0000", "#ffffff", true),
        ]);

        var slice = await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        var volume = Assert.Single(slice.Columns, c => c.Code == "Volume");
        Assert.NotNull(volume.ConditionalFormats);
        Assert.Equal(["gt", "lt"], volume.ConditionalFormats!.Select(r => r.Operator));
        Assert.Equal(("100", "#ff0000", "#ffffff", true),
            (volume.ConditionalFormats[0].Value, volume.ConditionalFormats[0].BackgroundHex,
             volume.ConditionalFormats[0].ForegroundHex, volume.ConditionalFormats[0].IsBold));

        // Колонка без правил — `null`, а не чужі правила.
        Assert.Null(Assert.Single(slice.Columns, c => c.Code == "Note").ConditionalFormats);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Правила_читаються_для_версії_шаблону_екземпляра()
    {
        _conditional.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        await _conditional.Received(1).GetAsync(2, Arg.Any<CancellationToken>());
    }
}
