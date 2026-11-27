using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
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

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Журнал_аркуша_несе_назви_значень_і_номер_рядка()
    {
        var audit = Substitute.For<IAuditReader>();
        audit.ReadCellChangesAsync(Arg.Any<CellChangeFilter>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<CellChangeView>([Change("Lookup", null, "37", ordinal: 20)], null, null));
        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetHistoryAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ApprovalEventRecord>>([]));
        var query = new SheetActivityQuery(audit, workflow, registries: Registries((37, "Diesel")));

        var page = await query.ReadAsync(
            new SheetActivityScope(1, 202606, "S1", [301], IncludeSystem: true), 50, null, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(20, item.RowOrdinal);
        Assert.Equal("Diesel", item.NewValueLabelL10n?.Get("en"));
        Assert.Null(item.OldValueLabelL10n);
    }
}
