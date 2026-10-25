// tests/Ecr.Adapters.Tests/Excel/ExcelImporterPermitGroupPreviewTests.cs
using System.Text.Json;
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
/// Permits PRMG: <c>ExcelImporter.PreviewAsync</c>/<c>ApplyAsync</c> на РЕАЛЬНОМУ xlsx (книга з справжнього експортера) для
/// колонки, ціль якої - ГРУПА з трьох типів (C - під Deny виконавця). Перевіряється зв'язка <c>LookupsAsync</c> →
/// <c>ImportDiffBuilder</c>: неоднозначний номер (кандидати лише з доступних типів), унікальний номер → зміна, номер лише
/// у Deny-типі → «немає запису» БЕЗ розкриття, число в комірці, застосування.
/// </summary>
/// <remarks>
/// Мутації (червоніють): у <c>ExcelImporter</c> прибрати <c>groups</c> з <c>LookupTargetEntries.ListAsync</c> - усі тести групи;
/// прибрати передачу <c>ambiguousLookups</c> у <c>Build</c> - тест неоднозначності; прибрати <c>profile</c> у
/// <c>ListAsync</c> - тест Deny-типу.
/// </remarks>
public sealed class ExcelImporterPermitGroupPreviewTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int ColumnId = 11;
    private const int Group = 900;
    private const int TypeA = 10;
    private const int TypeB = 11;
    private const int TypeDenied = 12;
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
    private readonly FakePermitGroupResolver _groups = new FakePermitGroupResolver().WithGroup(Group, [TypeA, TypeB, TypeDenied]);
    private TemplateVersionSnapshot _snapshot = null!;

    public ExcelImporterPermitGroupPreviewTests()
    {
        BatchStoreStubs.DelegateToSingle(_rows, _cells, _access);
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
        _snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet], new Dictionary<int, ColumnDef> { [ColumnId] = column },
            new Dictionary<(int, string), RowDef>());

        // N100 є в A, B і C(Deny); N200 - лише в A; N300 - лише в C(Deny).
        Define(TypeA, "PRM_WATER_A", [Entry(TypeA, 2001, "N100"), Entry(TypeA, 2002, "N200")]);
        Define(TypeB, "PRM_WATER_B", [Entry(TypeB, 2101, "N100")]);
        Define(TypeDenied, "PRM_WATER_C", [Entry(TypeDenied, 2201, "N100"), Entry(TypeDenied, 2202, "N300")]);
        _registries.ListEntriesAsync(Group, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([]));
        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).Select(id => new RegistryEntryStanding(
                id, id switch { 2001 or 2002 => TypeA, 2101 => TypeB, _ => TypeDenied }, true, false, null, null)).ToList());

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
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string> { "Registry.View" }, Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string> { "Registry:" + TypeDenied }, RoleIds = new HashSet<int>(),
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
        _cells.ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [RowId] = "0xAB" });
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Неоднозначний_номер_відмовляє_з_кандидатами_лише_доступних_типів_без_назви_Deny_типу()
    {
        var preview = await PreviewAsync("N100");

        Assert.Empty(preview.Changes);
        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal(ImportMessageKeys.AmbiguousPermitNumber, rejection.MessageKey);
        Assert.Contains("N100 — WATER_A", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("N100 — WATER_B", rejection.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("WATER_C", rejection.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("N200", 2002L)]
    [InlineData("N100 — WATER_B", 2101L)]
    [InlineData("N100 - WATER_B", 2101L)]
    public async Task Унікальний_номер_або_підпис_з_будь_яким_тире_дає_зміну(string typed, long expected)
    {
        var preview = await PreviewAsync(typed);

        Assert.Equal(expected, Assert.Single(preview.Changes).NewValue);
        Assert.Empty(preview.Rejected);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("N300")]
    [InlineData("N100 — WATER_C")]
    [InlineData("N999")]
    public async Task Номер_лише_у_Deny_типі_відхиляється_як_невідомий_без_розкриття(string typed)
    {
        var preview = await PreviewAsync(typed);

        Assert.Empty(preview.Changes);
        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal(ImportMessageKeys.ExpectsIdentifier, rejection.MessageKey);
        Assert.DoesNotContain("WATER_C", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Число_в_комірці_не_падає_а_відхиляється_як_невідомий_код()
    {
        var preview = await PreviewAsync(12345);

        Assert.Empty(preview.Changes);
        Assert.Equal(ImportMessageKeys.ExpectsIdentifier, Assert.Single(preview.Rejected).MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Застосування_плану_з_записом_типу_групи_пише_запис_реєстру_члена()
    {
        var preview = await PreviewAsync("N200");

        var response = await Importer().ApplyAsync(DocumentId, preview.PreviewToken, CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        await _cells.Received(1).ApplyAsync(
            Arg.Is<CellChangeSet>(c => c.Upserts.Single().Value.ValueRegistryEntryId == 2002),
            Arg.Any<CancellationToken>());
    }

    private async Task<ImportPreview> PreviewAsync(object typed)
    {
        using var workbook = await ExportAsync();
        var cell = workbook.Worksheet("Sheet").Cell(3, 1);
        if (typed is string text)
        {
            cell.Value = text;
        }
        else
        {
            cell.Value = Convert.ToDouble(typed, System.Globalization.CultureInfo.InvariantCulture);
        }

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
                Substitute.For<IUnitCatalog>(), groups: _groups),
            new ImportDiffBuilder(), _cells, _rows, _uow, _jobs, Substitute.For<ISheetEditGate>(),
            _methodologies, Substitute.For<ICalculationResultStore>(), _periods, null, _groups);
    }

    private async Task<XLWorkbook> ExportAsync()
    {
        var styles = Substitute.For<IStyleCatalog>();
        styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, StyleDef>());
        var exporter = new ExcelExporter(
            _cells, _rows, _metadata, styles, _registries, new StyleMapper(), new FormulaTranslator(), groups: _groups);

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
