using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
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

/// <summary>Експорт документа в CSV і JSON (ФВ-4.2).</summary>
public sealed class DocumentDataExportTests
{
    private const long DocumentId = 700;
    private const long Instance = 500;
    private const int Period = 202601;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    public DocumentDataExportTests()
    {
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, 3);

        var volume = Column(11, "Volume", 1, CellDataType.Decimal);
        volume.SetNumericFormat(28, 16);
        var note = Column(12, "Note", 2, CellDataType.String);
        var delta = Column(13, "Delta", 3, CellDataType.Decimal);
        delta.SetNumericFormat(18, 2);
        table.AddColumn(volume);
        table.AddColumn(note);
        table.AddColumn(delta);

        // ⚠ Формула прив'язана до Delta НАВІТЬ у тестах на includeFormulas=false:
        // саме так тест нижче (`Csv_і_Json_includeFormulas_false_...`) доводить,
        // що вимкнений прапорець ігнорує формулу, а не просто «її нема в даних».
        var deltaFormula = new FormulaDef(3, FormulaScope.Column, "[Volume] - [PreviousVolume]", ExpressionDialect.Template);
        deltaFormula.AssignColumn(13);
        table.AddFormula(deltaFormula);

        sheet.AddTable(table);

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns([new TableInstanceRef(Instance, DocumentId, 3, 2, Period)]);
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef> { [11] = volume, [12] = note, [13] = delta },
                new Dictionary<(int, string), RowDef>()));
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
             {
                 [Instance] = new Dictionary<string, long> { ["r1"] = 101 },
             });

        var p = new PeriodKey(Period);
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
              {
                  [Instance] =
                  [
                      new(new CellAddress(p, 101, 11), 3, new CellValueData { ValueNumeric = 12345678.1234567890123456m }),
                      new(new CellAddress(p, 101, 12), 3, new CellValueData { ValueString = "=HYPERLINK(\"x\"),a" }),
                      new(new CellAddress(p, 101, 13), 3, new CellValueData { ValueNumeric = -5.5m }),
                  ],
              });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-4.2")]
    public async Task Csv_таблиця_з_кодами_колонок_BOM_CRLF_і_16_знаками()
    {
        // ⚠ includeFormulas: false, ХОЧА Delta має формулу (див. конструктор) —
        // числовий доказ, що вимкнений прапорець лишає вивід побайтно тим самим,
        // що й до ФВ-4.2: жодного символу секції "formulas" нижче нема.
        var zip = await Exporter().ExportAsync(
            DocumentId, Period, DocumentExportFormat.Csv, includeFormulas: false, CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(zip));
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("001-Water-Main.csv", entry.FullName);

        using var raw = new MemoryStream();
        using (var s = entry.Open())
        {
            await s.CopyToAsync(raw);
        }

        var bytes = raw.ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        // ⚠ Текст-формула знешкоджена апострофом і взята в лапки (кома),
        // а від'ємне число — НІ: інакше «-5.50» перестало б бути числом.
        Assert.Equal(
            "rowKey,Volume,Note,Delta\r\n"
            + "r1,12345678.1234567890123456,\"'=HYPERLINK(\"\"x\"\"),a\",-5.50\r\n",
            Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-4.2")]
    public async Task Json_документ_таблиці_рядки_значення_за_кодом_і_десяткові_рядком()
    {
        // ⚠ includeFormulas: false — доказ той самий, що й для CSV вище: жодного
        // поля "expression" в описі колонки Delta нижче нема, попри формулу.
        var json = await Exporter().ExportAsync(
            DocumentId, Period, DocumentExportFormat.Json, includeFormulas: false, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(DocumentId, root.GetProperty("documentId").GetInt64());
        Assert.Equal(Period, root.GetProperty("periodKey").GetInt32());

        var table = Assert.Single(root.GetProperty("tables").EnumerateArray().ToList());
        Assert.Equal("Main", table.GetProperty("table").GetString());
        Assert.Equal(16, table.GetProperty("columns")[0].GetProperty("scale").GetInt32());

        var row = Assert.Single(table.GetProperty("rows").EnumerateArray().ToList());
        Assert.Equal("r1", row.GetProperty("rowKey").GetString());

        var volume = row.GetProperty("values").GetProperty("Volume");
        Assert.Equal(JsonValueKind.String, volume.ValueKind);
        Assert.Equal("12345678.1234567890123456", volume.GetString());
        Assert.Equal("-5.50", row.GetProperty("values").GetProperty("Delta").GetString());

        // ⚠ Числовий доказ нульового diff: колонка Delta МАЄ формулу (конструктор),
        // але описана рівно 4 полями (code/dataType/scale/unitId) — так само, як
        // до ФВ-4.2, без "expression".
        var deltaColumn = table.GetProperty("columns")[2];
        Assert.Equal("Delta", deltaColumn.GetProperty("code").GetString());
        Assert.Equal(4, deltaColumn.EnumerateObject().Count());
        Assert.False(deltaColumn.TryGetProperty("expression", out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-4.2")]
    public async Task Csv_includeFormulas_true_додає_секцію_formulas_із_сирим_виразом()
    {
        var zip = await Exporter().ExportAsync(
            DocumentId, Period, DocumentExportFormat.Csv, includeFormulas: true, CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(zip));
        var entry = Assert.Single(archive.Entries);

        using var raw = new MemoryStream();
        using (var s = entry.Open())
        {
            await s.CopyToAsync(raw);
        }

        var bytes = raw.ToArray();
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // ⚠ Секція formulas — ОКРЕМО після рядків даних (порожній рядок-межа),
        // сирий вираз БЕЗ трансляції в Excel-нотацію: жодних A1-посилань.
        Assert.Equal(
            "rowKey,Volume,Note,Delta\r\n"
            + "r1,12345678.1234567890123456,\"'=HYPERLINK(\"\"x\"\"),a\",-5.50\r\n"
            + "\r\n"
            + "formulas\r\n"
            + "column,expression\r\n"
            + "Delta,[Volume] - [PreviousVolume]\r\n",
            text);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-4.2")]
    public async Task Json_includeFormulas_true_додає_поле_expression_в_опис_колонки()
    {
        var json = await Exporter().ExportAsync(
            DocumentId, Period, DocumentExportFormat.Json, includeFormulas: true, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var table = Assert.Single(doc.RootElement.GetProperty("tables").EnumerateArray().ToList());
        var columns = table.GetProperty("columns");

        var deltaColumn = columns[2];
        Assert.Equal("Delta", deltaColumn.GetProperty("code").GetString());
        Assert.Equal("[Volume] - [PreviousVolume]", deltaColumn.GetProperty("expression").GetString());

        // ⚠ Колонки без формули (Volume, Note) поля "expression" не отримують
        // навіть коли includeFormulas: true — не кожна колонка формульна.
        Assert.False(columns[0].TryGetProperty("expression", out _));
        Assert.False(columns[1].TryGetProperty("expression", out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Невідомий_формат_422_і_задача_не_ставиться()
    {
        var (handler, jobs) = Handler(allowed: true);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(DocumentId, Options(), CancellationToken.None, "xml"));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.exportFormatUnknown", error.Details!["messageKey"]);
        await jobs.DidNotReceiveWithAnyArgs().EnqueueAsync<IExcelExportJob>(null, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Csv_без_гранта_на_документ_відмова_як_у_xlsx()
    {
        var (handler, jobs) = Handler(allowed: false);

        // ⛔ B-08: невидимий документ — 404, як неіснуючий (`DocumentVisibility`), а не 403.
        var denied = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(DocumentId, Options(), CancellationToken.None, "csv"));

        Assert.Equal("ECR-DOC-0404", denied.ErrorCode);
        await jobs.DidNotReceiveWithAnyArgs().EnqueueAsync<IExcelExportJob>(null, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Json_іде_тією_самою_задачею_з_тим_самим_ключем_на_32_hex()
    {
        var (handler, jobs) = Handler(allowed: true);

        await handler.HandleAsync(DocumentId, Options(), CancellationToken.None, "JSON");

        await jobs.Received(1).EnqueueAsync<IExcelExportJob>(
            Arg.Is<object?>(t => IsJsonTask(t)),
            Arg.Any<CancellationToken>(), 9);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Тип_вмісту_завантаження_визначається_за_вмістом()
    {
        var csv = await Exporter().ExportAsync(
            DocumentId, Period, DocumentExportFormat.Csv, includeFormulas: false, CancellationToken.None);
        var json = await Exporter().ExportAsync(
            DocumentId, Period, DocumentExportFormat.Json, includeFormulas: false, CancellationToken.None);

        using var book = new MemoryStream();
        using (var zip = new ZipArchive(book, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("[Content_Types].xml");
        }

        Assert.Equal("application/zip", DocumentExportFormat.OfContent(csv).ContentType);
        Assert.Equal("application/json", DocumentExportFormat.OfContent(json).ContentType);
        Assert.Equal("xlsx", DocumentExportFormat.OfContent(book.ToArray()).Extension);
    }

    private static bool IsJsonTask(object? payload)
        => payload is ExcelExportTask task && task.Format == "json"
           && task.ExportId.Length == 32 && task.ExportId.All(Uri.IsHexDigit);

    private DocumentDataExporter Exporter() => new(_cells, _rows, _metadata, _registries);

    private static (ExportDocumentHandler Handler, IBackgroundJobScheduler Jobs) Handler(bool allowed)
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<Common.ICurrentUser>();
        user.UserId.Returns(9);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 9 }.Permission("Document.Export").Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
              .Returns(allowed ? EditDecision.Allow() : EditDecision.Deny(EditDenyReason.NoGrant));
        return (new ExportDocumentHandler(jobs, access, user), jobs);
    }

    private static ExcelExportOptions Options() => new(false, false, "en", Period);

    private static ColumnDef Column(int id, string code, int ordinal, CellDataType type)
    {
        var column = new ColumnDef(3, EcrCode.Create(code), Text(code), ordinal, type);
        SetId(column, id);
        return column;
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
