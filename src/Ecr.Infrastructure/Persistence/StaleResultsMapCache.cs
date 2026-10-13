using System.Collections.Concurrent;
using Ecr.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Карта «документ → відколи результати застаріли» ОДНОГО періоду (LP-45, етап 2): ті самі дані, що дає
/// <see cref="StaleResultsQuery"/>, але для ВСЬОГО періоду одним запитом і з коротким кешем. З неї беруть позначку
/// в рядках ПЕРЕЛІКУ, фільтр <c>resultsStale</c> і лічильники <c>/documents/summary</c>. Картка одного документа
/// (<c>FindAsync</c>) карти НЕ використовує: її вузький запит по одному id дешевший за повну карту.
/// </summary>
/// <remarks>
/// ⛔ Карта НЕ залежить від читача: це «які документи періоду застаріли», без проєкту, ролі й гранту. Доступ
/// накладають викликачі, і ЗАВЖДИ тим, що вже відфільтровано доступом: рядки сторінки беруться з запиту документів
/// із межею грантів, лічильники рахуються як <c>CountAsync</c> по документах, видимих читачеві. Кеш не містить нічого,
/// чого б відповідь не дала й без нього, і чужий документ не з'являється в чужій відповіді через кеш.
///
/// ⛔ Чинність запису = ВСІ чотири умови:
/// 1) TTL 15 с (<see cref="Ttl"/>, годинник <c>IClock</c>) — страховка від усього, чого не видно нижче;
/// 2) епоха процесу (<see cref="StaleCountsEpoch.ValueFor"/>): запис комірок і перемикання актуального прогону
///    піднімають її ПІСЛЯ коміту — власна правка видна одразу;
/// 3) відбиток стану прогонів періоду (кількість і суми <c>Id</c> актуальних прогонів): міжпроцесна подія —
///    прогін завершила служба Worker, документ видалено разом із прогонами — змінює його, навіть якщо епоха цього
///    процесу не рухалась;
/// 4) відбиток довідників (<c>cfg.RegistryDef.DataChangedAt</c> і ребра <c>cfg.RegistryUse</c>), теж з іншого процесу.
/// Відбитки — СТАН закомічених рядків (кількість, суми, <c>CHECKSUM_AGG</c>), а не «максимум часу»: будь-який
/// наступний коміт, що змінює вхід предиката, змінює й відбиток, хоч би в якому порядку закомітились писачі.
/// Відбиток і епоха читаються ДО запиту карти: запис, що закомітився між ними, лише зайвий раз скине кеш.
///
/// ⚠ ПРОГАЛИНА, названа прямо: нові рядки <c>aud.CellChange</c>, записані ІНШИМ процесом (імпорт і збір у Worker;
/// це не <c>Recalculation</c>, яку предикат ігнорує), відбитком не видно — дешевого відбитка стану для
/// партиційованого журналу на мільйони рядків немає. Між-процесні правки видно ≤15 с; власні PATCH — одразу.
///
/// ⛔ Single-flight: N одночасних читачів одного (період, автор) у ОДНОМУ стані (епоха + відбиток) запускають ОДИН
/// запит; решта чекає його результат. Ключ спільного завантаження містить епоху й відбиток, тож читач ПІСЛЯ коміту
/// не приєднується до завантаження, що почалось ДО нього. Завантаження робить перший читач на СВОЄМУ контексті й
/// доводить його до кінця, не зважаючи на власний токен (контекст не можна кидати посеред команди, а решта
/// читачів чекає); читачі-слідувачі чекають через <c>WaitAsync(ct)</c> — їх скасування спільного запиту не чіпає.
/// Помилка не кешується: запис про завантаження знімається, наступний читач починає з нуля.
///
/// ⚠ Пам'ять: один запис на (період, автор-фільтр); карта містить лише застарілі документи періоду. Понад
/// <c>maxDocuments</c> — не кидаємо: карта «переповнена» (кешується лише цей факт), викликачі беруть прямий
/// запит (як у 1885c2c77), у журнал іде одне Warning.
/// </remarks>
internal sealed partial class StaleResultsMapCache(
    EcrDbContext db, IMemoryCache? cache, IClock? clock, StaleCountsEpoch? epoch,
    ILogger? logger = null, int maxDocuments = StaleResultsMapCache.DefaultMaxDocuments)
{
    /// <summary>Стеля карти за замовчуванням: документів із застарілими результатами в одному періоді.</summary>
    public const int DefaultMaxDocuments = 200_000;

    /// <summary>Життя запису (страховка): між-процесні правки видно ≤15 с; власні PATCH - одразу.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    private const string InFlightKey = "doc-stale-map:in-flight";

    private static int overflowWarned;

    /// <summary>Рядок відбитка стану.</summary>
    public sealed record FingerprintRow(
        long RunCount, long RunIdSum, int RunChecksum, long RegistryCount, int RegistryChecksum, long UseCount, long UseIdSum);

    /// <summary><c>Map = null</c> - переповнення: кешується й воно, щоб не повторювати величезний запит щоразу.</summary>
    private sealed record Entry(
        IReadOnlyDictionary<long, DateTime?>? Map, long Epoch, FingerprintRow Fingerprint, DateTime ExpiresAt);

    /// <summary>
    /// Карти періоду для кількох фільтрів автора за ОДНЕ читання відбитка: <c>null</c> — будь-який автор,
    /// число — лише правки цього користувача. Елемент <c>null</c> у відповіді — карта перевищила стелю:
    /// викликач бере прямий запит.
    /// </summary>
    /// <param name="periodKey">Період.</param>
    /// <param name="byUserIds">Фільтри автора.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<IReadOnlyDictionary<long, DateTime?>?>> GetAsync(
        int periodKey, IReadOnlyList<int?> byUserIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var result = new IReadOnlyDictionary<long, DateTime?>?[byUserIds.Count];
        if (cache is null || clock is null)
        {
            for (var i = 0; i < byUserIds.Count; i++)
            {
                result[i] = await LoadAsync(periodKey, byUserIds[i], ct).ConfigureAwait(false);
            }

            return result;
        }

        var epochValue = epoch?.ValueFor(periodKey) ?? 0;
        var fingerprint = await FingerprintAsync(periodKey, ct).ConfigureAwait(false);

        for (var i = 0; i < byUserIds.Count; i++)
        {
            result[i] = await GetOneAsync(periodKey, byUserIds[i], epochValue, fingerprint, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<long, DateTime?>?> GetOneAsync(
        int periodKey, int? user, long epochValue, FingerprintRow fingerprint, CancellationToken ct)
    {
        var key = $"doc-stale-map:{periodKey}:{(user is { } u ? u.ToString(System.Globalization.CultureInfo.InvariantCulture) : "*")}";

        if (TryHit(key, epochValue, fingerprint, out var hit))
        {
            return hit;
        }

        var inFlight = cache!.GetOrCreate(
            InFlightKey,
            _ => new ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyDictionary<long, DateTime?>?>>())!;

        // Слідувачі приєднуються лише до завантаження В ТОМУ САМОМУ стані (епоха + відбиток).
        var flightKey = $"{key}|{epochValue}|{fingerprint}";
        var mine = new TaskCompletionSource<IReadOnlyDictionary<long, DateTime?>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = inFlight.GetOrAdd(flightKey, mine);

        if (!ReferenceEquals(existing, mine))
        {
            return await existing.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            // Між попереднім переглядом і взяттям лідерства попередній лідер міг закінчити й покласти запис.
            if (TryHit(key, epochValue, fingerprint, out var raced))
            {
                mine.SetResult(raced);
                return raced;
            }

            // ⚠ Токен читача сюди НЕ йде (див. remarks): лідер доводить власний контекст до кінця.
            var map = await LoadAsync(periodKey, user, CancellationToken.None).ConfigureAwait(false);
            cache!.Set(key, new Entry(map, epochValue, fingerprint, clock!.UtcNow + Ttl), Ttl);
            mine.SetResult(map);
            return map;
        }
        catch (Exception ex)
        {
            mine.TrySetException(ex);
            _ = mine.Task.Exception;
            throw;
        }
        finally
        {
            inFlight.TryRemove(flightKey, out _);
        }
    }

    private bool TryHit(string key, long epochValue, FingerprintRow fingerprint, out IReadOnlyDictionary<long, DateTime?>? map)
    {
        if (cache!.TryGetValue(key, out Entry? hit) && hit is not null
            && hit.ExpiresAt > clock!.UtcNow && hit.Epoch == epochValue && hit.Fingerprint == fingerprint)
        {
            map = hit.Map;
            return true;
        }

        map = null;
        return false;
    }

    /// <summary>Карта без кешу: ті самі рядки, що й <see cref="StaleResultsQuery"/>, для всього періоду; <c>null</c> — переповнення.</summary>
    private async Task<IReadOnlyDictionary<long, DateTime?>?> LoadAsync(int periodKey, int? byUserId, CancellationToken ct)
    {
        var rows = await StaleResultsQuery.Documents(db, periodKey, byUserId)
            .OrderBy(r => r.DocumentId)
            .Take(maxDocuments + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⛔ Стеля - від нескінченної вибірки, а не законний обсяг: обрізана карта мовчки віддала б «не застарілі»
        // там, де вони застарілі, тож замість обрізання - сигнал «переповнено», викликач бере прямий запит.
        if (rows.Count > maxDocuments)
        {
            if (logger is not null && Interlocked.Exchange(ref overflowWarned, 1) == 0)
            {
                LogOverflow(logger, periodKey, maxDocuments);
            }

            return null;
        }

        // Kind::Unspecified з datetime2 - у JSON без `Z` читався б як локальний час; журнал пише UTC (L3-01).
        return rows.ToDictionary(
            r => r.DocumentId,
            r => r.Since is { } since ? (DateTime?)DateTime.SpecifyKind(since, DateTimeKind.Utc) : null);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Карта застарілості періоду {PeriodKey} перевищила {Max} документів: кеш карти вимкнено для цих запитів, перелік і зведення беруть прямий запит. Перегляньте стелю.")]
    private static partial void LogOverflow(ILogger logger, int periodKey, int max);

    private async Task<FingerprintRow> FingerprintAsync(int periodKey, CancellationToken ct)
    {
        return await db.Database
            .SqlQuery<FingerprintRow>($"""
                SELECT
                    (SELECT COUNT_BIG(*) FROM calc.CalculationRun WHERE Status = N'Current' AND PeriodKey = {periodKey}) AS RunCount,
                    (SELECT COALESCE(SUM(CAST(Id AS bigint)), 0) FROM calc.CalculationRun WHERE Status = N'Current' AND PeriodKey = {periodKey}) AS RunIdSum,
                    (SELECT COALESCE(CHECKSUM_AGG(CHECKSUM(Id)), 0) FROM calc.CalculationRun WHERE Status = N'Current' AND PeriodKey = {periodKey}) AS RunChecksum,
                    (SELECT COUNT_BIG(*) FROM cfg.RegistryDef) AS RegistryCount,
                    (SELECT COALESCE(CHECKSUM_AGG(CHECKSUM(Id, DataChangedAt)), 0) FROM cfg.RegistryDef) AS RegistryChecksum,
                    (SELECT COUNT_BIG(*) FROM cfg.RegistryUse WHERE SourceKind = 1) AS UseCount,
                    (SELECT COALESCE(SUM(CAST(Id AS bigint)), 0) FROM cfg.RegistryUse WHERE SourceKind = 1) AS UseIdSum
                """)
            .SingleAsync(ct)
            .ConfigureAwait(false);
    }
}
