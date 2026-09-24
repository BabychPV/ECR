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
/// Прихована колонка (Appearance → Hidden) не віддається в зріз сітки (<c>V-14</c>).
/// </summary>
/// <remarks>
/// ⛔ Колонка з <c>IsHidden</c> приходила в <c>TableSliceDto.Columns</c>, і сітка
/// документа показувала та редагувала її, хоча Excel-експорт її вже пропускав
/// (<c>DocumentDataExporter</c>). Фікстура — та сама, що в
/// <see cref="ColumnUnitSymbolTests"/>, з однією прихованою колонкою.
/// </remarks>
public sealed class HiddenColumnSliceTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int VolumeId = 11;
    private const int MassId = 12;
    private const int CubicMetre = 2;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IStyleCatalog _styles = Substitute.For<IStyleCatalog>();

    public HiddenColumnSliceTests()
    {
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, 3);

        // Колонка з одиницею і колонка без неї — обидва випадки в одному зрізі.
        var volume = new ColumnDef(3, EcrCode.Create("Volume"), Text("Volume"), 1, CellDataType.Decimal);
        SetId(volume, VolumeId);
        volume.SetUnit(CubicMetre);
        table.AddColumn(volume);

        var note = new ColumnDef(3, EcrCode.Create("Note"), Text("Note"), 2, CellDataType.String);
        SetId(note, MassId);
        note.SetHidden(true);
        table.AddColumn(note);

        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, 700, 3, 2, Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef> { [VolumeId] = volume, [MassId] = note },
                new Dictionary<(int, string), RowDef>()));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long>());
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string>());
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());
        _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns([]);

        // ⛔ Читання зрізу тепер вимагає і права `Document.View`, і ГРАНТА на
        // проєкт (`A7-53`, `A7-55`). Фікстура видає обидва явно: предмет цих
        // тестів — вміст зрізу, а не доступ, і мовчазний дозвіл підмінив би
        // одне іншим.
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());

        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["m3"] = new(CubicMetre, "m3", DimensionId: 2),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        // ⚠ Предмет цих тестів — позначення одиниці, не методології: жодна
        // до таблиці не прив'язана.
        _methodologies.GetMethodologyIdsBoundToTableAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                      .Returns(new List<int>());

        // ⚠ Предмет цих тестів — позначення одиниці, не стиль: порожній
        // каталог стилів (директива registry-lookup / cell-style, PR B2).
        _styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<int, StyleDef>());
    }

    private GetTableSliceHandler Handler()
        => new(_rows, _cells, _metadata, _units, _access, _methodologies, _periods, _styles);

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
    [Trait("Requirement", "V-14")]
    public async Task Прихована_колонка_не_потрапляє_в_зріз()
    {
        var slice = await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        // ⛔ Інакше сітка показує її і дає редагувати, хоча експорт її пропускає.
        Assert.DoesNotContain(slice.Columns, c => c.Code == "Note");
        Assert.Contains(slice.Columns, c => c.Code == "Volume");
    }
}
