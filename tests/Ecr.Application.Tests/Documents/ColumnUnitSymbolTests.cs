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
/// Позначення одиниці доходить до клієнта (<c>ФВ-16.1</c>).
/// </summary>
/// <remarks>
/// ⛔ Поле <c>UnitSymbol</c> було оголошене в контракті, зайняте на екрані
/// версії шаблону — і <b>завжди порожнє</b>: обидва обробники передавали
/// <c>UnitSymbol: null</c>. Оператор бачив «12» і не знав, кілограми це чи
/// тонни, у системі, вся суть якої в тому, що кожне число має одиницю. Тонни
/// під виглядом кілограмів — помилка в тисячу разів, і помічає її регулятор.
///
/// ⚠ Дефект мовчазний за побудовою: `null` у необов'язковому полі виглядає як
/// «одиниці немає», а не як «одиницю забули віддати». Знайдено зворотним
/// проходом аудиту — пошуком адрес сервера без споживача в інтерфейсі:
/// <c>GET /api/v1/units</c> не викликав ніхто, і питання «а хто ж тоді
/// розв'язує позначення» відповіді не мало.
/// </remarks>
public sealed class ColumnUnitSymbolTests
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

    public ColumnUnitSymbolTests()
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
    }

    private GetTableSliceHandler Handler() => new(_rows, _cells, _metadata, _units, _access);

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
    [Trait("Requirement", "ФВ-16.1")]
    public async Task Колонка_з_одиницею_несе_її_позначення()
    {
        var slice = await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        var volume = slice.Columns.Single(c => c.Code == "Volume");

        Assert.Equal(CubicMetre, volume.UnitId);
        Assert.Equal("m3", volume.UnitSymbol);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Колонка_без_одиниці_лишається_без_позначення()
    {
        var slice = await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        var note = slice.Columns.Single(c => c.Code == "Note");

        // ⚠ Саме `null`, а не порожній рядок: безрозмірна колонка й колонка,
        // одиницю якої не знайшли в довіднику, мають виглядати однаково —
        // без підпису, а не з підписом «».
        Assert.Null(note.UnitId);
        Assert.Null(note.UnitSymbol);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Довідник_читається_один_раз_на_зріз()
    {
        await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        // ⛔ Не по колонці: шістдесят колонок дали б шістдесят походів у
        // довідник там, де на весь зріз відведено 1.5 с (tz/08 §8.2).
        await _units.Received(1).GetAsync(Arg.Any<CancellationToken>());
    }
}
