using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Q-17 (запис): комірки колонок з ІНШИМ власником, ніж екземпляр запиту, пишуться в екземпляр власника.
/// </summary>
/// <remarks>
/// <para>
/// Власник (<see cref="IMonthColumnRouter.OwnerPeriodAsync"/>): місячний стовпець <c>m</c> — екземпляр періоду
/// <c>m</c>; введений не-місячний — якір року; обчислювані й таблиці поза маршрутизацією — екземпляр запиту.
/// Усе — лише за прапором <c>Collection:MonthColumnRouting</c>.
/// </para>
/// <para>
/// ⛔ Усе відбувається ВСЕРЕДИНІ транзакції запису, ПІСЛЯ замків усіх задіяних періодів
/// (<see cref="EnsureSheetStillEditableAsync"/>, за зростанням) і матеріалізації нових рядків запиту: тут за потреби
/// створюються екземпляр власника (вузько, <see cref="IRowStore.EnsureTableInstanceAsync"/>) і тіньовий рядок
/// (рядок з потрібним <c>RowKey</c> у власника ще немає). Відкат транзакції забирає їх разом із записом.
/// </para>
/// <para>
/// ⚠ Конфлікт версій рядків ВЛАСНИКІВ: очікувані версії не передаються. Клієнт знає версії лише рядків екземпляра
/// запиту — вони й захоплюються (claim) у власному наборі (навіть коли в батчі самі чужі комірки). Рядки власників
/// захищає замок аркуша × період (власні комірки того періоду пишуться під спільним замком, а створення/видалення
/// рядків — під виняткових) і «дотик» (<c>ModifiedAt</c>) у чужому наборі.
/// </para>
/// <para>
/// ⚠ Перерахунок: насіння групується за фізичним екземпляром (<see cref="RecalcGroup"/>). Читачі стовпця в інших
/// періодах (формули, що читають M{m} свого екземпляра) — окремий крок Q-17 (гачок:
/// <see cref="RecalcGroup.InputColumnDefIds"/> — змінені введені стовпці, яких не знають читачі інших періодів).
/// </para>
/// </remarks>
public sealed partial class PatchCellsHandler
{
    /// <summary>Насіння перерахунку одного фізичного екземпляра.</summary>
    /// <param name="TableInstanceId">Фізичний екземпляр (власник або екземпляр запиту).</param>
    /// <param name="PeriodKey">Його період.</param>
    /// <param name="Seeds">Адреси змінених комірок (рядок і колонка ЦЬОГО екземпляра).</param>
    /// <param name="InputColumnDefIds">Змінені введені колонки екземпляра — вхід для читачів в інших періодах.</param>
    private sealed record RecalcGroup(
        long TableInstanceId, int PeriodKey, IReadOnlyList<RecalculationSeed> Seeds, IReadOnlyList<int> InputColumnDefIds);

    /// <summary>Розкладка батчу по фізичних екземплярах.</summary>
    /// <param name="ViewSet">Набір екземпляра запиту (власні комірки + claim усіх його рядків батчу).</param>
    /// <param name="OwnerSets">Набори екземплярів власників, по одному на екземпляр, за зростанням періоду.</param>
    /// <param name="Physical">Ті самі зміни на фізичних адресах (для аудиту).</param>
    /// <param name="PreviousPhysical">Попередні значення на фізичних адресах.</param>
    /// <param name="OutOfWindowPhysical">Позначки «поза вікном» на фізичних адресах.</param>
    /// <param name="Groups">Насіння перерахунку по фізичних екземплярах.</param>
    private sealed record RoutedWrite(
        CellChangeSet ViewSet,
        IReadOnlyList<CellChangeSet> OwnerSets,
        CellChangeLists Physical,
        IReadOnlyDictionary<CellAddress, CellValueData> PreviousPhysical,
        IReadOnlyCollection<CellAddress> OutOfWindowPhysical,
        IReadOnlyList<RecalcGroup> Groups);

