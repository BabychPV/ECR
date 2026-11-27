using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Audit;

/// <summary>
/// ✎ DOCUX (огляд UI 27.11): журнал змін комірок (Audit trail → Cell changes, History інспектора) показував довідникову
/// комірку сирим id («— → 37» для Type of fuel = Diesel). Тепер рядок журналу несе назву запису поруч із id; рядок журналу
/// аркуша — ще й номер рядка на екрані (<c>RowOrdinal</c>).
/// </summary>
/// <remarks>
/// Мутації (локально): (1) у <c>LookupValueLabels.EntryId</c> не перевіряти тип <c>Lookup</c> — червоний «Decimal»;
/// (2) у <c>SheetActivityQuery.ReadCellsAsync</c> не передати <c>RowOrdinal</c> — червоний другий тест.
/// </remarks>
public sealed class LookupValueLabelsTests
{
    private static readonly DateTime At = new(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc);

    private static CellChangeView Change(string dataType, string? oldValue, string? newValue, int? ordinal = null)
        => new(At, 202606, 1, "p1-T3_00-0020", 301, oldValue, newValue, 7, "UserEdit", false,
            ColumnCode: "Land_TypeFuel", ColumnDataType: dataType, Id: 11, RowOrdinal: ordinal);

    private static IRegistryStore Registries(params (long Id, string Name)[] entries)
    {
        var store = Substitute.For<IRegistryStore>();
        IReadOnlyList<RegistryEntry> list =
        [
            .. entries.Select(e =>
            {
                var entry = new RegistryEntry(5, EcrCode.Create("E" + e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = e.Name }));
                typeof(Entity<long>).GetProperty("Id")!.SetValue(entry, e.Id);
                return entry;
            }),
        ];
        store.FindEntriesByIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>()).Returns(list);
        return store;
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Довідникова_колонка_отримує_назви_записів_інші_ні_одним_зверненням()
    {
        var registries = Registries((12, "Generator"), (37, "Diesel"));
        IReadOnlyList<CellChangeView> page =
        [
            Change("Lookup", "12", "37"),
            Change("Lookup", null, "999"), // запису вже немає — без назви
            Change("Decimal", "37", "12"), // не довідник — id не тлумачиться
        ];

        var labelled = await LookupValueLabels.WithLabelsAsync(page, registries, CancellationToken.None);

        Assert.Equal("Generator", labelled[0].OldValueLabelL10n?.Get("en"));
        Assert.Equal("Diesel", labelled[0].NewValueLabelL10n?.Get("en"));
        Assert.Equal("37", labelled[0].NewValue);
        Assert.Null(labelled[1].OldValueLabelL10n);
        Assert.Null(labelled[1].NewValueLabelL10n);
        Assert.Null(labelled[2].OldValueLabelL10n);
        Assert.Null(labelled[2].NewValueLabelL10n);
        await registries.Received(1).FindEntriesByIdsAsync(
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.Order().SequenceEqual(new long[] { 12, 37, 999 })), Arg.Any<CancellationToken>());

        // Без довідникових колонок — жодного звернення.
        var none = Registries();
        await LookupValueLabels.WithLabelsAsync([Change("Decimal", "1", "2")], none, CancellationToken.None);
        await none.DidNotReceive().FindEntriesByIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// ✎ DOCUX (b19, живий прохід): рядок, доданий людиною, має <c>doc.TableRow.Ordinal = 0</c> — журнал казав «Row 0». Номер
    /// тепер — позиція в порядку зрізу (рядки шаблону за <c>RowDef.Ordinal</c>, далі додані за <c>TableRow.Id</c>), як «row N»
    /// рядка формули. Мутація: повернути `tr.Ordinal` / не рахувати позицію — червоний.
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Журнал_аркуша_номер_рядка_на_екрані_для_доданих_рядків_і_назва_значення()
    {
        const long Instance = 77;
        var (rowStore, metadata) = SliceOf(Instance);
        var audit = Substitute.For<IAuditReader>();
        audit.ReadCellChangesAsync(Arg.Any<CellChangeFilter>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<CellChangeView>(
            [
                Change("Lookup", null, "37") with { RowKey = "p1-T3_00-0020", TableInstanceId = Instance },
                Change("Lookup", null, "37") with { RowKey = "T-B", TableInstanceId = Instance },
                Change("Lookup", null, "37") with { RowKey = "gone", TableInstanceId = Instance },
            ], null, null));
        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetHistoryAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ApprovalEventRecord>>([]));
        var query = new SheetActivityQuery(audit, workflow, registries: Registries((37, "Diesel")), rowStore: rowStore, metadata: metadata);

        var page = await query.ReadAsync(
            new SheetActivityScope(1, 202606, "S1", [301], IncludeSystem: true), 50, null, CancellationToken.None);

        // Порядок зрізу: T-A (шаблон, 1), T-B (шаблон, 2), далі додані за Id: …0019 (Id 40), …0020 (Id 41).
        Assert.Equal([4, 2, null], page.Items.Select(i => i.RowOrdinal));
        Assert.All(page.Items, i => Assert.Equal("Diesel", i.NewValueLabelL10n?.Get("en")));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Журнал_змін_номер_рядка_як_у_зрізі_а_без_сховищ_номера_немає_не_нуль()
    {
        const long Instance = 77;
        var (rowStore, metadata) = SliceOf(Instance);
        IReadOnlyList<CellChangeView> page = [Change("Decimal", "1", "2") with { RowKey = "p1-T3_00-0019", TableInstanceId = Instance, RowOrdinal = 0 }];

        var placed = await CellChangeRowPositions.WithScreenPositionsAsync(page, rowStore, metadata, CancellationToken.None);
        Assert.Equal(3, Assert.Single(placed).RowOrdinal);

        var unknown = await CellChangeRowPositions.WithScreenPositionsAsync(page, null, null, CancellationToken.None);
        Assert.Null(Assert.Single(unknown).RowOrdinal);
    }

    /// <summary>Екземпляр 77: шаблон T-A (Ordinal 2), T-B (Ordinal 5); додані людиною …0020 (Id 41) і …0019 (Id 40), Ordinal 0.</summary>
    private static (IRowStore Rows, IMetadataCache Metadata) SliceOf(long instance)
    {
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("T3"), new LocalizedText(new Dictionary<string, string> { ["en"] = "T3" }), 1,
            Domain.Enums.TableLayoutKind.MonthsInColumns, Domain.Enums.TableRowMode.Mixed);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, 3);
        table.AddRow(new RowDef(3, RowKey.Create("T-B"), 5, new LocalizedText(), Domain.Enums.RowKind.Item));
        table.AddRow(new RowDef(3, RowKey.Create("T-A"), 2, new LocalizedText(), Domain.Enums.RowKind.Item));
        var sheet = new SheetDef(templateVersionId: 2, EcrCode.Create("S1"), new LocalizedText(new Dictionary<string, string> { ["en"] = "S1" }), 1);
        sheet.AddTable(table);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>(), RowsByKey: new Dictionary<(int, string), RowDef>()));
        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(instance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(instance, 1, 3, 2, 202606));
        rows.GetRowIdsAsync(instance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["p1-T3_00-0020"] = 41, ["T-B"] = 9, ["p1-T3_00-0019"] = 40, ["T-A"] = 12,
            });
        return (rows, metadata);
    }
}
