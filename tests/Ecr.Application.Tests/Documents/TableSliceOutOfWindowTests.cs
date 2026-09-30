// tests/Ecr.Application.Tests/Documents/TableSliceOutOfWindowTests.cs
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
/// <c>ФВ-2.16</c>, <c>D-239</c>: значок «правка поза вікном» приходить у зрізі
/// (<c>TableSliceDto.OutOfWindowCells</c>), тож переживає перезавантаження.
/// </summary>
/// <remarks>
/// ⚠ Журнал тут — заглушка: предмет цих тестів — перетворення адрес журналу
/// на ключі сітки і відсікання того, чого зріз не показує. Сам запит
/// «остання зміна поза вікном» доводить <c>AuditReaderOutOfWindowCellsTests</c>
/// (Infrastructure, жива СУБД).
/// </remarks>
public sealed class TableSliceOutOfWindowTests
{
    private const long Document = 700;
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int VolumeId = 11;
    private const int SecretId = 12;
    private const long Row1 = 1001;
    private const long Row2 = 1002;

    /// <summary>Рядок іншої таблиці того самого документа.</summary>
    private const long ForeignRow = 9_999;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IAuditReader _audit = Substitute.For<IAuditReader>();
    private readonly TemplateVersionSnapshot _snapshot;

    public TableSliceOutOfWindowTests()
    {
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, 3);
        var volume = new ColumnDef(3, EcrCode.Create("Volume"), Text("Volume"), 1, CellDataType.Decimal);
        SetId(volume, VolumeId);
        var secret = new ColumnDef(3, EcrCode.Create("Secret"), Text("Secret"), 2, CellDataType.Decimal);
        SetId(secret, SecretId);
        table.AddColumn(volume);
        table.AddColumn(secret);
        sheet.AddTable(table);

        _snapshot = new TemplateVersionSnapshot(2, 0, [sheet],
            new Dictionary<int, ColumnDef> { [VolumeId] = volume, [SecretId] = secret },
            new Dictionary<(int, string), RowDef>());

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, Document, 3, 2, Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(_snapshot);
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["R1"] = Row1, ["R2"] = Row2 });
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string> { ["R1"] = "0x0A", ["R2"] = "0x0B" });
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());
        _cells.ReadSliceAsync(TableInstance, new PeriodKey(Period), Arg.Any<CancellationToken>())
              .Returns(new List<CellRecord>());

        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(ReadScopes.Everything(_snapshot));

        _methodologies.GetMethodologyIdsBoundToTableAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                      .Returns(new List<int>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Адреси_журналу_стають_ключами_сітки_рядок_колонка()
    {
        Journal((Row2, VolumeId), (Row1, VolumeId));

        var slice = await Handler(_audit).HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        // ⛔ Мутація «повернути [] замість читання журналу» (стан до ФВ-2.16 у
        // зрізі) валить саме цей рядок: значок знову зник би після F5.
        Assert.Equal(["R1:Volume", "R2:Volume"], slice.OutOfWindowCells);

        // Один запит на зріз — за документом і періодом екземпляра.
        await _audit.Received(1).ReadOutOfWindowCellsAsync(Document, Period, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Рядок_іншої_таблиці_документа_у_зріз_не_потрапляє()
    {
        // Журнал знає документ, а не екземпляр таблиці: адреса з іншої таблиці
        // того самого документа приходить із запиту і мусить відсіятися тут.
        Journal((ForeignRow, VolumeId), (Row1, VolumeId));

        var slice = await Handler(_audit).HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Equal(["R1:Volume"], slice.OutOfWindowCells);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Заборонена_колонка_значка_не_отримує()
    {
        // ⛔ S6: код забороненої колонки не має з'являтися в зрізі жодним полем.
        // Мутація «прибрати перевірку CanReadColumn» віддає "R1:Secret".
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(DocumentReadScope.For(
                   new AccessBuilder()
                       .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
                       .Deny(ResourceKind.Column, SecretId)
                       .Build(),
                   AccessBuilder.ProjectId,
                   _snapshot));
        Journal((Row1, SecretId), (Row1, VolumeId));

        var slice = await Handler(_audit).HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Equal(["R1:Volume"], slice.OutOfWindowCells);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Без_журналу_поле_порожнє_а_не_відсутнє()
    {
        var slice = await Handler(audit: null).HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.NotNull(slice.OutOfWindowCells);
        Assert.Empty(slice.OutOfWindowCells);
    }

    private void Journal(params (long TableRowId, int ColumnDefId)[] cells)
        => _audit.ReadOutOfWindowCellsAsync(Document, Period, Arg.Any<CancellationToken>())
                 .Returns(cells);

    private GetTableSliceHandler Handler(IAuditReader? audit)
        => new(_rows, _cells, _metadata, Units(), _access, _methodologies, _periods, Styles(), audit: audit);

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T e, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(e, id);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private static IUnitCatalog Units()
    {
        var catalogue = Substitute.For<IUnitCatalog>();
        catalogue.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        return catalogue;
    }

    private static IStyleCatalog Styles()
    {
        var catalogue = Substitute.For<IStyleCatalog>();
        catalogue.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, StyleDef>());

        return catalogue;
    }
}
