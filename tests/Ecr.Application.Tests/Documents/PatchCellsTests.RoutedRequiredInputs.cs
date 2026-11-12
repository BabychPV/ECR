using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Q-17 × gate обов'язкових входів: таблиця «місяці в колонках» у НЕ-власному (місячному) періоді. Статус і ключі
/// правил живуть лише в екземплярі-власнику (якір року), сітка показує їх у кожному місяці, а фізичне
/// <c>ReadCellsAsync</c> у місяці їх не бачить — до фіксу gate мовчав (doc 96, T3_00, рядок LTD-A01, 202609).
/// </summary>
/// <remarks>
/// Розкладка: екземпляр перегляду 500 @ 202609 (рядок 1001), власник — екземпляр 600 @ 202601 (той самий RowKey
/// <c>7001001</c>, рядок 2001). Колонки 12 (<c>Category</c>), 14 (статус, Running = 77) і 15 (<c>Fuel</c>) мають власника
/// 202601; <c>Volume</c> — місячна колонка вересня. Правило — <c>{"14":"77","12":"Engine"}</c>, фільтр активності D-425
/// — колонка 14. Сховище комірок імітує фізичне: віддає лише запитані адреси.
/// <para>
/// Мутація: у <c>PlanOwnerBaselineAsync</c> повернути <c>null</c> одразу — обидва тести «маршрутизація ввімкнена»
/// червоніють (попередження немає), контрольний лишається зеленим.
/// </para>
/// </remarks>
public sealed partial class PatchCellsTests
{
    private const int ViewPeriod = 202609;
    private const int AnchorPeriod = 202601;
    private const long OwnerInstance = 600;
    private const long OwnerRow = 2001;
    private const int StatusColumnId = 14;
    private const int FuelColumnId = 15;

    private IMonthColumnRouter? _monthRouter;

    /// <summary>Збережене у власнику (якір року): статус Running, <c>Category = Engine</c>, <c>Fuel = Diesel</c>.</summary>
    private static readonly Dictionary<CellAddress, CellValueData> OwnerCells = new()
    {
        [new CellAddress(PeriodKey.Parse(AnchorPeriod), OwnerRow, StatusColumnId)] = new() { ValueRegistryEntryId = 77 },
        [new CellAddress(PeriodKey.Parse(AnchorPeriod), OwnerRow, 12)] = new() { ValueString = "Engine" },
        [new CellAddress(PeriodKey.Parse(AnchorPeriod), OwnerRow, FuelColumnId)] = new() { ValueString = "Diesel" },
    };

    /// <summary>Таблиця 3 «місяці в колонках» з методологією й фільтром D-425, запит — з місяця 202609.</summary>
    private void RoutedTable(int requiredColumnId, bool routing)
    {
        WithMethodology(RequiredInputSeverity.Warn, matchJson: StatusAndCategoryRule, requiredColumnId: requiredColumnId);

        ColumnDef Column(int id, string code, int ordinal, CellDataType type)
        {
            var column = new ColumnDef(
                tableDefId: 3, EcrCode.Create(code),
                new LocalizedText(new Dictionary<string, string> { ["en"] = code }), ordinal, type);
            SetId(column, id);
            return column;
        }

        var volume = Column(VolumeColumnId, "Volume", 1, CellDataType.Decimal);
        var category = Column(12, "Category", 2, CellDataType.String);
        var fuel = Column(FuelColumnId, "Fuel", 3, CellDataType.String);
        var status = Column(StatusColumnId, "Status", 4, CellDataType.Lookup);

        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("Water"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(table, 3);
        table.AddColumn(volume);
        table.AddColumn(category);
        table.AddColumn(fuel);
        sheet.AddTable(table);

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
                ColumnsById: new Dictionary<int, ColumnDef>
                {
                    [VolumeColumnId] = volume, [12] = category, [FuelColumnId] = fuel,
                },
                RowsByKey: new Dictionary<(int, string), RowDef>()));

