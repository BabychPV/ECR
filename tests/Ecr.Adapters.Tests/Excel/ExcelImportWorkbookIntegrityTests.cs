// tests/Ecr.Adapters.Tests/Excel/ExcelImportWorkbookIntegrityTests.cs
using System.Globalization;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
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
/// Аудит 7, ділянка Y5: «експорт → правка в Excel → імпорт» справжніми
/// <see cref="ExcelExporter"/> і <see cref="ExcelImporter"/> — що робить з книгою
/// звичайна людина в Excel і що з цього доходить до перегляду імпорту.
/// </summary>
/// <remarks>
/// Книга: аркуш «Sheet», таблиця T1 (назва — рядок 1, заголовок — рядок 2, рядки
/// R1..R3 — рядки 3..5), колонки N (Decimal, A), S (String, B), P (Lookup, C),
/// стовпець підписів рядків — D.
/// </remarks>
public sealed class ExcelImportWorkbookIntegrityTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int NumberColumnId = 11;
    private const int TextColumnId = 12;
    private const int PermitColumnId = 13;
    private const int PermitRegistryId = 1;
    private const string SheetName = "Sheet";

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly TemplateVersionSnapshot _snapshot = Snapshot();
    private readonly List<CellRecord> _slice = [];
    private readonly List<Ecr.Domain.Entities.Dictionaries.RegistryEntry> _entries = [];

    public ExcelImportWorkbookIntegrityTests()
    {
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002, ["R3"] = 1003 };

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [InstanceId] = rowIds });
        _rows.GetRowVersionsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [InstanceId] = new Dictionary<string, string> { ["R1"] = "0xAA", ["R2"] = "0xBB", ["R3"] = "0xCC" },
            });

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(_ => new Dictionary<long, IReadOnlyList<CellRecord>> { [InstanceId] = [.. _slice] });

        _registries.ListEntriesAsync(PermitRegistryId, Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<Ecr.Domain.Entities.Dictionaries.RegistryEntry>)[.. _entries]);

        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(_snapshot);
        _periods.FindPeriodBoundsAsync(DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));

        Number(1001, 10m);
        Number(1002, 20m);
        Number(1003, 30m);
    }

    // ── Y5-04 ────────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-04")]
    public async Task Перейменований_аркуш_дає_відмову_таблиці_а_не_500()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet(SheetName).Name = "Sheet 2026";

        // ⛔ Мутація: прибрати перевірку `TryGetWorksheet` у `PreviewAsync` — тут
        // вилітає ArgumentException ClosedXML, тобто 500 ECR-SYS-0500 на стенді.
        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal("ECR-IMP-0422", rejection.ReasonCode);
        Assert.Equal(ImportMessageKeys.SheetMissing, rejection.MessageKey);
        Assert.Equal("T1", rejection.TableCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-04")]
    public async Task Видалений_аркуш_дає_ту_саму_відмову()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheets.Add("Other");
        workbook.Worksheet(SheetName).Delete();

        var preview = await ImportAsync(workbook);

        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal(ImportMessageKeys.SheetMissing, rejection.MessageKey);
    }

    // ── Y5-05 ────────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-05")]
    public async Task Текст_із_пробілами_на_краях_у_незміненій_книзі_не_дає_зміни()
    {
        Text(1001, "ТОО Альфа ");
        Text(1002, "  відступ");
        Text(1003, "   ");
        using var workbook = await ExportAsync();

        // ⛔ Мутація: повернути `Trim()` у гілку тексту `Read` — три фантомні зміни
        // («ТОО Альфа » → «ТОО Альфа», «  відступ» → «відступ», «   » → стерто).
        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        Assert.Empty(preview.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-05")]
    public async Task Текст_набраний_у_книзі_лягає_як_є_з_пробілами()
    {
        Text(1001, "old");
        using var workbook = await ExportAsync();
        workbook.Worksheet(SheetName).Cell(3, 2).Value = "new  value ";

        var preview = await ImportAsync(workbook);

        var change = Assert.Single(preview.Changes);
        Assert.Equal("S", change.ColumnCode);
        Assert.Equal("new  value ", change.NewValue);
    }

    // ── Y5-06 ────────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-06")]
    public async Task Число_в_текстовій_колонці_не_залежить_від_культури_сервера()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet(SheetName).Cell(3, 2).Value = 12.5;

        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        ImportPreview preview;
        try
        {
            // ⛔ Мутація: повернути `GetString()` для числової комірки в гілці тексту —
            // на ru-RU тут «12,5».
            preview = await ImportAsync(workbook);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }

        var change = Assert.Single(preview.Changes);
        Assert.Equal("S", change.ColumnCode);
        Assert.Equal("12.5", change.NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-06")]
    public async Task Текстова_колонка_експортується_текстовим_форматом()
    {
        // ⚠ Без `@` Excel перетворює набране «12.50» на число ще до імпорту.
        using var workbook = await ExportAsync();
        var sheet = workbook.Worksheet(SheetName);

        Assert.Equal("@", sheet.Cell(3, 2).Style.NumberFormat.Format);
        Assert.NotEqual("@", sheet.Cell(3, 1).Style.NumberFormat.Format);
    }

    // ── Y5-01 ────────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-01")]
    public async Task Незмінена_книга_не_дає_відмови_розкладки()
    {
        // Контроль: відбитки підписів і заголовків збігаються самі з собою.
        using var workbook = await ExportAsync();

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        Assert.Empty(preview.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-01")]
    public async Task Відсортовані_рядки_дають_відмову_таблиці_а_не_переставлені_значення()
    {
        using var workbook = await ExportAsync();
        SwapRows(workbook.Worksheet(SheetName), 3, 5);

        // ⛔ Мутація: прибрати звірку `LayoutFingerprint.FirstMismatch` у `Build` —
        // перегляд дає дві правдоподібні зміни (R1 · N: 10 → 30, R3 · N: 30 → 10).
        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal("ECR-IMP-0422", rejection.ReasonCode);
        Assert.Equal(ImportMessageKeys.LayoutChanged, rejection.MessageKey);
        Assert.Equal("R1", rejection.RowKey);
        Assert.Equal("D3", rejection.ExcelCell);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-01")]
    public async Task Видалений_рядок_дає_відмову_таблиці_а_не_стерте_значення()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet(SheetName).Row(3).Delete();

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        Assert.Contains(preview.Rejected, r => r.MessageKey == ImportMessageKeys.LayoutChanged);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-01")]
    public async Task Вставлена_колонка_дає_відмову_за_заголовком()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet(SheetName).Column(1).InsertColumnsBefore(1);

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        var layout = Assert.Single(preview.Rejected, r => r.MessageKey == ImportMessageKeys.LayoutChanged);
        Assert.Equal("N", layout.ColumnCode);
        Assert.Equal("A2", layout.ExcelCell);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-01")]
    public async Task Книга_без_відбитків_розкладки_читається_як_раніше()
    {
        // ⚠ Сумісність: книги, вивантажені до Y5-01 і AN-118, не мають `label`/`header`/`cells`
        // у карті. Їх не відмовляють — звірки просто немає (тож перестановка проходить, як і доти).
        using var workbook = await ExportAsync();
        StripLayoutFingerprints(workbook);
        SwapRows(workbook.Worksheet(SheetName), 3, 5);

        var preview = await ImportAsync(workbook);

        Assert.DoesNotContain(preview.Rejected, r => r.MessageKey == ImportMessageKeys.LayoutChanged);
        Assert.Equal(2, preview.Changes.Count);
    }

    // ── V7-01 ────────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "V7-01")]
    public async Task Переставлені_лише_дані_без_стовпця_підписів_дають_відмову_таблиці()
    {
        using var workbook = await ExportAsync();
        SwapData(workbook.Worksheet(SheetName), 3, 5);

        // ⛔ Мутація: `FirstMismatch` повертає `null` замість `PermutedRow(...)` — підписи
        // в D на місці, і перегляд дає дві правдоподібні зміни (R1 · N: 10 → 30, R3 · N: 30 → 10).
        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal("ECR-IMP-0422", rejection.ReasonCode);
        Assert.Equal(ImportMessageKeys.LayoutChanged, rejection.MessageKey);
        Assert.Equal("R1", rejection.RowKey);
        Assert.Equal("A3", rejection.ExcelCell);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "V7-01")]
    public async Task Перестановка_даних_разом_із_правкою_іншого_рядка_все_одно_дає_відмову()
    {
        // Цикл, а не рівність усього набору змінених рядків: правка R2 не ховає обмін R1 ↔ R3.
        using var workbook = await ExportAsync();
        var sheet = workbook.Worksheet(SheetName);
        SwapData(sheet, 3, 5);
        sheet.Cell(4, 1).Value = 99;

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        Assert.Single(preview.Rejected, r => r.MessageKey == ImportMessageKeys.LayoutChanged);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "V7-01")]
    public async Task Скопійований_рядок_не_дає_відмови_розкладки()
    {
        // Контроль хибної відмови: копія R1 у R2 (джерело не змінилося) — звичайна правка.
        using var workbook = await ExportAsync();
        var sheet = workbook.Worksheet(SheetName);
        for (var column = 1; column <= 3; column++)
        {
            sheet.Cell(4, column).Value = sheet.Cell(3, column).Value;
        }

        var preview = await ImportAsync(workbook);

        Assert.DoesNotContain(preview.Rejected, r => r.MessageKey == ImportMessageKeys.LayoutChanged);
        var change = Assert.Single(preview.Changes);
        Assert.Equal("R2", change.RowKey);
    }

    // ── Y5-03 ────────────────────────────────────────────────────────────────

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-03")]
    [InlineData("inactive", "err.ECR-CELL-4223.importInactiveEntry")]
    [InlineData("deleted", "err.ECR-CELL-4223.importDeletedEntry")]
    [InlineData("expired", "err.ECR-CELL-4223.importEntryNotValidOnDate")]
    public async Task Непридатний_запис_довідника_відхиляється_в_перегляді_а_не_на_застосуванні(
        string state, string expectedKey)
    {
        var entry = Entry(5, "PERMIT_OLD");
        switch (state)
        {
            case "inactive":
                entry.Deactivate();
                break;
            case "deleted":
                entry.SoftDelete();
                break;
            default:
                // Строк сплив 1 вересня — на останній день періоду (30.09) запис нечинний.
                entry.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 1));
                break;
        }

        using var workbook = await ExportAsync();
        workbook.Worksheet(SheetName).Cell(3, 3).Value = "PERMIT_OLD";

        // ⛔ Мутація: прибрати відмову `unusableLookups` у `Build` — тут звичайна зміна,
        // а застосування потім дає 4223 на всю книгу (C7, `CheckLookupStandings`).
        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal("ECR-CELL-4223", rejection.ReasonCode);
        Assert.Equal(expectedKey, rejection.MessageKey);
        Assert.Equal("C3", rejection.ExcelCell);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-03")]
    public async Task Придатний_запис_довідника_дає_зміну()
    {
        Entry(5, "PERMIT_A");

        using var workbook = await ExportAsync();
        workbook.Worksheet(SheetName).Cell(3, 3).Value = "permit_a";

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Rejected);
        Assert.Equal(5L, Assert.Single(preview.Changes).NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-03")]
    public async Task Цифри_замість_коду_не_стають_ідентифікатором_запису()
    {
        // Запис 11 є в тому самому довіднику — «11» у книзі колись тихо ставав посиланням на нього.
        Entry(5, "PERMIT_A");
        Entry(11, "PERMIT_B");

        using var workbook = await ExportAsync();
        workbook.Worksheet(SheetName).Cell(3, 3).Value = "11";

        // ⛔ Мутація: прибрати відмову для рядка в Lookup-колонці — зміна з NewValue «11»,
        // яку `CellValueReader.Identifier` перетворює на Id 11.
        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal(ImportMessageKeys.ExpectsIdentifier, rejection.MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y5-03")]
    public async Task Незмінене_осиротіле_посилання_не_дає_ні_зміни_ні_відмови()
    {
        // Запису 99 немає серед кодів довідника: експорт пише Id числом.
        Entry(5, "PERMIT_A");
        Permit(1001, 99);

        using var workbook = await ExportAsync();
        Assert.Equal("99", workbook.Worksheet(SheetName).Cell(3, 3).GetString());

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        Assert.Empty(preview.Rejected);
    }

    // ── Стенд ────────────────────────────────────────────────────────────────

    private void Number(long rowId, decimal value)
        => _slice.Add(new CellRecord(
            new CellAddress(Period, rowId, NumberColumnId), TableId, new CellValueData { ValueNumeric = value }));

    private void Text(long rowId, string value)
        => _slice.Add(new CellRecord(
            new CellAddress(Period, rowId, TextColumnId), TableId, new CellValueData { ValueString = value }));

    private void Permit(long rowId, long entryId)
        => _slice.Add(new CellRecord(
            new CellAddress(Period, rowId, PermitColumnId), TableId, new CellValueData { ValueRegistryEntryId = entryId }));

    private Ecr.Domain.Entities.Dictionaries.RegistryEntry Entry(long id, string code)
    {
        var entry = new Ecr.Domain.Entities.Dictionaries.RegistryEntry(
            PermitRegistryId, EcrCode.Create(code), Name(code));
        typeof(Entity<long>).GetProperty("Id")!.SetValue(entry, id);
        _entries.Add(entry);
        return entry;
    }

    private static void SwapRows(IXLWorksheet sheet, int first, int second)
    {
        for (var column = 1; column <= 4; column++)
        {
            var a = sheet.Cell(first, column).Value;
            sheet.Cell(first, column).Value = sheet.Cell(second, column).Value;
            sheet.Cell(second, column).Value = a;
        }
    }

    private static void SwapData(IXLWorksheet sheet, int first, int second)
    {
        // Лише колонки даних A…C; стовпець підписів D лишається на місці (V7-01).
        for (var column = 1; column <= 3; column++)
        {
            var a = sheet.Cell(first, column).Value;
            sheet.Cell(first, column).Value = sheet.Cell(second, column).Value;
            sheet.Cell(second, column).Value = a;
        }
    }

    private static void StripLayoutFingerprints(XLWorkbook workbook)
    {
        var map = workbook.Worksheet(ExcelWorkbookMap.SheetName);
        var json = string.Concat(map.CellsUsed().OrderBy(c => c.Address.RowNumber).Select(c => c.GetString()));
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            json, ",\"(label|header|cells)\":\"[0-9a-f]*\"", string.Empty);
        Assert.NotEqual(json, stripped);
        map.Clear();
        map.Cell(1, 1).Value = stripped;
    }

    private async Task<XLWorkbook> ExportAsync()
    {
        var exporter = new ExcelExporter(
            _cells, _rows, _metadata, Substitute.For<IStyleCatalog>(), _registries,
            new StyleMapper(), new FormulaTranslator());

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: false, IncludeStyles: false, Language: "en", PeriodKey: PeriodKeyValue),
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

        var importer = new ExcelImporter(
            _metadata, _registries, access, user, Substitute.For<IImportPreviewStore>(),
            patch: null!,
            new ImportDiffBuilder(), _cells, _rows,
            Substitute.For<IUnitOfWork>(), Substitute.For<IBackgroundJobScheduler>(), Substitute.For<ISheetEditGate>(),
            periods: _periods);

        return await importer.PreviewAsync(DocumentId, stream, CancellationToken.None);
    }

    private static TemplateVersionSnapshot Snapshot()
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Name(SheetName), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Name("Table 1"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        var number = new ColumnDef(TableId, EcrCode.Create("N"), Name("Volume"), 1, CellDataType.Decimal);
        SetId(number, NumberColumnId);
        var text = new ColumnDef(TableId, EcrCode.Create("S"), Name("Note"), 2, CellDataType.String);
        SetId(text, TextColumnId);
        var permit = new ColumnDef(TableId, EcrCode.Create("P"), Name("Permit"), 3, CellDataType.Lookup);
        permit.SetLookup(registryDefId: PermitRegistryId);
        SetId(permit, PermitColumnId);
        table.AddColumn(number);
        table.AddColumn(text);
        table.AddColumn(permit);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [NumberColumnId] = number, [TextColumnId] = text, [PermitColumnId] = permit },
            new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
