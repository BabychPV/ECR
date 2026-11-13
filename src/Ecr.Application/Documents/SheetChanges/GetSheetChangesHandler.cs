// src/Ecr.Application/Documents/SheetChanges/GetSheetChangesHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.SheetChanges;

/// <summary>
/// «Чи змінив хтось дані аркуша, відколи я читав» — дешеве опитування живого оновлення сітки (30 с на вкладку).
/// Право <c>Document.View</c>; доступ — той самий, що на читання аркуша.
/// </summary>
/// <remarks>
/// ⛔ Доступ: невидимий документ і схований аркуш (за періодом, D-214) — одна й та сама <c>404 ECR-DOC-0404</c>;
/// схована таблиця не потрапляє ні в пробу, ні в <c>changedTables</c>; зміни схованих колонок не рахуються
/// ні в авторстві, ні в «змінено» (рядок, змінений лише в схованій колонці, мовчить).
///
/// ⚠ Маркер — <c>rowversion</c> рядків (<see cref="ISheetChangeStore"/>). «Своє» відсіюється за журналом
/// <c>aud.CellChange</c>: рядок, ОСТАННЮ (не-перерахункову) зміну якого зробив сам викликач, не вважається чужим —
/// тому власний <c>PATCH</c> лише пересуває маркер наступного опитування, банера не дає. Чужа правка того самого
/// рядка ПІСЛЯ маркера, перекрита власною, — це конфлікт версії рядка (<c>ECR-CELL-0409</c>), а не банер.
/// Рядок, змінений без запису в журналі значень (додано порожній рядок, видалено рядок, позначка сироти), — чужий
/// з невідомим автором: правдиве «дані змінились» дешевше за пропущене.
///
/// ⚠ Межа проби: на екземпляр таблиці авторство перевіряється для <see cref="SampledRowsPerTable"/> найсвіжіших
/// змінених рядків. Масова власна правка (імпорт сотень рядків), серед якої загубилася чужа правка СТАРІШОГО рядка,
/// цього опитування банера не дасть — прийнята межа, бо інакше одне опитування після імпорту читало б журнал усіх
/// змінених рядків.
///
/// ⛔ Q-17 (LAND-YEAR G4): у таблиці «місяці в колонках» колонку з іншим власником (не-місячна — якір року, M{m} —
/// період m) PATCH пише в екземпляр ВЛАСНИКА, тож правка з іншого місяця не торкалася жодного рядка екземпляра перегляду і
/// банер мовчав. Проба тоді йде ще й по екземплярах власників тих колонок, які читач бачить на логічній адресі І в
/// періоді-власнику (D-214) — ТІЄЮ САМОЮ однією командою (<see cref="ISheetChangeStore.ProbeOwnersAsync"/>); колонки,
/// закриті в періоді-власнику, у авторстві рахуються схованими. Маршрутизація вимкнена, копія аркуша або чужих колонок
/// немає — проба й кількість звернень як були.
/// </remarks>
public sealed class GetSheetChangesHandler(
    IAccessDecisionService access,
    ICurrentUser currentUser,
    ISheetChangeStore store,
    IClock clock,
    IMonthColumnRouter? monthRouter = null,
    IMetadataCache? metadata = null)
{
    /// <summary>Право — те саме, що на читання документа (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.View";

    /// <summary>Скільки найсвіжіших змінених рядків екземпляра перевіряється на авторство.</summary>
    public const int SampledRowsPerTable = 50;

    /// <summary>
    /// Запас вікна журналу перед моментом видачі маркера: зміну, записану в журнал ДО коміту довгої транзакції,
    /// авторство все одно знаходить.
    /// </summary>
    public static readonly TimeSpan AttributionSlack = TimeSpan.FromMinutes(15);

    /// <summary>Опитування змін аркуша.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш версії шаблону.</param>
    /// <param name="periodKey">Період (рік*100+номер).</param>
    /// <param name="since">Маркер попереднього опитування; <c>null</c> — перше.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="sheetCopyId">RS: копія аркуша, яку дивиться клієнт (її дані й стан); <c>null</c> — оригінал.</param>
    public async Task<SheetChangesDto> HandleAsync(
        long documentId, int sheetDefId, int periodKey, string? since, CancellationToken ct, long? sheetCopyId = null)
    {
        var key = PeriodKey.Parse(periodKey);

        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, Permission, ct).ConfigureAwait(false);

        var anyPeriod = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        var readable = anyPeriod.InPeriod(key);
        var view = readable.VisibleTablesOfSheet(sheetDefId) ?? throw NotFound(documentId);

        var now = clock.UtcNow;
        var previous = SheetChangeMarker.Decode(since);

        // ✎ LIVE-STATUS: стан погодження — окремо від даних і на КОЖНОМУ шляху відповіді (і без змін комірок).
        var status = await store.SheetStatusAsync(documentId, sheetDefId, periodKey, ct, sheetCopyId).ConfigureAwait(false);
        var statusChanged = previous?.StatusRevision is { } seen && seen != status.Revision;

        if (previous is null || view.Tables.Count == 0)
        {
            return Unchanged(await store.CurrentMarkerAsync(ct).ConfigureAwait(false), now, status, statusChanged);
        }

        // Q-17 (G4): екземпляри власників колонок, видимих на логічній адресі й у періоді-власнику.
        var owners = sheetCopyId is null
            ? await OwnerTargetsAsync(anyPeriod, documentId, key, view.Tables.Keys, view.HiddenColumnIds, ct).ConfigureAwait(false)
            : null;

        var probe = owners is null
            ? await store
                .ProbeAsync(
                    documentId, periodKey, [.. view.Tables.Keys.Order()], previous.Value.Version, SampledRowsPerTable, ct,
                    sheetCopyId)
                .ConfigureAwait(false)
            : await store
                .ProbeOwnersAsync(documentId, periodKey, owners.Value.Targets, previous.Value.Version, SampledRowsPerTable, ct)
                .ConfigureAwait(false);

        if (probe.Rows.Count == 0)
        {
            return Unchanged(probe.Marker, now, status, statusChanged);
        }

        IReadOnlyList<int> hidden = owners is { Blocked.Count: > 0 } o
            ? [.. view.HiddenColumnIds.Concat(o.Blocked).Distinct()]
            : view.HiddenColumnIds;

        var issued = previous.Value.IssuedAtUtc < now ? previous.Value.IssuedAtUtc : now;
        var attribution = await store
            .AttributeAsync(
                documentId, [.. probe.Rows.Select(r => r.TableRowId).Distinct()], hidden,
                issued - AttributionSlack, currentUser.UserId!.Value, ct)
            .ConfigureAwait(false);

        bool Foreign(SheetChangedRow row)
            => attribution.ForeignRowIds.Contains(row.TableRowId) || !attribution.AttributedRowIds.Contains(row.TableRowId);

        if (!probe.Rows.Any(Foreign))
        {
            return Unchanged(probe.Marker, now, status, statusChanged);
        }

        // Таблиці з чужими змінами — і ті, де слідом за ними щось перерахувалося (похідні клітинки теж застаріли).
        var tables = probe.Rows
            .Where(r => Foreign(r) || attribution.RecalculatedRowIds.Contains(r.TableRowId))
            .Select(r => (r.TableDefId, r.TableInstanceId))
            .Distinct()
            .Where(t => view.Tables.ContainsKey(t.TableDefId))
            .Select(t => new SheetChangedTableDto(view.Tables[t.TableDefId], t.TableInstanceId))
            .OrderBy(t => t.TableCode, StringComparer.Ordinal)
            .ToList();

        return new SheetChangesDto(
            SheetChangeMarker.Encode(probe.Marker, now, status.Revision),
            Changed: true,
            attribution.LastForeignUserId,
            attribution.LastForeignDisplayName,
            attribution.LastForeignAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null,
            tables,
            status.Status,
            Revision(status.Revision),
            statusChanged);
    }

    /// <summary>
    /// Q-17 (G4): цілі проби — видимі таблиці аркуша в періоді перегляду плюс екземпляри періодів-власників колонок, які
    /// читач бачить на логічній адресі (не сховані) І в періоді-власнику (D-214, як <c>OwnerScopeMask</c>); закриті у
    /// власника — <c>Blocked</c> (для авторства вони сховані). <c>null</c> — власників немає: стара проба без змін.
    /// </summary>
    /// <remarks>
    /// Власник — ТОЙ САМИЙ <see cref="IMonthColumnRouter.ForeignColumnsAsync"/>, за яким пише PATCH. Маршрутизація
    /// вимкнена — <c>null</c> без звернень до бази.
    /// </remarks>
    private async Task<(IReadOnlyList<SheetProbeTarget> Targets, IReadOnlyList<int> Blocked)?> OwnerTargetsAsync(
        DocumentReadScope anyPeriod,
        long documentId,
        PeriodKey viewPeriod,
        IEnumerable<int> visibleTableIds,
        IReadOnlyList<int> hiddenColumnIds,
        CancellationToken ct)
    {
        if (monthRouter is not { Enabled: true } || metadata is null)
        {
            return null;
        }

        var document = await monthRouter.GetDocumentAsync(documentId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var snapshot = await metadata.GetAsync(document.TemplateVersionId, ct).ConfigureAwait(false);
        var tablesById = snapshot.Sheets.SelectMany(s => s.Tables).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var hidden = hiddenColumnIds.ToHashSet();
        var tableIds = visibleTableIds.Order().ToList();

        var owners = new SortedSet<(int Period, int Table)>();
        var blocked = new HashSet<int>();
        var scopeByOwner = new Dictionary<PeriodKey, DocumentReadScope>();
        foreach (var tableId in tableIds)
        {
            if (!tablesById.TryGetValue(tableId, out var table) || !monthRouter.AppliesToTable(table))
            {
                continue;
            }

            foreach (var foreign in await monthRouter.ForeignColumnsAsync(document, table, viewPeriod, ct).ConfigureAwait(false))
            {
                if (foreign.OwnerPeriod == viewPeriod || hidden.Contains(foreign.Column.Id))
                {
                    continue;
                }

                if (!scopeByOwner.TryGetValue(foreign.OwnerPeriod, out var ownerScope))
                {
                    scopeByOwner[foreign.OwnerPeriod] = ownerScope = anyPeriod.InPeriod(foreign.OwnerPeriod);
                }

                if (ownerScope.CanReadColumn(foreign.Column.Id))
                {
                    owners.Add((foreign.OwnerPeriod.Value, table.Id));
                }
                else
                {
                    blocked.Add(foreign.Column.Id);
                }
            }
        }

        if (owners.Count == 0)
        {
            return null;
        }

        return (
            [
                .. tableIds.Select(t => new SheetProbeTarget(viewPeriod.Value, t)),
                .. owners.Select(o => new SheetProbeTarget(o.Period, o.Table)),
            ],
            [.. blocked.Order()]);
    }

    private static SheetChangesDto Unchanged(ulong marker, DateTime now, SheetStatusSnapshot status, bool statusChanged)
        => new(
            SheetChangeMarker.Encode(marker, now, status.Revision), Changed: false, null, null, null, [],
            status.Status, Revision(status.Revision), statusChanged);

    private static string Revision(ulong revision) => revision.ToString("x16", CultureInfo.InvariantCulture);

    private static NotFoundException NotFound(long documentId)
        => new(
            "ECR-DOC-0404",
            $"Документ {documentId} не знайдено.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-DOC-0404.document",
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
            });
}