        // Запит — з місяця вересня.
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, DocumentId: 700, TableDefId: 3, TemplateVersionId: 2, PeriodKey: ViewPeriod));
        _periods.FindPeriodBoundsAsync(700, ViewPeriod, Arg.Any<CancellationToken>())
                .Returns(new PeriodBounds(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));
        _access.CanEditCellsAsync(
                   Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                   Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
               .Returns(call => call.ArgAt<IReadOnlyCollection<CellAddress>>(3)
                   .ToDictionary(a => a, _ => EditDecision.Allow()));

        // D-425: статус — колонка 14, Running = 77.
        var source = Substitute.For<IRowActivityFilterSource>();
        var resolution = new RowActivityFilterResolution(
            "77", new Dictionary<int, IReadOnlyList<int>> { [3] = [StatusColumnId] });
        source.ResolveForMethodologiesAsync(
                  Arg.Is<IReadOnlyCollection<int>>(ids => ids.Contains(100)), Arg.Any<IReadOnlyCollection<int>>(),
                  Arg.Any<CancellationToken>())
              .Returns(new Dictionary<int, RowActivityFilterResolution?> { [100] = resolution });
        _rowActivity = source;

        // Фізичне сховище: віддає лише запитані адреси. У вересні рядок 1001 несе тільки місячну комірку.
        var stored = new Dictionary<CellAddress, CellValueData>(OwnerCells)
        {
            [new CellAddress(PeriodKey.Parse(ViewPeriod), 1001L, VolumeColumnId)] = new() { ValueNumeric = 10m },
        };
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(call => (IReadOnlyDictionary<CellAddress, CellValueData>)call.ArgAt<IReadOnlyCollection<CellAddress>>(0)
                  .Where(stored.ContainsKey)
                  .Distinct()
                  .ToDictionary(a => a, a => stored[a]));

        // Екземпляри року й рядки власника — той самий RowKey.
        _rows.GetTableInstancesOfTableInYearAsync(700, 3, 2026, Arg.Any<CancellationToken>())
             .Returns(new List<TableInstanceRef>
             {
                 new(OwnerInstance, 700, 3, 2, AnchorPeriod),
                 new(TableInstance, 700, 3, 2, ViewPeriod),
             });
        _rows.GetRowIdsAsync(OwnerInstance, PeriodKey.Parse(AnchorPeriod), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["7001001"] = OwnerRow });
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["7001001"] = 1001L });

        var router = Substitute.For<IMonthColumnRouter>();
        router.Enabled.Returns(routing);
        if (routing)
        {
            var document = new MonthRoutingDocument(700, 1, 2);
            router.GetDocumentAsync(700, Arg.Any<CancellationToken>()).Returns(document);
            router.AppliesToTable(Arg.Any<TableDef>()).Returns(true);
            router.ForeignColumnsAsync(
                      Arg.Any<MonthRoutingDocument>(), Arg.Any<TableDef>(), PeriodKey.Parse(ViewPeriod), Arg.Any<CancellationToken>())
                  .Returns((IReadOnlyList<ForeignColumn>)
                  [
                      new ForeignColumn(category, PeriodKey.Parse(AnchorPeriod)),
                      new ForeignColumn(status, PeriodKey.Parse(AnchorPeriod)),
                      new ForeignColumn(fuel, PeriodKey.Parse(AnchorPeriod)),
                  ]);
            router.OwnerPeriodAsync(
                      Arg.Any<MonthRoutingDocument>(), Arg.Any<TableDef>(), Arg.Any<ColumnDef>(), Arg.Any<PeriodKey>(),
                      Arg.Any<CancellationToken>())
                  .Returns(call => call.ArgAt<ColumnDef>(2).Id is 12 or StatusColumnId or FuelColumnId
                      ? PeriodKey.Parse(AnchorPeriod)
                      : call.ArgAt<PeriodKey>(3));
        }

        _monthRouter = router;
    }

    private static PatchCellsRequest MonthRequest(params PatchCell[] cells)
        => new(TableInstance, ViewPeriod, "UserEdit", [new PatchRow("7001001", "0x0A", cells)]);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    [Trait("Requirement", "Q-17")]
    public async Task Місяць_не_власник_стерти_ключ_правила_попереджає_ECR_CALC_0437()
    {
        RoutedTable(requiredColumnId: 12, routing: true);

        var response = await Handler().HandleAsync(MonthRequest(new PatchCell("Category", null)), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        var warning = Assert.Single(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        Assert.Equal("Warning", warning.Severity);
        Assert.Equal("7001001", warning.RowKey);
        Assert.Equal("Category", warning.ColumnCode);

        // Запис стирання пішов у власника — туди ж, звідки gate узяв «збережене».
        await _cells.Received(1).ApplyBatchAsync(
            Arg.Is<IReadOnlyCollection<CellChangeSet>>(sets => sets.Any(
                s => s.TableInstanceId == OwnerInstance
                     && s.Deletes.Contains(new CellAddress(PeriodKey.Parse(AnchorPeriod), OwnerRow, 12)))),
            Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    [Trait("Requirement", "Q-17")]
    public async Task Місяць_не_власник_стерти_звичайний_вхід_правила_що_збіглося_попереджає()
    {
        // Правило збігається лише на значеннях власника (статус 77, Category Engine); вимога — Fuel, не ключ.
        RoutedTable(requiredColumnId: FuelColumnId, routing: true);

        var response = await Handler().HandleAsync(MonthRequest(new PatchCell("Fuel", null)), CancellationToken.None);

        var warning = Assert.Single(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        Assert.Equal("Fuel", warning.ColumnCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    [Trait("Requirement", "Q-17")]
    public async Task Місяць_не_власник_заповнені_у_власника_входи_не_дають_попередження()
    {
        // Правка лише місячної колонки: усі входи заповнені у власника — gate мовчить (не хибна тривога).
        RoutedTable(requiredColumnId: FuelColumnId, routing: true);

        var response = await Handler().HandleAsync(MonthRequest(new PatchCell("Volume", 12500m)), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    [Trait("Requirement", "Q-17")]
    public async Task Маршрутизація_вимкнена_gate_читає_фізично_як_до_фіксу()
    {
        // Контроль: та сама таблиця без Q-17 — значення якоря в місяці не видно (фізичне зберігання), рядок без
        // статусу відсіює D-425, і жодного звернення до власників.
        RoutedTable(requiredColumnId: FuelColumnId, routing: false);

        var response = await Handler().HandleAsync(MonthRequest(new PatchCell("Fuel", null)), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        await _rows.DidNotReceive().GetTableInstancesOfTableInYearAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _cells.DidNotReceive().ReadCellsAsync(
            Arg.Is<IReadOnlyCollection<CellAddress>>(a => a.Any(x => x.PeriodKey == PeriodKey.Parse(AnchorPeriod))),
            Arg.Any<CancellationToken>());
    }
}
