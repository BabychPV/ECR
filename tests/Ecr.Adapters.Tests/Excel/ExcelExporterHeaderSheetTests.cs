// tests/Ecr.Adapters.Tests/Excel/ExcelExporterHeaderSheetTests.cs
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
/// A2-12: експорт xlsx містить значення полів шапки з підписами — окремим видимим
/// аркушем «Header» ПІСЛЯ аркушів даних; зворотний імпорт його не бачить.
/// </summary>
/// <remarks>
/// Мутації: прибрати <c>WriteHeaderSheetAsync</c> з <c>ExportAsync</c> — тести аркуша червоні;
/// прибрати фільтр <c>!f.IsDeleted</c> у <c>DocumentHeaderExport.ReadAsync</c> — тест видаленого поля червоний;
/// покласти блок шапки в аркуш даних (рядки таблиці зсуваються) — тести імпорту червоні.
/// </remarks>
public sealed class ExcelExporterHeaderSheetTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int ColumnId = 11;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();

    public ExcelExporterHeaderSheetTests() => Arrange(withHeaderFields: true);

    private TemplateVersionSnapshot _snapshot = null!;

    private void Arrange(bool withHeaderFields)
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        var column = new ColumnDef(TableId, EcrCode.Create("A"), Text("A"), 1, CellDataType.Decimal);
        SetId(column, ColumnId);
        table.AddColumn(column);
        sheet.AddTable(table);

        var fields = new List<HeaderFieldDef>();
        var values = new Dictionary<int, DocumentHeaderValueData>();
        if (withHeaderFields)
        {
            fields.Add(Field(21, "RDATE", 1, CellDataType.Date, "Report date", "Дата отчёта", "Есеп күні"));
            fields.Add(Field(22, "OPERATOR", 2, CellDataType.String, "Operator", "Оператор", "Оператор"));
            fields.Add(Field(23, "QTY", 3, CellDataType.Decimal, "Quantity", "Количество", "Саны"));
            fields.Add(Field(24, "NOTE", 4, CellDataType.String, "Note", "Примечание", "Ескертпе"));
            var removed = Field(25, "OLD", 5, CellDataType.String, "Old field", "Старое поле", "Ескі өріс");
            removed.SoftDelete(1, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
            fields.Add(removed);

            values[21] = new DocumentHeaderValueData { ValueDate = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc) };
            values[22] = new DocumentHeaderValueData { ValueString = "I. Petrenko" };
            values[23] = new DocumentHeaderValueData { ValueNumeric = 12.5m };
            values[25] = new DocumentHeaderValueData { ValueString = "stale" };
        }

        _snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [ColumnId] = column },
            new Dictionary<(int, string), RowDef>())
        {
            HeaderFields = fields,
        };

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [InstanceId] = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002 },
            });
        _rows.GetRowVersionsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [InstanceId] = new Dictionary<string, string> { ["R1"] = "0xAA", ["R2"] = "0xBB" },
            });
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [InstanceId] =
                [
                    new CellRecord(new CellAddress(Period, 1001, ColumnId), TableId, new CellValueData { ValueNumeric = 8m }),
                    new CellRecord(new CellAddress(Period, 1002, ColumnId), TableId, new CellValueData { ValueNumeric = 5m }),
                ],
            });
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(_snapshot);
        _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<int, DocumentHeaderValueData>)values);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Аркуш_шапки_видимий_після_аркуша_даних_із_підписами_кодами_і_типізованими_значеннями()
    {
        using var workbook = await ExportAsync("en");

        // Порядок: дані → шапка → прихована карта; перший видимий аркуш — дані (сценарії тримаються за це).
        Assert.Equal(["Sheet", "Header", "_ecr"], [.. workbook.Worksheets.Select(w => w.Name)]);
        var header = workbook.Worksheet("Header");
        Assert.Equal(XLWorksheetVisibility.Visible, header.Visibility);

        Assert.Equal(["Field", "Code", "Value"], [header.Cell(1, 1).GetString(), header.Cell(1, 2).GetString(), header.Cell(1, 3).GetString()]);

        Assert.Equal("Report date", header.Cell(2, 1).GetString());
        Assert.Equal("RDATE", header.Cell(2, 2).GetString());
        Assert.Equal(new DateTime(2026, 9, 30), header.Cell(2, 3).GetDateTime());

        Assert.Equal("Operator", header.Cell(3, 1).GetString());
        Assert.Equal("I. Petrenko", header.Cell(3, 3).GetString());

        Assert.Equal("Quantity", header.Cell(4, 1).GetString());
        Assert.Equal(12.5, header.Cell(4, 3).GetDouble());

        // Порожнє поле — рядок є, значення порожнє; видалене поле (зі значенням) — не експортується.
        Assert.Equal("Note", header.Cell(5, 1).GetString());
        Assert.True(header.Cell(5, 3).IsEmpty());
        Assert.True(header.Cell(6, 1).IsEmpty());
        Assert.DoesNotContain("stale", header.CellsUsed().Select(c => c.GetString()));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("ru", "Шапка", "Поле", "Код", "Значение", "Дата отчёта", "Оператор")]
    [InlineData("kz", "Тақырып", "Өріс", "Код", "Мәні", "Есеп күні", "Оператор")]
    [InlineData("en", "Header", "Field", "Code", "Value", "Report date", "Operator")]
    public async Task Підписи_блоку_з_каталогу_а_підписи_полів_з_шаблону_мовою_експорту(
        string language, string sheetName, string field, string code, string value, string dateLabel, string operatorLabel)
    {
        using var workbook = await ExportAsync(language, Catalog(language, sheetName, field, code, value));

        var header = workbook.Worksheet(sheetName);
        Assert.Equal([field, code, value], [header.Cell(1, 1).GetString(), header.Cell(1, 2).GetString(), header.Cell(1, 3).GetString()]);
        Assert.Equal(dateLabel, header.Cell(2, 1).GetString());
        Assert.Equal(operatorLabel, header.Cell(3, 1).GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_полів_шапки_у_шаблоні_аркуша_шапки_нема()
    {
        Arrange(withHeaderFields: false);

        using var workbook = await ExportAsync("en");

        Assert.Equal(["Sheet", "_ecr"], [.. workbook.Worksheets.Select(w => w.Name)]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Назва_аркуша_шапки_розводиться_з_аркушем_даних_що_має_те_саме_ім_я()
    {
        // Аркуш даних уже зветься «Header»: аркуш шапки отримує суфікс, а не губить дані й не падає.
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Header"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        sheet.AddTable(table);
        _snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>())
        {
            HeaderFields = [Field(21, "OPERATOR", 1, CellDataType.String, "Operator", "Оператор", "Оператор")],
        };
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(_snapshot);

        using var workbook = await ExportAsync("en");

        Assert.Equal(["Header", "Header~2", "_ecr"], [.. workbook.Worksheets.Select(w => w.Name)]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Незмінена_книга_з_аркушем_шапки_не_дає_ні_змін_ні_відхилень()
    {
        using var workbook = await ExportAsync("en");

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        Assert.Empty(preview.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Правка_аркуша_шапки_імпортом_ігнорується_а_правка_даних_лишається_єдиною_зміною()
    {
        using var workbook = await ExportAsync("en");
        workbook.Worksheet("Header").Cell(3, 3).Value = "Someone else";
        workbook.Worksheet("Sheet").Cell(3, 1).Value = 9;

        var preview = await ImportAsync(workbook);

        var change = Assert.Single(preview.Changes);
        Assert.Equal("R1", change.RowKey);
        Assert.Equal("A", change.ColumnCode);
        Assert.Empty(preview.Rejected);
    }

    private async Task<XLWorkbook> ExportAsync(string language, IUiStringCatalog? catalog = null)
    {
        var exporter = new ExcelExporter(
            _cells, _rows, _metadata, Substitute.For<IStyleCatalog>(), Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator(), headers: _headers, catalog: catalog);

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: false, IncludeStyles: false, Language: language, PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        var copy = new MemoryStream();
        await output.CopyToAsync(copy);
        copy.Position = 0;
        return new XLWorkbook(copy);
    }

    private static IUiStringCatalog Catalog(string language, string sheet, string field, string code, string value)
    {
        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetAsync(language, Arg.Any<CancellationToken>()).Returns(new UiStringCatalog(
            language, 1,
            new Dictionary<string, string>
            {
                [HeaderExportLabels.SheetKey] = sheet,
                [HeaderExportLabels.FieldKey] = field,
                [HeaderExportLabels.CodeKey] = code,
                [HeaderExportLabels.ValueKey] = value,
            }));
        return catalog;
    }

    private async Task<ImportPreview> ImportAsync(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var results = Substitute.For<ICalculationResultStore>();
        results.ReadCurrentAsync(DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>()).Returns([]);

        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
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

        var expressionHeaders = Substitute.For<IDocumentHeaderStore>();
        expressionHeaders.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        var importer = new ExcelImporter(
            _metadata, Substitute.For<IRegistryStore>(), access, user, Substitute.For<IImportPreviewStore>(),
            new PatchCellsHandler(
                Substitute.For<ICellStore>(), Substitute.For<IRowStore>(), Substitute.For<IDocumentStore>(),
                Substitute.For<IPeriodStore>(), Substitute.For<IMetadataCache>(), Substitute.For<IAccessDecisionService>(),
                new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
                Substitute.For<IMethodologyStore>(), Substitute.For<IRegistryStore>(), expressionHeaders,
                Substitute.For<IAuditWriter>(), Substitute.For<IAuditReader>(),
                Substitute.For<IBackgroundJobScheduler>(), Substitute.For<IUnitOfWork>(),
                Substitute.For<ICurrentUser>(), Substitute.For<IClock>(), Substitute.For<ISheetEditGate>(),
                Substitute.For<IUnitCatalog>()),
            new ImportDiffBuilder(), _cells, _rows,
            Substitute.For<IUnitOfWork>(), Substitute.For<IBackgroundJobScheduler>(), Substitute.For<ISheetEditGate>(),
            methodologies, results);

        return await importer.PreviewAsync(DocumentId, stream, CancellationToken.None);
    }

    private static HeaderFieldDef Field(int id, string code, int ordinal, CellDataType type, string en, string ru, string kz)
    {
        var field = new HeaderFieldDef(
            TemplateVersionId, EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = en, ["ru"] = ru, ["kz"] = kz }), ordinal, type);
        SetId(field, id);
        return field;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
