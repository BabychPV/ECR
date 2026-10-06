using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// A2-12: CSV (файл <c>000-header.csv</c>) і JSON (властивість <c>header</c>) містять поля шапки
/// документа з підписами мовою запиту.
/// </summary>
/// <remarks>
/// Мутації: прибрати <c>DocumentHeaderExport.ReadAsync</c> з <c>ExportAsync</c> — усі тести червоні;
/// прибрати <c>!f.IsDeleted</c> — тест видаленого поля червоний; не передавати мову — тести ru/kz червоні.
/// Доступ: окремого рівня Deny для поля шапки в моделі прав нема (<c>ResourceKind</c>: проєкт, аркуш,
/// таблиця, колонка, довідник) — експорт шапки йде за правилом документа, яке перевіряє
/// <c>ExportDocumentHandler</c> (<c>Csv_без_гранта_на_документ_відмова_як_у_xlsx</c>).
/// </remarks>
public sealed class DocumentDataExportHeaderTests
{
    private const long DocumentId = 700;
    private const long Instance = 500;
    private const int Period = 202601;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();

    public DocumentDataExportHeaderTests()
    {
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, 3);
        var note = new ColumnDef(3, EcrCode.Create("Note"), Text("Note"), 1, CellDataType.String);
        SetId(note, 12);
        table.AddColumn(note);
        sheet.AddTable(table);

        var removed = Field(25, "OLD", 6, CellDataType.String, "Old", "Старое", "Ескі");
        removed.SoftDelete(1, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        var snapshot = new TemplateVersionSnapshot(
            2, 0, [sheet], new Dictionary<int, ColumnDef> { [12] = note }, new Dictionary<(int, string), RowDef>())
        {
            HeaderFields =
            [
                Field(21, "RDATE", 1, CellDataType.Date, "Report date", "Дата отчёта", "Есеп күні"),
                Field(22, "OPERATOR", 2, CellDataType.String, "Operator", "Оператор", "Оператор"),
                Field(23, "QTY", 3, CellDataType.Decimal, "Quantity", "Количество", "Саны"),
                Field(24, "NOTE", 4, CellDataType.String, "Note, \"quoted\"", "Примечание", "Ескертпе"),
                Field(26, "FORMULA", 5, CellDataType.String, "Formula", "Формула", "Формула"),
                removed,
            ],
        };

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(Instance, DocumentId, 3, 2, Period)]);
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [Instance] = new Dictionary<string, long> { ["r1"] = 101 },
            });
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), new PeriodKey(Period), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [Instance] = [new(new CellAddress(new PeriodKey(Period), 101, 12), 3, new CellValueData { ValueString = "n" })],
            });
        _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(
            (IReadOnlyDictionary<int, DocumentHeaderValueData>)new Dictionary<int, DocumentHeaderValueData>
            {
                [21] = new() { ValueDate = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc) },
                [22] = new() { ValueString = "I. Petrenko" },
                [23] = new() { ValueNumeric = -12.5m },
                [26] = new() { ValueString = "=HYPERLINK(\"x\")" },
                [25] = new() { ValueString = "stale" },
            });
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("en", "Report date", "Operator")]
    [InlineData("ru", "Дата отчёта", "Оператор")]
    [InlineData("kz", "Есеп күні", "Оператор")]
    public async Task Json_має_властивість_header_з_кодом_підписом_мовою_запиту_і_значенням_рядком(
        string language, string dateLabel, string operatorLabel)
    {
        var json = await Exporter().ExportAsync(
            DocumentId, Period, DocumentExportFormat.Json, false, null, null, CancellationToken.None, language);

        using var doc = JsonDocument.Parse(json);
        var header = doc.RootElement.GetProperty("header").EnumerateArray().ToList();

        Assert.Equal(["RDATE", "OPERATOR", "QTY", "NOTE", "FORMULA"], header.Select(h => h.GetProperty("code").GetString()));
        Assert.Equal(dateLabel, header[0].GetProperty("label").GetString());
        Assert.Equal("2026-01-31T00:00:00", header[0].GetProperty("value").GetString());
        Assert.Equal(operatorLabel, header[1].GetProperty("label").GetString());
        Assert.Equal("I. Petrenko", header[1].GetProperty("value").GetString());
        Assert.Equal("-12.5", header[2].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, header[3].GetProperty("value").ValueKind);

        // Зворотна сумісність: решта документа — як і було.
        Assert.Equal("Main", Assert.Single(doc.RootElement.GetProperty("tables").EnumerateArray().ToList()).GetProperty("table").GetString());
        Assert.DoesNotContain("stale", Encoding.UTF8.GetString(json));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Csv_має_окремий_файл_шапки_перед_таблицями_із_захистом_текстів_і_числом_як_є()
    {
        var zip = await Exporter().ExportAsync(
            DocumentId, Period, DocumentExportFormat.Csv, false, null, null, CancellationToken.None, "ru");

        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.Equal(["000-header.csv", "001-Water-Main.csv"], archive.Entries.Select(e => e.FullName));

        using var reader = new StreamReader(archive.GetEntry("000-header.csv")!.Open(), Encoding.UTF8);
        Assert.Equal(
            "code,label,value\r\n"
            + "RDATE,Дата отчёта,2026-01-31T00:00:00\r\n"
            + "OPERATOR,Оператор,I. Petrenko\r\n"
            + "QTY,Количество,-12.5\r\n"
            + "NOTE,Примечание,\r\n"
            + "FORMULA,Формула,\"'=HYPERLINK(\"\"x\"\")\"\r\n",
            await reader.ReadToEndAsync());

        // Файл таблиці не змінився: перший рядок — коди колонок.
        using var table = new StreamReader(archive.GetEntry("001-Water-Main.csv")!.Open(), Encoding.UTF8);
        Assert.StartsWith("rowKey,Note\r\n", await table.ReadToEndAsync(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_сховища_шапки_json_має_порожній_header_а_csv_лишається_без_файлу_шапки()
    {
        var bare = new DocumentDataExporter(_cells, _rows, _metadata, _registries);

        var json = await bare.ExportAsync(DocumentId, Period, DocumentExportFormat.Json, false, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(doc.RootElement.GetProperty("header").EnumerateArray());

        var zip = await bare.ExportAsync(DocumentId, Period, DocumentExportFormat.Csv, false, CancellationToken.None);
        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.Equal("001-Water-Main.csv", Assert.Single(archive.Entries).FullName);
    }

    private DocumentDataExporter Exporter()
        => new(_cells, _rows, _metadata, _registries, headers: _headers);

    private static HeaderFieldDef Field(int id, string code, int ordinal, CellDataType type, string en, string ru, string kz)
    {
        var field = new HeaderFieldDef(
            2, EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = en, ["ru"] = ru, ["kz"] = kz }), ordinal, type);
        SetId(field, id);
        return field;
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
