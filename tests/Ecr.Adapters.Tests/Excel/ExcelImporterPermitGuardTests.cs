// tests/Ecr.Adapters.Tests/Excel/ExcelImporterPermitGuardTests.cs
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
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Імпорт із Excel у таблицю з прив'язкою «рядки з дозволу» (Permits, PM-10): зіставлені колонки (ключ, ліміт,
/// довідкові) з книги не приймаються, факти наявних рядків - приймаються. Нових рядків імпорт не створює узагалі.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати виклик <c>PermitBoundImportGuard.Apply</c> в <c>ExcelImporter.PreviewAsync</c> - червоніє
/// «Таблиця_з_прив_язкою…»; прибрати перевірку зіставленої колонки в охоронці - теж червоніє «Таблиця_з_прив_язкою…».
/// </remarks>
public sealed class ExcelImporterPermitGuardTests
{
    private const long DocumentId = 42;
    private const long TableInstanceId = 500;
    private const int TableDefId = 3;
    private const int VolumeColumnId = 11;
    private const int SubstanceColumnId = 12;
    private const int FactColumnId = 13;
    private const int TemplateVersionId = 222;
    private static readonly PeriodKey Period = new(202601);
    private static readonly JsonSerializerOptions MapOptions = new(JsonSerializerDefaults.Web);

    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly ICellStore _cellStore = Substitute.For<ICellStore>();
    private readonly IRowStore _rowStore = Substitute.For<IRowStore>();
    private readonly IPermitBindingStore _bindings = Substitute.For<IPermitBindingStore>();

    public ExcelImporterPermitGuardTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => ReadScopes.Everything(BuildSnapshot()));
        _access.CanEditSlicesAsync(
                Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>)
                call.ArgAt<IReadOnlyCollection<long>>(1).Distinct().ToDictionary(
                    id => id,
                    _ => (IReadOnlyDictionary<CellAddress, EditDecision>)new Dictionary<CellAddress, EditDecision>()));

        _rowStore.GetTableInstancesAsync(DocumentId, Period, Arg.Any<CancellationToken>())
            .Returns(new List<TableInstanceRef> { new(TableInstanceId, DocumentId, TableDefId, TemplateVersionId, Period.Value) });
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(BuildSnapshot());
        _rowStore.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [TableInstanceId] = new Dictionary<string, long> { ["P1"] = 9001L },
            });
        _rowStore.GetRowVersionsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [TableInstanceId] = new Dictionary<string, string> { ["P1"] = "0xAA" },
            });
        _cellStore.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Таблиця_з_прив_язкою_приймає_лише_факти_наявних_рядків()
    {
        Bind(active: true);
        using var workbook = BuildWorkbook();

        var preview = await Importer().PreviewAsync(DocumentId, workbook, CancellationToken.None);

        // Єдина допустима зміна - факт наявного рядка дозволу.
        var change = Assert.Single(preview.Changes);
        Assert.Equal(("P1", "Fact"), (change.RowKey, change.ColumnCode));

        // Ключ і ліміт наявного рядка - зіставлені колонки. Нового рядка імпорт не створює й без дозволу (R-B2 закритий).
        Assert.Contains(preview.Rejected, r => r is { RowKey: "P1", ColumnCode: "Substance", MessageKey: "deny.ColumnReadOnly" });
        Assert.Contains(preview.Rejected, r => r is { RowKey: "P1", ColumnCode: "Volume", MessageKey: "deny.ColumnReadOnly" });
        Assert.Contains(preview.Rejected, r => r is { RowKey: "NEW", ReasonCode: "ECR-ROW-0404" });
        Assert.DoesNotContain(preview.Changes, c => c.RowKey == "NEW");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Таблиця_без_чинної_прив_язки_імпортується_як_раніше_і_прив_язки_не_читаються()
    {
        Bind(active: false);
        using var workbook = BuildWorkbook();

        var preview = await Importer().PreviewAsync(DocumentId, workbook, CancellationToken.None);

        // Без прив'язки: три значення P1; рядок NEW імпорт не створює і без дозволу (ECR-ROW-0404) - жодних відмов охоронця.
        Assert.Equal(3, preview.Changes.Count);
        Assert.DoesNotContain(preview.Rejected, r => r.MessageKey is "deny.RowReadOnly" or "deny.ColumnReadOnly");
        await _bindings.DidNotReceive().ListActiveRowsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    private void Bind(bool active)
    {
        _bindings.AnyActiveRowsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(active);

        var binding = new PermitSheetBinding(
            EcrCode.Create("B1"), TemplateVersionId, TableDefId, 1, 1, PermitBindingMode.Rows, PermitSelectionRule.ByFacilityPeriodEnd, null);
        binding.AddColumn(new PermitSheetBindingColumn(PermitColumnRole.RowKey, EcrCode.Create("Substance"), SubstanceColumnId, 0));
        binding.AddColumn(new PermitSheetBindingColumn(PermitColumnRole.Limit, EcrCode.Create("Limit"), VolumeColumnId, 1));
        var view = new PermitBindingView(
            binding, binding.Columns, "Main", "AIR", "Emissions", null,
            new Dictionary<int, string> { [SubstanceColumnId] = "Substance", [VolumeColumnId] = "Volume" });
        _bindings.ListActiveRowsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<PermitBindingView> { view });
    }

    private static TemplateVersionSnapshot BuildSnapshot()
    {
        var volume = new ColumnDef(TableDefId, EcrCode.Create("Volume"), Name("Volume"), 1, CellDataType.Decimal);
        var substance = new ColumnDef(TableDefId, EcrCode.Create("Substance"), Name("Substance"), 2, CellDataType.String);
        var fact = new ColumnDef(TableDefId, EcrCode.Create("Fact"), Name("Fact"), 3, CellDataType.Decimal);
        SetId(volume, VolumeColumnId);
        SetId(substance, SubstanceColumnId);
        SetId(fact, FactColumnId);

        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("Water"), Name("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Name("Main"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        SetId(table, TableDefId);
        table.AddColumn(volume);
        table.AddColumn(substance);
        table.AddColumn(fact);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            TemplateVersionId, 0, [sheet],
            new Dictionary<int, ColumnDef> { [VolumeColumnId] = volume, [SubstanceColumnId] = substance, [FactColumnId] = fact },
            new Dictionary<(int, string), RowDef>());
    }

    private static void SetId(Entity<int> entity, int id) => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p1", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string> { "Registry.View" }, Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    /// <summary>Книга: P1 (наявний) з трьома значеннями, NEW (нового рядка немає) з одним.</summary>
    private static MemoryStream BuildWorkbook()
    {
        var map = new ExcelWorkbookMap(
            DocumentId, Period.Value, TemplateVersionId,
            [
                new ExcelTableBlock(
                    TableInstanceId, TableDefId, "Main", "Data", HeaderRow: 2,
                    Columns:
                    [
                        new ExcelColumnRef(VolumeColumnId, "Volume", Number: 1, IsCalculated: false, LookupRegistryDefId: null),
                        new ExcelColumnRef(SubstanceColumnId, "Substance", Number: 2, IsCalculated: false, LookupRegistryDefId: null),
                        new ExcelColumnRef(FactColumnId, "Fact", Number: 3, IsCalculated: false, LookupRegistryDefId: null),
                    ],
                    Rows: [new ExcelRowRef("P1", Number: 3), new ExcelRowRef("NEW", Number: 4)]),
            ]);

        using var workbook = new XLWorkbook();
        var data = workbook.Worksheets.Add("Data");
        data.Cell(3, 1).Value = "999";
        data.Cell(3, 2).Value = "Renamed";
        data.Cell(3, 3).Value = "5";
        data.Cell(4, 1).Value = "7";

        workbook.Worksheets.Add(ExcelWorkbookMap.SheetName).Cell(1, 1).Value = JsonSerializer.Serialize(map, MapOptions);

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private ExcelImporter Importer()
    {
        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        var patchRegistries = Substitute.For<IRegistryStore>();
        var patchHeaders = Substitute.For<IDocumentHeaderStore>();
        patchHeaders.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        var uow = Substitute.For<IUnitOfWork>();
        uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
           .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _metadata, Substitute.For<IRegistryStore>(), _access, _user, Substitute.For<IImportPreviewStore>(),
            new PatchCellsHandler(
                Substitute.For<ICellStore>(), Substitute.For<IRowStore>(), Substitute.For<IDocumentStore>(),
                Substitute.For<IPeriodStore>(), Substitute.For<IMetadataCache>(), Substitute.For<IAccessDecisionService>(),
                new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
                methodologies, patchRegistries, patchHeaders,
                Substitute.For<IAuditWriter>(), Substitute.For<IAuditReader>(),
                Substitute.For<IBackgroundJobScheduler>(), Substitute.For<IUnitOfWork>(),
                Substitute.For<ICurrentUser>(), Substitute.For<IClock>(), Substitute.For<ISheetEditGate>(),
                Substitute.For<IUnitCatalog>()),
            new ImportDiffBuilder(), _cellStore, _rowStore, uow, Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<ISheetEditGate>(), permitBindings: _bindings);
    }
}
