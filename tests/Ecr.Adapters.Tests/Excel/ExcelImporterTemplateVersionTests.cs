// tests/Ecr.Adapters.Tests/Excel/ExcelImporterTemplateVersionTests.cs
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
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

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Q-234 (аудит фази 3, Excel-обмін): <c>ExcelImporter.PreviewAsync</c> має
/// резолвити версію шаблону і належність <c>TableInstanceId</c> з БД за
/// <c>documentId</c>/періодом — так само, як <c>IRowStore.ResolveTableInstanceAsync</c>
/// робить це для звичайного PATCH, — а не довіряти значенням, записаним у
/// вивантажену книгу на момент експорту.
/// </summary>
/// <remarks>
/// ⛔ До фікса код читав <c>map.TemplateVersionId</c> — поле з файлу, який
/// принципово є вхідними даними користувача (той самий статус, що й тіло
/// HTTP-запиту). Публікація нової версії шаблону між експортом і імпортом
/// (звичайна подія за рік звітності) означала, що diff будувався проти
/// структури, якої вже нема: тип комірки міг визначитися неправильно, а
/// <c>ApplyAsync</c> (він завжди резолвить ПОТОЧНУ версію через
/// <c>PatchCellsHandler</c>) міг відмовити батч посеред застосування —
/// уже ПІСЛЯ того, як перегляд показав користувачу, що все гаразд.
/// </remarks>
public sealed class ExcelImporterTemplateVersionTests
{
    private const long DocumentId = 42;
    private const long CurrentTableInstanceId = 500;
    private const int TableDefId = 3;
    private const int VolumeColumnId = 11;
    private const int StaleTemplateVersionId = 111;
    private const int CurrentTemplateVersionId = 222;
    private static readonly PeriodKey Period = new(202601);
    private static readonly JsonSerializerOptions MapOptions = new(JsonSerializerDefaults.Web);

    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IImportPreviewStore _previews = Substitute.For<IImportPreviewStore>();
    private readonly ICellStore _cellStore = Substitute.For<ICellStore>();
    private readonly IRowStore _rowStore = Substitute.For<IRowStore>();

    public ExcelImporterTemplateVersionTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());

        // ⚠ Порожній словник рішень — жодна адреса не заборонена явно
        // (той самий прийом, що й у PatchCellsTests): ImportDiffBuilder
        // трактує відсутність запису як «дозволено».
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<CellAddress, EditDecision>());

        // ⛔ Поточна (жива) версія шаблону — ЄДИНЕ, що відповідає за
        // TemplateVersionId цього документа. Файл каже інше (111), і саме
        // це різне значення й перевіряє тест.
        _rowStore.GetTableInstancesAsync(DocumentId, Period, Arg.Any<CancellationToken>())
            .Returns(new List<TableInstanceRef>
            {
                new(CurrentTableInstanceId, DocumentId, TableDefId, CurrentTemplateVersionId, Period.Value),
            });

        _metadata.GetAsync(CurrentTemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(BuildSnapshot());

        _rowStore.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [CurrentTableInstanceId] = new Dictionary<string, long> { ["R1"] = 9001L },
            });

        _rowStore.GetRowVersionsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [CurrentTableInstanceId] = new Dictionary<string, string> { ["R1"] = "0xAA" },
            });

        _cellStore.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>());
    }

    private static TemplateVersionSnapshot BuildSnapshot()
    {
        var column = new ColumnDef(
            tableDefId: TableDefId, EcrCode.Create("Volume"), Name("Volume"), 1, CellDataType.Decimal);
        SetId(column, VolumeColumnId);

        var sheet = new SheetDef(
            templateVersionId: CurrentTemplateVersionId, EcrCode.Create("Water"), Name("Water"), 1);

        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"), Name("Main"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        SetId(table, TableDefId);
        table.AddColumn(column);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            TemplateVersionId: CurrentTemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [VolumeColumnId] = column },
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static void SetId(Entity<int> entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p1", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    /// <summary>Книга з картою: <paramref name="tableInstanceId"/> лишається параметром навмисно — саме ним другий тест підміняє належність блоку.</summary>
    private static MemoryStream BuildWorkbook(long tableInstanceId)
    {
        var map = new ExcelWorkbookMap(
            DocumentId, Period.Value, StaleTemplateVersionId,
            [
                new ExcelTableBlock(
                    tableInstanceId, TableDefId, "Main", "Data", HeaderRow: 2,
                    Columns: [new ExcelColumnRef(VolumeColumnId, "Volume", Number: 1, IsCalculated: false, LookupRegistryDefId: null)],
                    Rows: [new ExcelRowRef("R1", Number: 3)]),
            ]);

        using var workbook = new XLWorkbook();
        var data = workbook.Worksheets.Add("Data");
        data.Cell(3, 1).Value = "999";

        var mapSheet = workbook.Worksheets.Add(ExcelWorkbookMap.SheetName);
        mapSheet.Cell(1, 1).Value = JsonSerializer.Serialize(map, MapOptions);

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private ExcelImporter Importer() => new(
        _metadata, _registries, _access, _user, _previews,
        new PatchCellsHandler(
            Substitute.For<ICellStore>(), Substitute.For<IRowStore>(), Substitute.For<IDocumentStore>(),
            Substitute.For<IPeriodStore>(), Substitute.For<IMetadataCache>(), Substitute.For<IAccessDecisionService>(),
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            Substitute.For<IAuditWriter>(), Substitute.For<IBackgroundJobScheduler>(), Substitute.For<IUnitOfWork>(),
            Substitute.For<ICurrentUser>(), Substitute.For<IClock>()),
        new ImportDiffBuilder(), _cellStore, _rowStore);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Q-234")]
    public async Task Перегляд_бере_версію_шаблону_з_БД_а_не_з_книги()
    {
        using var workbook = BuildWorkbook(CurrentTableInstanceId);

        var preview = await Importer().PreviewAsync(DocumentId, workbook, CancellationToken.None);

        // ⛔ Пряме опудало фікса: якби код досі читав `map.TemplateVersionId`
        // (111), сюди пішов би виклик `GetAsync(111, ...)` — неналаштований
        // саб, що впав би на `null`-знімку ще ДО цих перевірок.
        await _metadata.Received(1).GetAsync(CurrentTemplateVersionId, Arg.Any<CancellationToken>());
        await _metadata.DidNotReceive().GetAsync(StaleTemplateVersionId, Arg.Any<CancellationToken>());

        // Значення з книги (999) не збігається з поточним (комірки немає —
        // `ReadSlicesAsync` повертає порожньо), тож diff повинен показати
        // одну зміну, побудовану проти ЖИВОЇ структури.
        var change = Assert.Single(preview.Changes);
        Assert.Equal("R1", change.RowKey);
        Assert.Equal("Volume", change.ColumnCode);
        Assert.Equal(999m, change.NewValue);
        Assert.Empty(preview.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Q-234")]
    public async Task Блок_із_чужим_TableInstanceId_відхиляється_без_читання_його_даних()
    {
        const long foreignTableInstanceId = 777;
        using var workbook = BuildWorkbook(foreignTableInstanceId);

        var preview = await Importer().PreviewAsync(DocumentId, workbook, CancellationToken.None);

        Assert.Empty(preview.Changes);
        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal("ECR-IMP-0422", rejection.ReasonCode);

        // ⛔ Захист від підміни: TableInstanceId, якого немає серед
        // екземплярів ЦЬОГО документа за цей період, не повинен потрапляти
        // в пакетне читання рядків/комірок — інакше книга з чужим
        // TableInstanceId (той самий шаблон, інший документ/проєкт) читала б
        // чужі дані ще до будь-якого рішення про доступ.
        await _cellStore.Received(1).ReadSlicesAsync(
            Arg.Is<IReadOnlyList<long>>(ids => ids.Count == 0), Arg.Any<CancellationToken>());
    }
}
