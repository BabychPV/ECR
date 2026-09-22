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
        var zip = await Exporter().ExportAsync(DocumentId, Period, DocumentExportFormat.Csv, CancellationToken.None);

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
        var json = await Exporter().ExportAsync(DocumentId, Period, DocumentExportFormat.Json, CancellationToken.None);

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

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(DocumentId, Options(), CancellationToken.None, "csv"));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        await jobs.DidNotReceiveWithAnyArgs().EnqueueAsync<IExcelExportJob>(null, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Json_іде_тією_самою_задачею_а_ключ_несе_формат_для_завантаження()
    {
        var (handler, jobs) = Handler(allowed: true);

        await handler.HandleAsync(DocumentId, Options(), CancellationToken.None, "JSON");

        await jobs.Received(1).EnqueueAsync<IExcelExportJob>(
            Arg.Is<object?>(t => IsJsonTask(t)),
            Arg.Any<CancellationToken>(), 9);
    }

    private static bool IsJsonTask(object? payload)
        => payload is ExcelExportTask task && task.Format == "json"
           && DocumentExportFormat.OfExportId(task.ExportId).ContentType == "application/json";

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
