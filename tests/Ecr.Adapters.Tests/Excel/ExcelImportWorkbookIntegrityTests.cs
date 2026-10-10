// tests/Ecr.Adapters.Tests/Excel/ExcelImportWorkbookIntegrityTests.cs
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

    // ── Стенд ────────────────────────────────────────────────────────────────

    private void Number(long rowId, decimal value)
        => _slice.Add(new CellRecord(
            new CellAddress(Period, rowId, NumberColumnId), TableId, new CellValueData { ValueNumeric = value }));

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
            Substitute.For<IUnitOfWork>(), Substitute.For<IBackgroundJobScheduler>(), Substitute.For<ISheetEditGate>());

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
