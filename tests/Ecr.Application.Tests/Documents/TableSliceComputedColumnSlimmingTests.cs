// tests/Ecr.Application.Tests/Documents/TableSliceComputedColumnSlimmingTests.cs
using System.Diagnostics;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// HSE401, пункт 4: зріз великої динамічної таблиці не несе ключа `CalculatedCell` на кожну комірку колонки формули -
/// клієнт виводить це з типу колонки (<c>permissions.ts</c>). Рівень обробника (пам'ять, без БД): 6 428 рядків x 63 колонки
/// (~405 тис. комірок, 31 колонка формул) не вкладаються в ліміт тестової БД 192 МБ, тому БД тут немає взагалі.
/// </summary>
public sealed class TableSliceComputedColumnSlimmingTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int Rows = 6_428;
    private const int InputColumns = 32;
    private const int FormulaColumns = 31;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Велика_таблиця_не_несе_CalculatedCell_на_колонки_формул_і_вкладається_в_бюджет()
    {
        var f = Build(Rows, deniedFormulaReason: EditDenyReason.CalculatedCell);

        var clock = Stopwatch.StartNew();
        var slice = await f.Handler.HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);
        clock.Stop();

        // Жодного ключа CalculatedCell; решта (вхідні колонки - дозволені) теж порожня.
        Assert.DoesNotContain(slice.CellPermissions.Values, v => v == nameof(EditDenyReason.CalculatedCell));
        Assert.Empty(slice.CellPermissions);
        Assert.Equal(Rows, slice.Rows.Count);
        Assert.Equal(InputColumns + FormulaColumns, slice.Columns.Count);

        // Розмір: те, що додали б ключі «до» (rowKey:col -> "CalculatedCell"), у відсотках від повного вмісту.
        var slimBytes = JsonSerializer.SerializeToUtf8Bytes(slice, Json).Length;
        var legacyKeys = new Dictionary<string, string>();
        foreach (var row in slice.Rows)
        {
            foreach (var column in slice.Columns.Where(c => c.DataType == nameof(CellDataType.Formula)))
            {
                legacyKeys[$"{row.RowKey}:{column.Code}"] = nameof(EditDenyReason.CalculatedCell);
            }
        }

        var extraBytes = JsonSerializer.SerializeToUtf8Bytes(legacyKeys).Length;
        var legacyBytes = slimBytes + extraBytes;
        var saved = 100.0 * extraBytes / legacyBytes;
        output.WriteLine(
            $"HSE401 slice {Rows}x{InputColumns + FormulaColumns}: handler {clock.ElapsedMilliseconds} ms; "
            + $"payload {slimBytes / 1048576.0:F1} MB (було б {legacyBytes / 1048576.0:F1} MB, -{saved:F0}%), "
            + $"прибрано ключів: {legacyKeys.Count}");

        Assert.Equal(Rows * FormulaColumns, legacyKeys.Count);
        Assert.True(saved >= 25, $"економія payload лише {saved:F0}%");

        // Бюджет обробника (пам'ять, без БД): ціль - секунди, а не десятки секунд; значення виміряно й записано в коміті.
        Assert.True(clock.ElapsedMilliseconds < 8_000, $"зріз збирався {clock.ElapsedMilliseconds} мс");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Інша_причина_відмови_у_колонці_формули_ключ_зберігає()
    {
        // Поданий документ: причина DocumentSubmitted стоїть у черзі ВИЩЕ за CalculatedCell, клієнт її не виведе з типу.
        var f = Build(3, deniedFormulaReason: EditDenyReason.DocumentSubmitted);

        var slice = await f.Handler.HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Equal(3 * FormulaColumns, slice.CellPermissions.Count);
        Assert.All(slice.CellPermissions.Values, v => Assert.Equal(nameof(EditDenyReason.DocumentSubmitted), v));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task CalculatedCell_на_вхідній_колонці_ключ_зберігає()
    {
        var f = Build(2, deniedFormulaReason: EditDenyReason.CalculatedCell);
        var input = f.Columns.First(c => c.DataType == CellDataType.Decimal);
        f.Decisions[new CellAddress(new PeriodKey(Period), f.RowIds["r1"], input.Id)] =
            EditDecision.Deny(EditDenyReason.CalculatedCell);

        var slice = await f.Handler.HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Equal(nameof(EditDenyReason.CalculatedCell), Assert.Single(slice.CellPermissions).Value);
    }

    private static Fixture Build(int rowCount, EditDenyReason deniedFormulaReason)
    {
        var rows = Substitute.For<IRowStore>();
        var cells = Substitute.For<ICellStore>();
        var metadata = Substitute.For<IMetadataCache>();
        var units = Substitute.For<IUnitCatalog>();
        var access = Substitute.For<IAccessDecisionService>();
        var methodologies = Substitute.For<IMethodologyStore>();
        var periods = Substitute.For<IPeriodStore>();
        var styles = Substitute.For<IStyleCatalog>();

        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        SetId(sheet, 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        SetId(table, 3);

        var columns = new List<ColumnDef>();
        var byId = new Dictionary<int, ColumnDef>();
        for (var i = 0; i < InputColumns + FormulaColumns; i++)
        {
            var isFormula = i >= InputColumns;
            var column = new ColumnDef(
                3, EcrCode.Create(isFormula ? $"F{i}" : $"I{i}"), Text($"C{i}"), i + 1,
                isFormula ? CellDataType.Formula : CellDataType.Decimal);
            SetId(column, 100 + i);
            table.AddColumn(column);
            columns.Add(column);
            byId[column.Id] = column;
        }

        sheet.AddTable(table);
        var snapshot = new TemplateVersionSnapshot(2, 0, [sheet], byId, new Dictionary<(int, string), RowDef>());

        var rowIds = Enumerable.Range(1, rowCount).ToDictionary(i => $"r{i}", i => 10_000L + i);
        var key = new PeriodKey(Period);
        var records = new List<CellRecord>(rowCount * columns.Count);
        var decisions = new Dictionary<CellAddress, EditDecision>(rowCount * columns.Count);
        foreach (var rowId in rowIds.Values)
        {
            foreach (var column in columns)
            {
                var address = new CellAddress(key, rowId, column.Id);
                records.Add(new CellRecord(address, 3, new CellValueData { ValueNumeric = (rowId % 997) + 0.25m }));
                decisions[address] = column.DataType == CellDataType.Formula
                    ? EditDecision.Deny(deniedFormulaReason)
                    : EditDecision.Allow();
            }
        }

        rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(TableInstance, 700, 3, 2, Period));
        metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);
        rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(rowIds);
        rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, bool>());
        cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns(records);
        cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(records);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>()).Returns(decisions);
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async _ => ReadScopes.Everything(await metadata.GetAsync(2, CancellationToken.None)));
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.Ordinal)));
        methodologies.GetMethodologyIdsBoundToTableAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<int>());
        styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, StyleDef>());

        return new Fixture(
            new GetTableSliceHandler(rows, cells, metadata, units, access, methodologies, periods, styles),
            columns, decisions, rowIds);
    }

    private sealed record Fixture(
        GetTableSliceHandler Handler,
        List<ColumnDef> Columns,
        Dictionary<CellAddress, EditDecision> Decisions,
        Dictionary<string, long> RowIds);

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View", "Template.View" },
        Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
        Denies = new HashSet<string>(StringComparer.Ordinal),
        RoleIds = new HashSet<int>(),
    };
}
