// tests/Ecr.Adapters.Tests/Excel/ExcelImportCalculatedKindsRoundTripTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// P1 (Аудит-2, Land doc 96, 12.11): свіжий експорт, імпортований БЕЗ правок, відмовлявся цілком —
/// <c>ECR-CELL-4221 importCalculatedStale</c> на обчислюваних комірках. Кругова перевірка «експорт → імпорт» для
/// кожного виду обчислюваної колонки Land: місячна формула з текстовим результатом
/// (<c>Land_Measure_OperatingTime</c> над String <c>OperatingTimeReleaseOnColdVentYesNo_Mnn</c>), місячна формула з
/// числовим результатом, похідна з довідника (<c>Category1</c> = REGFIELD, текст «6975») і колонка методології
/// (<c>Calculated</c>, оверлей результатів).
/// </summary>
/// <remarks>
/// ⛔ Причина: формула з ТЕКСТОВИМ результатом, що виглядає числом, лежить у <c>ValueString</c>; експорт пише її
/// текстом, а імпорт читає текст числової колонки в <c>decimal</c> і порівнював рядок із числом — «не те саме» при
/// збігу з відбитком експорту, тобто «книга застаріла». Мутація: прибрати гілку <c>TextAsNumber</c> у
/// <c>ImportDiffBuilder.Same</c> — червоні випадки <c>OPTIME</c> і <c>CAT1</c> (місячна числова й методологія —
/// сторожі регресії, зелені й до правки).
/// </remarks>
public sealed class ExcelImportCalculatedKindsRoundTripTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int InputColumnId = 11;
    private const int EmissionColumnId = 12;
    private const int MonthInputColumnId = 13;
    private const int MonthTextFormulaColumnId = 14;
    private const int MonthNumberFormulaColumnId = 15;
    private const int DerivedColumnId = 16;
    private const int MethodologyVersionId = 5;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly ICalculationResultStore _results = Substitute.For<ICalculationResultStore>();
    private readonly TemplateVersionSnapshot _snapshot = Snapshot();

    public ExcelImportCalculatedKindsRoundTripTests()
    {
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002 };

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [InstanceId] = rowIds });
        _rows.GetRowVersionsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [InstanceId] = new Dictionary<string, string> { ["R1"] = "AAAAAAAAB9E=", ["R2"] = "AAAAAAAAB9I=" },
            });

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [InstanceId] =
                [
                    Input(1001, InputColumnId, new CellValueData { ValueNumeric = 8m }),
                    Input(1002, InputColumnId, new CellValueData { ValueNumeric = 5m }),

                    // Місячна String-колонка (власник — місяць 09) і формула-міст над нею: текстовий результат «720».
                    Input(1001, MonthInputColumnId, new CellValueData { ValueString = "720" }),
                    Input(1002, MonthInputColumnId, new CellValueData { ValueString = "12.5" }),
                    Calculated(1001, MonthTextFormulaColumnId, new CellValueData { ValueString = "720" }),
                    Calculated(1002, MonthTextFormulaColumnId, new CellValueData { ValueString = "12.5" }),

                    // Місячна формула з числовим результатом.
                    Calculated(1001, MonthNumberFormulaColumnId, new CellValueData { ValueNumeric = 744m }),
                    Calculated(1002, MonthNumberFormulaColumnId, new CellValueData { ValueNumeric = 0.1234567890123456m }),

                    // Похідна з довідника: REGFIELD(…, 'CATEGORY1') — текст «6975».
                    Calculated(1001, DerivedColumnId, new CellValueData { ValueString = "6975" }),
                    Calculated(1002, DerivedColumnId, new CellValueData { ValueString = "Group A" }),
                ],
            });

        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(_snapshot);

        _methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([new ColumnResultBinding(TableId, EmissionColumnId, 2, "EMISSION", "{}", [MethodologyVersionId])]);
        _results.ReadCurrentAsync(DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>())
            .Returns(
            [
                new CalculationResultRow(MethodologyVersionId, "R1", "EMISSION", 20.123456789m, 8, null),
                new CalculationResultRow(MethodologyVersionId, "R2", "EMISSION", 12.5m, 8, null),
            ]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("kz")]
    public async Task Незмінений_експорт_усіх_видів_обчислюваних_колонок_не_дає_ні_змін_ні_відмов(string language)
    {
        using var workbook = await ExportAsync();

        var preview = await ImportAsync(workbook, language);

        Assert.Empty(preview.Changes);
        Assert.Empty(preview.Rejected);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("OPTIME")] // місячна формула, текстовий результат «720» (Land_Measure_OperatingTime, T9_04/T9_07)
    [InlineData("OPTIME_N")] // місячна формула, числовий результат
    [InlineData("CAT1")] // похідна з довідника, текст «6975» (Category1, T9a_19)
    [InlineData("EMISSION")] // колонка методології (оверлей результатів)
    public async Task Незмінена_колонка_кожного_виду_не_дає_відмови(string columnCode)
    {
        using var workbook = await ExportAsync();

        var preview = await ImportAsync(workbook, "en");

        Assert.DoesNotContain(preview.Rejected, r => r.ColumnCode == columnCode);
        Assert.DoesNotContain(preview.Changes, c => c.ColumnCode == columnCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Число_з_тим_самим_значенням_замість_тексту_не_зміна()
    {
        // Людина перенабрала «6975» числом (Excel тримає Number): значення те саме — не правка.
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, Column("CAT1")).Value = 6975;

        var preview = await ImportAsync(workbook, "en");

        Assert.Empty(preview.Rejected);
        Assert.Empty(preview.Changes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Змінений_текстовий_результат_формули_лишається_ECR_CELL_4221_і_не_застосовується()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, Column("CAT1")).Value = "6976";

        var preview = await ImportAsync(workbook, "en");

        Assert.Empty(preview.Changes);
        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal("CAT1", rejection.ColumnCode);
        Assert.Equal("ECR-CELL-4221", rejection.ReasonCode);
        Assert.Equal(ImportMessageKeys.Calculated, rejection.MessageKey);
    }

    private static int Column(string code) => code switch
    {
        "A" => 1,
        "EMISSION" => 2,
        "TXT_M09" => 3,
        "OPTIME" => 4,
        "OPTIME_N" => 5,
        "CAT1" => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    private static CellRecord Input(long rowId, int columnId, CellValueData value)
        => new(new CellAddress(Period, rowId, columnId), TableId, value);

    private static CellRecord Calculated(long rowId, int columnId, CellValueData value)
        => new(new CellAddress(Period, rowId, columnId), TableId, value with { IsCalculated = true });

    private async Task<XLWorkbook> ExportAsync()
    {
        var exporter = new ExcelExporter(
            _cells, _rows, _metadata, Substitute.For<IStyleCatalog>(), Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator(), _methodologies, _results);

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: true, IncludeStyles: true, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        var copy = new MemoryStream();
        await output.CopyToAsync(copy);
        copy.Position = 0;
        return new XLWorkbook(copy);
    }

    private async Task<ImportPreview> ImportAsync(XLWorkbook workbook, string language)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.Language.Returns(language);
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
            _methodologies, _results);

        return await importer.PreviewAsync(DocumentId, stream, CancellationToken.None);
    }

    private static TemplateVersionSnapshot Snapshot()
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);

        var columns = new Dictionary<int, ColumnDef>
        {
            [InputColumnId] = new ColumnDef(TableId, EcrCode.Create("A"), Text("A"), 1, CellDataType.Decimal),
            [EmissionColumnId] = new ColumnDef(TableId, EcrCode.Create("EMISSION"), Text("Emission"), 2, CellDataType.Calculated),
            [MonthInputColumnId] = new ColumnDef(TableId, EcrCode.Create("TXT_M09"), Text("Txt M09"), 3, CellDataType.String),
            [MonthTextFormulaColumnId] = new ColumnDef(TableId, EcrCode.Create("OPTIME"), Text("OpTime"), 4, CellDataType.Formula),
            [MonthNumberFormulaColumnId] = new ColumnDef(TableId, EcrCode.Create("OPTIME_N"), Text("OpTime N"), 5, CellDataType.Formula),
            [DerivedColumnId] = new ColumnDef(TableId, EcrCode.Create("CAT1"), Text("Category1"), 6, CellDataType.Formula),
        };

        foreach (var (id, column) in columns)
        {
            SetId(column, id);
            table.AddColumn(column);
        }

        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet], columns, new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
