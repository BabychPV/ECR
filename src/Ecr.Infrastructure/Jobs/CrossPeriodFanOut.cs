using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Після зміни періоду ставить перерахунок у ІНШІ періоди року, чиї формули читають змінені
/// колонки (<c>[Period:-N]</c>, <c>[Period:#k]</c>) — WW-03, закриття G2.
/// </summary>
/// <remarks>
/// ⛔ Постановка — лише <see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/> на ціль
/// «документ × період-читач» (<see cref="FormulaRecalculationTarget.Of"/>): серія правок і
/// ланцюжок читачів зливаються в одну задачу періоду, а не множаться; <c>EnqueueExclusive</c>
/// витіснив би задачу, що виконується. Стеля — один запис у чергу на період-читач за прогін
/// (≤ 11 для місячного року).
/// <para>
/// ⚠ Закриті періоди й періоди без стану не ставляться — «закриті автоматично не перераховуються
/// ніколи» (ТЗ 05-engines); подані аркуші відсікає сам запис прогону (<c>EnterSheetsAsync</c>).
/// Ланцюжок завершується сам: прогін-читач записує лише ЗМІНЕНЕ, а без змін далі нічого не ставиться.
/// </para>
/// </remarks>
public sealed class CrossPeriodFanOut(
    IRowStore rowStore,
    ITemplateVersionStore versions,
    IPeriodStore periods,
    IBackgroundJobScheduler jobs,
    EcrDbContext db,
    // Q-17: необов'язкові — прямі `new CrossPeriodFanOut(...)` у тестах їх не передають (правило власності вимкнено);
    // DI підставляє обидва, а роутер без прапора повертає «не застосовується».
    IMonthColumnRouter? router = null,
    IMetadataCache? metadata = null)
{
    /// <summary>
    /// Стеля довжини ланцюжка читачів. Місячний рік дає не більше 12 ланок у ациклічному графі;
    /// 24 лишає запас, а розбіжний цикл (`x = [Period:#2].x + 1` у парі періодів) зупиняється.
    /// </summary>
    public const int MaxHops = 24;

    /// <summary>Ставить перерахунок у періоди-читачі.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="source">Період, у якому змінилися колонки.</param>
    /// <param name="changed">
    /// Змінені колонки цього періоду: пари «таблиця, колонка». Тип записано як <c>ValueTuple&lt;int, int&gt;</c>, а не
    /// кортежем у дужках: сторож <c>ForbiddenApiTests</c> читає сигнатури регексом до першої закривної дужки.
    /// </param>
    /// <param name="onlyEarlierReaders">
    /// Лише читачі РАНІШЕ за джерело: після повного перерахунку року за зростанням пізніші
    /// читачі вже пораховані від остаточного джерела, а ранні (<c>[Period:#k]</c> з k пізніше) — ні.
    /// </param>
    /// <param name="hops">Номер ланки ланцюжка (0 — перша постановка після правки/повного прогону).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="inputChanged">
    /// Q-17: підмножина <paramref name="changed"/> — колонки, змінені ВВЕДЕННЯМ (насіння правки), не похідні. Лише вони
    /// можуть бути стовпцями з чужим власником (прапор <c>Collection:MonthColumnRouting</c>); <c>null</c> — правило
    /// власності не застосовується.
    /// </param>
    /// <returns>Скільки періодів-читачів поставлено.</returns>
    public async Task<int> EnqueueReadersAsync(
        long documentId,
        PeriodKey source,
        IReadOnlyCollection<ValueTuple<int, int>> changed,
        bool onlyEarlierReaders,
        int hops,
        CancellationToken ct,
        IReadOnlyCollection<ValueTuple<int, int>>? inputChanged = null)
    {
        ArgumentNullException.ThrowIfNull(changed);

        if (changed.Count == 0 || hops > MaxHops)
        {
            return 0;
        }

        var instances = await rowStore.GetTableInstancesAsync(documentId, source, ct).ConfigureAwait(false);
        var readers = new SortedSet<int>();
        var carried = new HashSet<(int TableDefId, int ColumnDefId)>();
        var carriedInputs = new HashSet<(int TableDefId, int ColumnDefId, int? MonthNumber)>();
        IReadOnlySet<int>? existing = null;

        foreach (var versionId in instances.Select(i => i.TemplateVersionId).Distinct())
        {
            var dependencies = await versions.ListFormulaDependenciesAsync(versionId, ct).ConfigureAwait(false);

            var relevant = CrossPeriodReaders.ReadAcrossPeriods(dependencies, changed);
            var routed = CrossPeriodReaders.ReadsRoutedInput(
                dependencies,
                await RoutedInputsAsync(documentId, versionId, inputChanged, ct).ConfigureAwait(false));
            if (relevant.Count == 0 && routed.Count == 0)
            {
                continue;
            }

            existing ??= await ExistingSequencesAsync(documentId, source.Year, ct).ConfigureAwait(false);

            readers.UnionWith(CrossPeriodReaders.ReaderSequences(dependencies, relevant, routed, source.Sequence, existing));
            carried.UnionWith(relevant);
            carriedInputs.UnionWith(routed);
        }

        var enqueued = 0;
        foreach (var reader in readers)
        {
            if (onlyEarlierReaders && reader >= source.Sequence)
            {
                continue;
            }

            var readerKey = PeriodKey.Create(source.Year, reader);
            var state = await periods.FindPeriodStateAsync(documentId, readerKey.Value, ct).ConfigureAwait(false);
            if (state is null or Ecr.Domain.Enums.PeriodState.Closed)
            {
                continue;
            }

            // L1: не ставимо задачу, яка б повернулася з `written = 0` (порожні цілі в періоді-читачі).
            if (!await ReaderHasWorkAsync(documentId, readerKey, source.Sequence, carried, carriedInputs, ct)
                    .ConfigureAwait(false))
            {
                continue;
            }

            // Елемент масиву `cells` — «джерело зміни», а не адреса комірки: `RowId = 0` його відрізняє,
            // а злиття задач (`JobPayloadMerge`) об'єднує масив і прибирає повтори.
            var cells = carried
                .OrderBy(c => c.TableDefId).ThenBy(c => c.ColumnDefId)
                .Select(c => new DirtyCell(0, c.ColumnDefId, c.TableDefId, source.Sequence, hops))
                .Concat(carriedInputs
                    .OrderBy(c => c.TableDefId).ThenBy(c => c.ColumnDefId)
                    .Select(c => new DirtyCell(
                        0, c.ColumnDefId, c.TableDefId, source.Sequence, hops, OwnerInput: true)))
                .ToList();

            await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                    FormulaRecalculationTarget.Of(documentId, readerKey.Value, createdByUserId: null),
                    new { DocumentId = documentId, TableInstanceId = 0L, PeriodKey = readerKey.Value, Cells = cells },
                    ct)
                .ConfigureAwait(false);
            enqueued++;
        }

        return enqueued;
    }

    /// <summary>
    /// L1: чи має період-читач хоч одну ціль для переданих змін — той самий відбір, що зробить задача
    /// (<see cref="CrossPeriodReaders.Targets"/> за залежностями ВЕРСІЇ ПЕРІОДУ-ЧИТАЧА), без плану й без запису.
    /// </summary>
    /// <remarks>
    /// ⛔ Помилитися можна лише в бік «так»: немає метаданих (<c>metadata is null</c>) — так; версія з активним
    /// Rollup/Check — так (<c>RecalculationService</c> йде далі й за порожніх цілей); у періоді немає екземплярів — ні
    /// (задача повернула б 0). Таблиця формули без екземпляра в періоді-читачі цілі не дає: сервіс її пропускає.
    /// </remarks>
    private async Task<bool> ReaderHasWorkAsync(
        long documentId,
        PeriodKey reader,
        int sourceSequence,
        IReadOnlyCollection<(int TableDefId, int ColumnDefId)> carried,
        IReadOnlyCollection<(int TableDefId, int ColumnDefId, int? MonthNumber)> carriedInputs,
        CancellationToken ct)
    {
        if (metadata is null)
        {
            return true;
        }

        var sources = carried
            .Select(c => new CrossPeriodSource(sourceSequence, c.TableDefId, c.ColumnDefId))
            .Concat(carriedInputs.Select(c => new CrossPeriodSource(sourceSequence, c.TableDefId, c.ColumnDefId, OwnerInput: true)))
            .ToList();

        var instances = await rowStore.GetTableInstancesAsync(documentId, reader, ct).ConfigureAwait(false);
        foreach (var group in instances.GroupBy(i => i.TemplateVersionId))
        {
            var snapshot = await metadata.GetAsync(group.Key, ct).ConfigureAwait(false);
            if (snapshot.HasActiveRollupOrCheck)
            {
                return true;
            }

            var dependencies = await versions.ListFormulaDependenciesAsync(group.Key, ct).ConfigureAwait(false);
            var liveFormulaTables = snapshot.Sheets
                .SelectMany(s => s.Tables)
                .SelectMany(t => t.Formulas.Where(f => !f.IsDeleted).Select(f => (Formula: f.Id, Table: t.Id)))
                .GroupBy(p => p.Formula)
                .ToDictionary(g => g.Key, g => g.First().Table);
            var presentTables = group.Select(i => i.TableDefId).ToHashSet();

            if (CrossPeriodReaders.HasTargets(dependencies, sources, reader.Sequence, liveFormulaTables, presentTables))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Q-17: з насіння правки — введені (не похідні) стовпці таблиць «місяці в колонках» місячного проєкту, чиї
    /// значення живуть в екземплярі-власнику й читаються з усіх екземплярів року. Прапор вимкнено, проєкт не
    /// <c>Monthly</c> або таблиця поза маршрутизацією — порожньо (поведінка не змінюється).
    /// </summary>
    private async Task<List<(int TableDefId, int ColumnDefId, int? MonthNumber)>> RoutedInputsAsync(
        long documentId,
        int templateVersionId,
        IReadOnlyCollection<ValueTuple<int, int>>? inputChanged,
        CancellationToken ct)
    {
        var result = new List<(int TableDefId, int ColumnDefId, int? MonthNumber)>();
        if (inputChanged is not { Count: > 0 } || router is not { Enabled: true } || metadata is null
            || await router.GetDocumentAsync(documentId, ct).ConfigureAwait(false) is null)
        {
            return result;
        }

        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var tables = snapshot.Sheets
            .Where(s => !s.IsDeleted)
            .SelectMany(s => s.Tables)
            .GroupBy(t => t.Id)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var (tableDefId, columnDefId) in inputChanged)
        {
            if (!tables.TryGetValue(tableDefId, out var table) || !router.AppliesToTable(table))
            {
                continue;
            }

            var column = table.Columns.FirstOrDefault(c => c.Id == columnDefId);
            if (column is null || column.IsDeleted || column.IsComputed)
            {
                continue;
            }

            result.Add((tableDefId, columnDefId, column.IsMonthColumn ? column.MonthNumber : null));
        }

        return result;
    }

    /// <summary>Номери періодів року, у яких документ має екземпляри таблиць.</summary>
    private async Task<IReadOnlySet<int>> ExistingSequencesAsync(long documentId, int year, CancellationToken ct)
    {
        var from = year * 100;
        var to = from + 99;

        var keys = await db.TableInstances
            .AsNoTracking()
            .Where(i => i.DocumentId == documentId && i.PeriodKeyValue >= from && i.PeriodKeyValue <= to)
            .Select(i => i.PeriodKeyValue)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return keys.Select(k => k - from).ToHashSet();
    }
}
