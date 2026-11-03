// tests/Ecr.Adapters.Tests/Excel/ExcelSheetCopiesTests.cs
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
/// RS (копії аркушів, рішення людини 03.11): експорт кладе оригінал і кожну копію на ОКРЕМИЙ аркуш книги «&lt;Назва&gt; N»,
/// блок карти кожного ключується екземпляром, а зворотний імпорт повертає правку в екземпляр СВОГО аркуша.
/// </summary>
/// <remarks>
/// ⛔ Мутація (локально, не комітиться): ключувати екземпляр за <c>TableDefId</c> (<c>byTableCopy</c> без копії) — експорт
/// падає на дублі ключа або кладе дані копії поверх оригінала; тести червоні.
/// </remarks>
public sealed class ExcelSheetCopiesTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long OriginalInstance = 729;
    private const long CopyInstance = 730;
    private const long CopyId = 77;
    private const int TableId = 1;
    private const int InputColumnId = 11;
    private const int DoubledColumnId = 12;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IDocumentSheetCopyStore _copies = Substitute.For<IDocumentSheetCopyStore>();
    private readonly TemplateVersionSnapshot _snapshot = Snapshot("Stacks");

    public ExcelSheetCopiesTests() => Arrange(null);

    private void Arrange(LocalizedText? nameOverride)
    {
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new TableInstanceRef(OriginalInstance, DocumentId, TableId, TemplateVersionId, PeriodKeyValue),
                new TableInstanceRef(CopyInstance, DocumentId, TableId, TemplateVersionId, PeriodKeyValue, CopyId),
            ]);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [OriginalInstance] = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002 },
                [CopyInstance] = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 2001, ["R2"] = 2002 },
            });
        _rows.GetRowVersionsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [OriginalInstance] = new Dictionary<string, string> { ["R1"] = "0xAA", ["R2"] = "0xAB" },
                [CopyInstance] = new Dictionary<string, string> { ["R1"] = "0xBA", ["R2"] = "0xBB" },
            });
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [OriginalInstance] =
                [
                    new CellRecord(new CellAddress(Period, 1001, InputColumnId), TableId, new CellValueData { ValueNumeric = 5m }),
                    new CellRecord(new CellAddress(Period, 1002, InputColumnId), TableId, new CellValueData { ValueNumeric = 6m }),
                ],
                [CopyInstance] =
                [
                    new CellRecord(new CellAddress(Period, 2001, InputColumnId), TableId, new CellValueData { ValueNumeric = 50m }),
                ],
            });
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(_snapshot);
        _copies.ListGroupsAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns([new SheetCopyGroupInfo(1, nameOverride, [new SheetCopyOrdinal(CopyId, 2)])]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "RS-1103")]
    public async Task Оригінал_і_копія_лягають_на_окремі_аркуші_з_назвою_і_номером_а_карта_ключує_за_екземпляром()
    {
        using var workbook = await ExportAsync();

        var visible = workbook.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible).Select(w => w.Name).ToList();
        Assert.Equal(["Stacks 1", "Stacks 2"], visible);

        // Заголовок — рядок 2, дані — з рядка 3: оригінал 5/6, копія 50 і порожньо.
        Assert.Equal(5d, workbook.Worksheet("Stacks 1").Cell(3, 1).GetDouble());
        Assert.Equal(50d, workbook.Worksheet("Stacks 2").Cell(3, 1).GetDouble());
        Assert.True(workbook.Worksheet("Stacks 2").Cell(4, 1).IsEmpty());

        var map = ExcelExportHarness.Map(workbook);
        Assert.Equal(
            [(OriginalInstance, "Stacks 1"), (CopyInstance, "Stacks 2")],
            map.Tables.Select(t => (t.TableInstanceId, t.SheetName)));

        // Формула копії адресує ЇЇ комірки, оригіналу — його.
        Assert.Contains("Stacks 1", workbook.Worksheet("Stacks 1").Cell(3, 2).FormulaA1, StringComparison.Ordinal);
        Assert.Contains("Stacks 2", workbook.Worksheet("Stacks 2").Cell(3, 2).FormulaA1, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "RS-1103")]
    public async Task Перейменування_групи_міняє_назви_всіх_аркушів_і_довга_назва_лишає_номер()
    {
        var @override = new LocalizedText(new Dictionary<string, string> { ["en"] = "Piles of the northern quarry yard" });
        Arrange(@override);

        using var workbook = await ExportAsync();

        var visible = workbook.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible).Select(w => w.Name).ToList();

        // Межа Excel — 31 символ: різеться назва, а " 1" / " 2" лишаються.
        Assert.Equal(2, visible.Count);
        Assert.All(visible, name => Assert.True(name.Length <= 31, name));
        Assert.EndsWith(" 1", visible[0], StringComparison.Ordinal);
        Assert.EndsWith(" 2", visible[1], StringComparison.Ordinal);
        Assert.StartsWith("Piles of the northern", visible[1], StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "RS-1103")]
    public async Task Незмінена_книга_не_дає_змін_а_правка_копії_порівнюється_з_даними_копії_а_не_оригіналу()
    {
        using var untouched = await ExportAsync();
        var preview = await ImportAsync(untouched);
        Assert.Empty(preview.Changes);
        Assert.Empty(preview.Rejected);

        using var edited = await ExportAsync();
        edited.Worksheet("Stacks 2").Cell(3, 1).Value = 51;
        var copyPreview = await ImportAsync(edited);

        // Було 50 (дані КОПІЇ), а не 5 (оригінал з тим самим RowKey) — імпорт іде за екземпляром блоку.
        var change = Assert.Single(copyPreview.Changes);
        Assert.Equal(("R1", "A", 50m), (change.RowKey, change.ColumnCode, Convert.ToDecimal(change.OldValue, System.Globalization.CultureInfo.InvariantCulture)));

        using var originalEdited = await ExportAsync();
        originalEdited.Worksheet("Stacks 1").Cell(3, 1).Value = 7;
        var originalPreview = await ImportAsync(originalEdited);
        var originalChange = Assert.Single(originalPreview.Changes);
        Assert.Equal(5m, Convert.ToDecimal(originalChange.OldValue, System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<XLWorkbook> ExportAsync()
    {
        var exporter = new ExcelExporter(
            _cells, _rows, _metadata, Substitute.For<IStyleCatalog>(), Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator(), sheetCopies: _copies);

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: true, IncludeStyles: false, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        var copy = new MemoryStream();
        await output.CopyToAsync(copy);
        copy.Position = 0;

        return new XLWorkbook(copy);
    }

    private async Task<ImportPreview> ImportAsync(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string> { "Registry.View" }, Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        });
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => ReadScopes.Everything(_snapshot));
        access.CanEditSlicesAsync(
                Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>)
                call.ArgAt<IReadOnlyCollection<long>>(1).Distinct().ToDictionary(
                    id => id,
                    _ => (IReadOnlyDictionary<CellAddress, EditDecision>)new Dictionary<CellAddress, EditDecision>()));

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        var importer = new ExcelImporter(
            _metadata, Substitute.For<IRegistryStore>(), access, user, Substitute.For<IImportPreviewStore>(),
            new PatchCellsHandler(
                Substitute.For<ICellStore>(), Substitute.For<IRowStore>(), Substitute.For<IDocumentStore>(),
                Substitute.For<IPeriodStore>(), Substitute.For<IMetadataCache>(), Substitute.For<IAccessDecisionService>(),
                new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
                Substitute.For<IMethodologyStore>(), Substitute.For<IRegistryStore>(), headers,
                Substitute.For<IAuditWriter>(), Substitute.For<IAuditReader>(),
                Substitute.For<IBackgroundJobScheduler>(), Substitute.For<IUnitOfWork>(),
                Substitute.For<ICurrentUser>(), Substitute.For<IClock>(), Substitute.For<ISheetEditGate>(),
                Substitute.For<IUnitCatalog>()),
            new ImportDiffBuilder(), _cells, _rows,
            Substitute.For<IUnitOfWork>(), Substitute.For<IBackgroundJobScheduler>(), Substitute.For<ISheetEditGate>(),
            Substitute.For<IMethodologyStore>(), Substitute.For<ICalculationResultStore>());

        return await importer.PreviewAsync(DocumentId, stream, CancellationToken.None);
    }

    private static TemplateVersionSnapshot Snapshot(string sheetName)
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text(sheetName), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        var input = new ColumnDef(TableId, EcrCode.Create("A"), Text("A"), 1, CellDataType.Decimal);
        SetId(input, InputColumnId);
        var doubled = new ColumnDef(TableId, EcrCode.Create("B"), Text("B"), 2, CellDataType.Formula);
        SetId(doubled, DoubledColumnId);
        table.AddColumn(input);
        table.AddColumn(doubled);

        var formula = new FormulaDef(TableId, FormulaScope.Column, "[A] * 2", ExpressionDialect.Template);
        SetId(formula, 100);
        formula.AssignColumn(DoubledColumnId);
        table.AddFormula(formula);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [InputColumnId] = input, [DoubledColumnId] = doubled },
            new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