    private async Task<RoutedWrite?> RouteForeignOwnersAsync(
        RequestContext context,
        CellChangeLists changes,
        bool isLateEdit,
        IReadOnlyDictionary<CellAddress, CellValueData> previous,
        CancellationToken ct)
    {
        if (monthRouter is not { Enabled: true } || !monthRouter.AppliesToTable(context.Table))
        {
            return null;
        }

        var document = await monthRouter.GetDocumentAsync(context.Instance.DocumentId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        // ── 1. Власник кожної колонки батчу ──────────────────────────────
        var columns = context.Table.Columns.Where(c => !c.IsDeleted).ToDictionary(c => c.Id);
        var ownerOf = new Dictionary<int, PeriodKey>();
        foreach (var columnId in changes.Upserts.Select(u => u.Address.ColumnDefId)
                     .Concat(changes.Deletes.Select(d => d.ColumnDefId)).Distinct())
        {
            ownerOf[columnId] = columns.TryGetValue(columnId, out var column)
                ? await monthRouter.OwnerPeriodAsync(document, context.Table, column, context.PeriodKey, ct).ConfigureAwait(false)
                : context.PeriodKey;
        }

        if (ownerOf.Values.All(p => p == context.PeriodKey))
        {
            return null;
        }

        // ── 2. Екземпляри й рядки власників (створюються під замками) ────
        var instances = (await rowStore
                .GetTableInstancesOfTableInYearAsync(
                    context.Instance.DocumentId, context.Instance.TableDefId, context.PeriodKey.Year, ct)
                .ConfigureAwait(false))
            .ToDictionary(i => i.PeriodKey);

        var foreignPeriods = ownerOf.Values.Where(p => p != context.PeriodKey).Distinct().OrderBy(p => p.Value).ToList();
        var ownerInstance = new Dictionary<int, TableInstanceRef>();
        var ownerRowIds = new Dictionary<int, Dictionary<string, long>>();

        foreach (var period in foreignPeriods)
        {
            var upsertsHere = changes.Upserts.Any(u => ownerOf[u.Address.ColumnDefId] == period);
            if (!instances.TryGetValue(period.Value, out var instance))
            {
                if (!upsertsHere)
                {
                    continue; // стирати нічого: екземпляра власника немає, комірок у ньому теж
                }

                // Ліниво й вузько: лише ця таблиця, без решти таблиць документа за період.
                instance = await rowStore
                    .EnsureTableInstanceAsync(context.Instance.DocumentId, context.Instance.TableDefId, period, ct)
                    .ConfigureAwait(false);
                instances[period.Value] = instance;
            }

            var rowIds = new Dictionary<string, long>(
                await rowStore.GetRowIdsAsync(instance.TableInstanceId, period, ct).ConfigureAwait(false),
                StringComparer.Ordinal);

            // Тіньовий рядок: у власника немає рядка з таким RowKey, а записати в нього треба.
            var missing = changes.Upserts
                .Where(u => ownerOf[u.Address.ColumnDefId] == period)
                .Select(u => changes.RowKeyById[u.Address.TableRowId])
                .Where(key => !rowIds.ContainsKey(key))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (missing.Count > 0)
            {
                var ordinal = await rowStore.GetMaxOrdinalAsync(instance.TableInstanceId, period, ct).ConfigureAwait(false) + 1;
                var created = await rowStore
                    .CreateRowsBatchAsync(
                        [new RowCreationBatch(instance.TableInstanceId, period, [.. missing.Select(RowKey.Create)], ordinal)], ct)
                    .ConfigureAwait(false);
                for (var i = 0; i < missing.Count; i++)
                {
                    rowIds[missing[i]] = created[0][i];
                }
            }

            ownerInstance[period.Value] = instance;
            ownerRowIds[period.Value] = rowIds;
        }

        // ── 3. Розкладка по екземплярах ──────────────────────────────────
        var ownUpserts = new List<CellRecord>();
        var ownDeletes = new List<CellAddress>();
        var physUpserts = new List<CellRecord>();
        var physDeletes = new List<CellAddress>();
        var ownerUpserts = new Dictionary<int, List<CellRecord>>();
        var ownerDeletes = new Dictionary<int, List<CellAddress>>();
        var outOfWindow = new List<CellAddress>();
        var physRowKeys = new Dictionary<long, string>(changes.RowKeyById);

        bool TryMap(CellAddress logical, out CellAddress physical, out int ownerPeriod)
        {
            ownerPeriod = ownerOf[logical.ColumnDefId].Value;
            physical = logical;
            if (ownerPeriod == context.PeriodKey.Value)
            {
                return true;
            }

            if (!ownerRowIds.TryGetValue(ownerPeriod, out var rows)
                || !changes.RowKeyById.TryGetValue(logical.TableRowId, out var key)
                || !rows.TryGetValue(key, out var ownerRowId))
            {
                return false; // немає ні екземпляра, ні рядка власника — стирати нічого
            }

            physical = new CellAddress(new PeriodKey(ownerPeriod), ownerRowId, logical.ColumnDefId);
            physRowKeys[ownerRowId] = key;
            return true;
        }

        foreach (var upsert in changes.Upserts)
        {
            if (!TryMap(upsert.Address, out var physical, out var ownerPeriod))
            {
                continue;
            }

            var record = upsert with { Address = physical };
            physUpserts.Add(record);
            if (context.OutOfWindow.Contains(upsert.Address))
            {
                outOfWindow.Add(physical);
            }

            if (ownerPeriod == context.PeriodKey.Value)
            {
                ownUpserts.Add(upsert);
            }
            else
            {
                (ownerUpserts.TryGetValue(ownerPeriod, out var list) ? list : ownerUpserts[ownerPeriod] = []).Add(record);
            }
        }

        foreach (var delete in changes.Deletes)
        {
            if (!TryMap(delete, out var physical, out var ownerPeriod))
            {
                continue;
            }

            physDeletes.Add(physical);
            if (context.OutOfWindow.Contains(delete))
            {
                outOfWindow.Add(physical);
            }

            if (ownerPeriod == context.PeriodKey.Value)
            {
                ownDeletes.Add(delete);
            }
            else
            {
                (ownerDeletes.TryGetValue(ownerPeriod, out var list) ? list : ownerDeletes[ownerPeriod] = []).Add(physical);
            }
        }

        // Власний набір: claim ВСІХ рядків запиту (версії клієнта), навіть коли власних комірок немає.
        var viewSet = new CellChangeSet(
            context.Instance.TableInstanceId, ownUpserts, ownDeletes, changes.Touched,
            context.UserId, isLateEdit, changes.ExpectedRowVersions);

        var ownerSets = new List<CellChangeSet>();
        var groups = new List<RecalcGroup>();
        var approverSheet = context.ApproverEdit ? context.Table.SheetDefId : (int?)null;

        if (ownUpserts.Count + ownDeletes.Count > 0)
        {
            groups.Add(new RecalcGroup(
                context.Instance.TableInstanceId,
                context.PeriodKey.Value,
                [.. ownUpserts.Select(u => u.Address).Concat(ownDeletes)
                    .Select(a => new RecalculationSeed(a.TableRowId, a.ColumnDefId, approverSheet)).Distinct()],
                [.. ownUpserts.Select(u => u.Address.ColumnDefId).Concat(ownDeletes.Select(d => d.ColumnDefId)).Distinct()]));
        }

        foreach (var period in ownerInstance.Keys.Order())
        {
            var upserts = ownerUpserts.GetValueOrDefault(period) ?? [];
            var deletes = ownerDeletes.GetValueOrDefault(period) ?? [];
            if (upserts.Count + deletes.Count == 0)
            {
                continue;
            }

            var instance = ownerInstance[period];
            var touched = upserts.Select(u => u.Address.TableRowId).Concat(deletes.Select(d => d.TableRowId)).Distinct().ToList();

            // Очікуваних версій немає (див. примітку класу); «дотик» рядків власника — сховищем.
            ownerSets.Add(new CellChangeSet(
                instance.TableInstanceId, upserts, deletes, touched, context.UserId, isLateEdit, ExpectedRowVersions: null));

            groups.Add(new RecalcGroup(
                instance.TableInstanceId,
                period,
                [.. upserts.Select(u => u.Address).Concat(deletes)
                    .Select(a => new RecalculationSeed(a.TableRowId, a.ColumnDefId, approverSheet)).Distinct()],
                [.. upserts.Select(u => u.Address.ColumnDefId).Concat(deletes.Select(d => d.ColumnDefId)).Distinct()]));
        }

        // ── 4. Попередні значення — на ФІЗИЧНИХ адресах власників ────────
        var previousPhysical = new Dictionary<CellAddress, CellValueData>(previous);
        var ownerAddresses = physUpserts.Select(u => u.Address).Concat(physDeletes)
            .Where(a => a.PeriodKey != context.PeriodKey).Distinct().ToList();
        if (ownerAddresses.Count > 0)
        {
            foreach (var (address, value) in await cellStore.ReadCellsAsync(ownerAddresses, ct).ConfigureAwait(false))
            {
                previousPhysical[address] = value;
            }
        }

        var physical2 = new CellChangeLists(
            physUpserts, physDeletes, changes.Touched, physRowKeys, changes.ExpectedRowVersions);

        return new RoutedWrite(viewSet, ownerSets, physical2, previousPhysical, outOfWindow, groups);
    }
}
