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
    EcrDbContext db)
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
    /// <returns>Скільки періодів-читачів поставлено.</returns>
    public async Task<int> EnqueueReadersAsync(
        long documentId,
        PeriodKey source,
        IReadOnlyCollection<ValueTuple<int, int>> changed,
        bool onlyEarlierReaders,
        int hops,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changed);

        if (changed.Count == 0 || hops > MaxHops)
        {
            return 0;
        }

        var instances = await rowStore.GetTableInstancesAsync(documentId, source, ct).ConfigureAwait(false);
        var readers = new SortedSet<int>();
        var carried = new HashSet<(int TableDefId, int ColumnDefId)>();
        IReadOnlySet<int>? existing = null;

        foreach (var versionId in instances.Select(i => i.TemplateVersionId).Distinct())
        {
            var dependencies = await versions.ListFormulaDependenciesAsync(versionId, ct).ConfigureAwait(false);

            var relevant = CrossPeriodReaders.ReadAcrossPeriods(dependencies, changed);
            if (relevant.Count == 0)
            {
                continue;
            }

            existing ??= await ExistingSequencesAsync(documentId, source.Year, ct).ConfigureAwait(false);

            readers.UnionWith(CrossPeriodReaders.ReaderSequences(dependencies, relevant, source.Sequence, existing));
            carried.UnionWith(relevant);
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

            // Елемент масиву `cells` — «джерело зміни», а не адреса комірки: `RowId = 0` його відрізняє,
            // а злиття задач (`JobPayloadMerge`) об'єднує масив і прибирає повтори.
            var cells = carried
                .OrderBy(c => c.TableDefId).ThenBy(c => c.ColumnDefId)
                .Select(c => new DirtyCell(0, c.ColumnDefId, c.TableDefId, source.Sequence, hops))
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
