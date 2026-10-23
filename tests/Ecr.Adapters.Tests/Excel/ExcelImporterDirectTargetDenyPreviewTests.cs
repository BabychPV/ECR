// tests/Ecr.Adapters.Tests/Excel/ExcelImporterDirectTargetDenyPreviewTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// S18 (рішення людини 23.10: «Deny блокує і вибір запису»): перегляд імпорту Excel для колонки Lookup з ПРЯМОЮ ціллю
/// (не групою), на реєстр якої виконавець має Deny, відповідає на код ІСНУЮЧОГО запису так само, як на код неіснуючого -
/// інакше перегляд розкриває існування запису (apply відхилив би його як missingEntry через
/// <c>LookupTargetMatcher.Belongs</c>). Без Deny існуючий код дає зміну.
/// </summary>
/// <remarks>
/// Мутація (червоніє): у <c>LookupTargetEntries.ListAsync</c> прибрати гілку Deny для прямої цілі - тест Deny-цілі.
/// </remarks>
public sealed class ExcelImporterDirectTargetDenyPreviewTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int ColumnId = 11;
    private const int Target = 10;
    private const long RowId = 1001;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly Dictionary<string, string> _savedPlans = [];
    private TemplateVersionSnapshot _snapshot = null!;
    private bool _denyTarget;

    public ExcelImporterDirectTargetDenyPreviewTests()
    {
        BatchStoreStubs.DelegateToSingle(_rows, _cells, _access);
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        var column = new ColumnDef(TableId, EcrCode.Create("Permit"), Text("Permit"), 1, CellDataType.Lookup);
        SetId(column, ColumnId);
        column.SetLookup(Target);
        table.AddColumn(column);
        sheet.AddTable(table);
        _snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet], new Dictionary<int, ColumnDef> { [ColumnId] = column },
            new Dictionary<(int, string), RowDef>());

        var definition = new RegistryDef(EcrCode.Create("PRM_WATER_A"), Text("PRM_WATER_A"), isTemporal: true);
        SetId(definition, Target);
        _registries.FindDefinitionByIdAsync(Target, Arg.Any<CancellationToken>()).Returns(definition);
        _registries.ListEntriesAsync(Target, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([Entry(Target, 2001, "N100")]));
        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).Select(id => new RegistryEntryStanding(
                id, Target, true, false, null, null)).ToList());

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [InstanceId] = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = RowId },
            });
        _rows.GetRowVersionsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [InstanceId] = new Dictionary<string, string> { ["R1"] = "0xAA" },
            });
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>> { [InstanceId] = [] });
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(_snapshot);

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(_ => new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
            Denies = _denyTarget ? new HashSet<string> { "Registry:" + Target } : new HashSet<string>(),
            RoleIds = new HashSet<int>(),
        });
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => ReadScopes.Everything(_snapshot));
        _access.CanEditSlicesAsync(Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>)
                call.ArgAt<IReadOnlyCollection<long>>(1).Distinct().ToDictionary(
                    id => id, _ => (IReadOnlyDictionary<CellAddress, EditDecision>)new Dictionary<CellAddress, EditDecision>()));
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _access.CanEditCellsAsync(
                Arg.Any<AccessProfile>(), InstanceId, Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<CellAddress>>(3).ToDictionary(a => a, _ => EditDecision.Allow()));

        _methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns([]);
        _methodologies.GetMethodologyIdsBoundToTableAsync(TableId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _periods.FindPeriodBoundsAsync(DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));
        _rows.ResolveTableInstanceAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue));
        _rows.GetRowsAsync(InstanceId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new List<RowState> { new("R1", RowId, "0xAA", IsOrphaned: false) });
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<CellAddress, CellValueData>());
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_Deny_існуючий_код_дає_зміну()
    {
        _denyTarget = false;

        var preview = await PreviewAsync("N100");

        Assert.Equal(2001L, Assert.Single(preview.Changes).NewValue);
        Assert.Empty(preview.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Deny_на_пряму_ціль_існуючий_код_відповідає_як_неіснуючий()
    {
        _denyTarget = true;

        var existing = await PreviewAsync("N100");
        var missing = await PreviewAsync("N999");

        Assert.Empty(existing.Changes);
        Assert.Empty(missing.Changes);
        var existingRejection = Assert.Single(existing.Rejected);
        var missingRejection = Assert.Single(missing.Rejected);
        Assert.Equal(ImportMessageKeys.ExpectsIdentifier, missingRejection.MessageKey);
        Assert.Equal(missingRejection.MessageKey, existingRejection.MessageKey);
    }

    private async Task<ImportPreview> PreviewAsync(string typed)
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 1).Value = typed;

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        return await Importer().PreviewAsync(DocumentId, stream, CancellationToken.None);
    }

    private ExcelImporter Importer()
    {
        var previews = Substitute.For<IImportPreviewStore>();
        previews.SaveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => { _savedPlans[call.ArgAt<string>(0)] = call.ArgAt<string>(1); return Task.CompletedTask; });
        previews.FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_savedPlans.GetValueOrDefault(call.ArgAt<string>(0))));

        var expressionHeaders = Substitute.For<IDocumentHeaderStore>();
        expressionHeaders.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc));

        return new ExcelImporter(
            _metadata, _registries, _access, _user, previews,
            new PatchCellsHandler(
                _cells, _rows, Substitute.For<IDocumentStore>(), _periods, _metadata, _access,
                new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
                _methodologies, _registries, expressionHeaders, Substitute.For<IAuditWriter>(),
                Substitute.For<IAuditReader>(), _jobs, _uow, _user, clock, Substitute.For<ISheetEditGate>(),
                Substitute.For<IUnitCatalog>()),
            new ImportDiffBuilder(), _cells, _rows, _uow, _jobs, Substitute.For<ISheetEditGate>(),
            _methodologies, Substitute.For<ICalculationResultStore>(), _periods, null);
    }

    private async Task<XLWorkbook> ExportAsync()
    {
        var styles = Substitute.For<IStyleCatalog>();
        styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, StyleDef>());
        var exporter = new ExcelExporter(
            _cells, _rows, _metadata, styles, _registries, new StyleMapper(), new FormulaTranslator());

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: false, IncludeStyles: false, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        var copy = new MemoryStream();
        await output.CopyToAsync(copy);
        copy.Position = 0;
        return new XLWorkbook(copy);
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
