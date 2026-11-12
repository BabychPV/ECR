using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Audit;

/// <summary>
/// Q-17 (LAND-YEAR G1/G2): для періоду перегляду — колонки таблиць «місяці в колонках», зміни яких журнал зберігає в
/// ПЕРІОДІ-ВЛАСНИКУ (введена не-місячна — якір року; місячна M{m} — період m), згруповані за власником.
/// </summary>
/// <remarks>
/// <para>
/// Власника дає ТОЙ САМИЙ <see cref="IMonthColumnRouter.ForeignColumnsAsync"/>, за яким PATCH пише чужу колонку
/// (<c>PatchCellsHandler.RouteForeignOwnersAsync</c>) і за яким історія однієї комірки йде до власника
/// (<c>GetCellChangesHandler.RouteToOwnerPeriodAsync</c>): другого правила власності тут немає.
/// </para>
/// <para>
/// ⛔ Лише ЗВУЖУЄ: колонка потрапляє в перелік, тільки якщо читач бачить її на ЛОГІЧНІЙ адресі
/// (<c>visibleColumnIds</c> — межі перегляду, як були) І в періоді-власнику (межа D-214,
/// <c>InPeriod(owner)</c>, як <c>OwnerScopeMask</c>). Область на період-власник будується один раз (їх ≤ 13).
/// </para>
/// <para>
/// Маршрутизація вимкнена, немає метаданих, документ не місячний чи поза маршрутизацією, таблиці не «місяці в колонках»
/// або чужих колонок немає — <c>null</c>; за вимкненого роутера — БЕЗ жодного звернення до бази.
/// </para>
/// </remarks>
internal static class OwnerPeriodColumns
{
    /// <summary>Пари «період-власник → колонки» для журналу змін.</summary>
    /// <param name="router">Роутер власності; <c>null</c> — Q-17 не діє.</param>
    /// <param name="metadata">Знімки метаданих.</param>
    /// <param name="anyPeriod">Межі читання документа без періоду (з них — <c>InPeriod(owner)</c>).</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="viewPeriod">Період перегляду.</param>
    /// <param name="visibleColumnIds">Колонки, видимі читачеві на логічній адресі (як у фільтрі журналу).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="sheetDefId">Лише таблиці цього аркуша; <c>null</c> — усі аркуші документа.</param>
    /// <param name="onlyColumnId">Лише ця колонка (фільтр журналу за колонкою); <c>null</c> — усі.</param>
    public static async Task<IReadOnlyList<CellChangeOwnerColumns>?> CollectAsync(
        IMonthColumnRouter? router,
        IMetadataCache? metadata,
        DocumentReadScope anyPeriod,
        long documentId,
        PeriodKey viewPeriod,
        IReadOnlyCollection<int> visibleColumnIds,
        CancellationToken ct,
        int? sheetDefId = null,
        int? onlyColumnId = null)
    {
        ArgumentNullException.ThrowIfNull(anyPeriod);
        ArgumentNullException.ThrowIfNull(visibleColumnIds);

        if (router is not { Enabled: true } || metadata is null || !viewPeriod.IsValid || visibleColumnIds.Count == 0)
        {
            return null;
        }

        var document = await router.GetDocumentAsync(documentId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var snapshot = await metadata.GetAsync(document.TemplateVersionId, ct).ConfigureAwait(false);
        var visible = visibleColumnIds as IReadOnlySet<int> ?? visibleColumnIds.ToHashSet();
        var onlyTable = onlyColumnId is { } only && snapshot.ColumnsById.TryGetValue(only, out var onlyColumn)
            ? onlyColumn.TableDefId
            : (int?)null;
        if (onlyColumnId is not null && onlyTable is null)
        {
            return null;
        }

        var scopeByOwner = new Dictionary<PeriodKey, DocumentReadScope>();
        var byOwner = new SortedDictionary<int, List<int>>();
        foreach (var table in snapshot.Sheets
                     .Where(s => !s.IsDeleted && (sheetDefId is null || s.Id == sheetDefId))
                     .SelectMany(s => s.Tables))
        {
            if ((onlyTable is { } t && table.Id != t) || !router.AppliesToTable(table))
            {
                continue;
            }

            foreach (var foreign in await router.ForeignColumnsAsync(document, table, viewPeriod, ct).ConfigureAwait(false))
            {
                var columnId = foreign.Column.Id;
                if (foreign.OwnerPeriod == viewPeriod || !visible.Contains(columnId)
                    || (onlyColumnId is { } o && o != columnId))
                {
                    continue;
                }

                if (!scopeByOwner.TryGetValue(foreign.OwnerPeriod, out var ownerScope))
                {
                    scopeByOwner[foreign.OwnerPeriod] = ownerScope = anyPeriod.InPeriod(foreign.OwnerPeriod);
                }

                if (!ownerScope.CanReadColumn(columnId))
                {
                    continue;
                }

                if (!byOwner.TryGetValue(foreign.OwnerPeriod.Value, out var columns))
                {
                    byOwner[foreign.OwnerPeriod.Value] = columns = [];
                }

                columns.Add(columnId);
            }
        }

        return byOwner.Count == 0
            ? null
            : [.. byOwner.Select(p => new CellChangeOwnerColumns(p.Key, [.. p.Value.Distinct().Order()]))];
    }
}
