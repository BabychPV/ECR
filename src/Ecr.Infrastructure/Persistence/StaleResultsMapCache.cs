using System.Collections.Concurrent;
using Ecr.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
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
/// партиційованого журналу на мільйони рядків немає. Власні PATCH — одразу.
///
/// ⚠ SWR (D-424, LP-45): коли епоха й відбиток ЗБІГАЮТЬСЯ, а запис протермінований за TTL не більш ніж на
/// <see cref="StaleWhileRevalidateWindow"/> (120 с), читач одразу отримує СТАРУ карту, а оновлення йде у фоні одним
/// завантаженням (single-flight за ключем, власний scope через <c>IServiceScopeFactory</c>, без токена запиту).
/// Зміна епохи чи відбитка — синхронне перезавантаження, як і раніше. НОВА МЕЖА видимості: між-процесні правки
/// журналу (імпорт, збір у Worker) видно до TTL (15 с) + час фонового завантаження (замість 15 с). Без
/// <c>IServiceScopeFactory</c> (тести без DI) SWR вимкнено.
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
    ILogger? logger = null, int maxDocuments = StaleResultsMapCache.DefaultMaxDocuments,
    IServiceScopeFactory? scopeFactory = null)
{
    /// <summary>
    /// Вікно stale-while-revalidate (D-424): запис, протермінований за TTL не більш ніж на цей час, а епоха й відбиток
    /// якого ЗБІГАЮТЬСЯ з поточними, віддається негайно, а оновлюється у фоні одним завантаженням.
    /// </summary>
    public static readonly TimeSpan StaleWhileRevalidateWindow = TimeSpan.FromSeconds(120);

    /// <summary>Стеля карти за замовчуванням: документів із застарілими результатами в одному періоді.</summary>
    public const int DefaultMaxDocuments = 200_000;

    /// <summary>Життя запису (страховка): між-процесні правки видно ≤15 с; власні PATCH - одразу.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Життя кешу відбитка довідників (D-430): довідники міняються рідко, а відбиток був запитом на КОЖЕН виклик
    /// переліку. Власна правка довідника скидає кеш одразу (епоха), чужий процес - до 60 с.
    /// </summary>
    public static readonly TimeSpan RegistryFingerprintTtl = TimeSpan.FromSeconds(60);

    private const string RegistryFingerprintKey = "doc-stale-map:registry-fingerprint";
    private const string InFlightKey = "doc-stale-map:in-flight";
    private const string OverflowWarnedKey = "doc-stale-map:overflow-warned";

    private static int overflowWarned;

    /// <summary>Рядок відбитка стану.</summary>
    public sealed record FingerprintRow(
        long RunCount, long RunIdSum, int RunChecksum, long RegistryCount, int RegistryChecksum, long UseCount, long UseIdSum);

    /// <summary><c>Map = null</c> - переповнення: кешується й воно, щоб не повторювати величезний запит щоразу.</summary>
    private sealed record Entry(
        IReadOnlyDictionary<long, DateTime?>? Map, long Epoch, FingerprintRow Fingerprint, DateTime ExpiresAt, TimeSpan Window);

    /// <summary>
    /// Карти періоду для кількох фільтрів автора за ОДНЕ читання відбитка: <c>null</c> — будь-який автор,
    /// число — лише правки цього користувача. Елемент <c>null</c> у відповіді — карта перевищила стелю:
    /// викликач бере прямий запит.
    /// </summary>
    /// <param name="periodKey">Період.</param>
    /// <param name="byUserIds">Фільтри автора.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="window">Власне вікно SWR запису, що кладеться (прогрів кільця: 15 хв); <c>null</c> - <see cref="StaleWhileRevalidateWindow"/>.</param>
    public async Task<IReadOnlyList<IReadOnlyDictionary<long, DateTime?>?>> GetAsync(
        int periodKey, IReadOnlyList<int?> byUserIds, CancellationToken ct, TimeSpan? window = null)
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
            result[i] = await GetOneAsync(periodKey, byUserIds[i], epochValue, fingerprint, ct, window).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<long, DateTime?>?> GetOneAsync(
        int periodKey, int? user, long epochValue, FingerprintRow fingerprint, CancellationToken ct, TimeSpan? window = null)
    {
        var key = $"doc-stale-map:{periodKey}:{(user is { } u ? u.ToString(System.Globalization.CultureInfo.InvariantCulture) : "*")}";

        if (TryHit(key, epochValue, fingerprint, out var hit))
        {
            return hit;
        }

        // ⛔ SWR лише на TTL-складову (D-424): епоха й відбиток ЗБІГАЮТЬСЯ - стан не змінювався ні цим процесом, ні
        // іншим; змінився лише вік. Інакше (власна правка, прогін, довідники) - синхронне перезавантаження нижче.
        if (scopeFactory is not null && TryStale(key, epochValue, fingerprint, out var old, out var oldWindow))
        {
            StartBackgroundRefresh(key, periodKey, user, epochValue, fingerprint, oldWindow);
            return old;
        }

        var inFlight = InFlightRegistry();

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
            var keep = window ?? StaleWhileRevalidateWindow;
            cache!.Set(key, new Entry(map, epochValue, fingerprint, clock!.UtcNow + Ttl, keep), Ttl + keep);
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

    /// <summary>
    /// Реєстр завантажень цього кешу. ⛔ `IMemoryCache.GetOrCreate` НЕ атомарний: при першому одночасному
    /// зверненні кожен читач створив би СВІЙ словник, лідерів стало б кілька - тому створення під замком кешу.
    /// </summary>
    private ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyDictionary<long, DateTime?>?>> InFlightRegistry()
    {
        lock (cache!)
        {
            if (!cache.TryGetValue(InFlightKey, out ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyDictionary<long, DateTime?>?>>? found)
                || found is null)
            {
                found = new ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyDictionary<long, DateTime?>?>>();
                cache.Set(InFlightKey, found);
            }

            return found;
        }
    }

    /// <summary>Чи вже писали Warning про переповнення: на кеш (тобто на процес), а без кешу - статично.</summary>
    private bool FirstOverflow()
    {
        if (cache is null)
        {
            return Interlocked.Exchange(ref overflowWarned, 1) == 0;
        }

        lock (cache)
        {
            if (cache.TryGetValue(OverflowWarnedKey, out _))
            {
                return false;
            }

            cache.Set(OverflowWarnedKey, true);
            return true;
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

    /// <summary>Запис протермінований за TTL не більш ніж на вікно SWR, а епоха й відбиток збігаються.</summary>
    private bool TryStale(
        string key, long epochValue, FingerprintRow fingerprint, out IReadOnlyDictionary<long, DateTime?>? map, out TimeSpan window)
    {
        if (cache!.TryGetValue(key, out Entry? hit) && hit is not null
            && hit.Epoch == epochValue && hit.Fingerprint == fingerprint
            && hit.ExpiresAt <= clock!.UtcNow && clock.UtcNow - hit.ExpiresAt <= hit.Window)
        {
            map = hit.Map;
            window = hit.Window;
            return true;
        }

        map = null;
        window = StaleWhileRevalidateWindow;
        return false;
    }

    /// <summary>
    /// Фонове оновлення протермінованого запису: ОДНЕ на (ключ, епоха, відбиток) - решта читачів отримує стару карту й
    /// нічого не запускає. Власний scope і контекст (контекст читача завершиться разом із запитом), без токена запиту.
    /// Помилка не кешується: стара карта лишається до кінця вікна, наступний читач запускає нову спробу.
    /// </summary>
    private void StartBackgroundRefresh(
        string key, int periodKey, int? user, long epochValue, FingerprintRow fingerprint, TimeSpan window)
    {
        var inFlight = InFlightRegistry();
        var flightKey = $"{key}|{epochValue}|{fingerprint}";
        var mine = new TaskCompletionSource<IReadOnlyDictionary<long, DateTime?>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ReferenceEquals(inFlight.GetOrAdd(flightKey, mine), mine))
        {
            return;
        }

        var memory = cache!;
        var time = clock!;
        var factory = scopeFactory!;
        var log = logger;
        var max = maxDocuments;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = factory.CreateAsyncScope();
                var backgroundDb = scope.ServiceProvider.GetRequiredService<EcrDbContext>();
                var map = await new StaleResultsMapCache(backgroundDb, null, null, null, log, max)
                    .LoadAsync(periodKey, user, CancellationToken.None).ConfigureAwait(false);

                // Не затирати запис новішої епохи, що встиг покласти синхронний читач.
                if (!(memory.TryGetValue(key, out Entry? current) && current is not null && current.Epoch > epochValue))
                {
                    memory.Set(key, new Entry(map, epochValue, fingerprint, time.UtcNow + Ttl, window), Ttl + window);
                }

                mine.TrySetResult(map);
            }
            catch (Exception ex)
            {
                mine.TrySetException(ex);
                _ = mine.Task.Exception;
                if (log is not null)
                {
                    LogRefreshFailed(log, ex, periodKey);
                }
            }
            finally
            {
                inFlight.TryRemove(flightKey, out _);
            }
        });
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Фонове оновлення карти застарілості періоду {PeriodKey} не вдалося; віддається попередня карта до кінця вікна, далі - синхронне завантаження.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception, int periodKey);

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
            if (logger is not null && FirstOverflow())
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

    /// <summary>Частина відбитка по довідниках: глобальна, не залежить від періоду (кешується <see cref="RegistryFingerprintTtl"/>).</summary>
    private sealed record RegistryPart(long RegistryCount, int RegistryChecksum, long UseCount, long UseIdSum);

    private sealed record RunPart(long RunCount, long RunIdSum, int RunChecksum);

    /// <summary>Запис кешу довідників: значення, момент завантаження, епоха процесу на момент завантаження.</summary>
    private sealed record RegistryEntry(RegistryPart Part, DateTime LoadedAt, long Epoch);

    /// <summary>Тримач запису й замка single-flight; живе в <see cref="IMemoryCache"/> під <see cref="RegistryFingerprintKey"/>.</summary>
    private sealed class RegistryHolder : IDisposable
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public volatile RegistryEntry? Entry;

        public void Dispose() => Gate.Dispose();
    }

    private async Task<FingerprintRow> FingerprintAsync(int periodKey, CancellationToken ct)
    {
        // Прогонова частина - на кожен виклик (міжпроцесна подія «Worker завершив прогін» видна одразу);
        // довідникова - з кешу на RegistryFingerprintTtl.
        var run = await db.Database
            .SqlQuery<RunPart>($"""
                SELECT
                    (SELECT COUNT_BIG(*) FROM calc.CalculationRun WHERE Status = N'Current' AND PeriodKey = {periodKey}) AS RunCount,
                    (SELECT COALESCE(SUM(CAST(Id AS bigint)), 0) FROM calc.CalculationRun WHERE Status = N'Current' AND PeriodKey = {periodKey}) AS RunIdSum,
                    (SELECT COALESCE(CHECKSUM_AGG(CHECKSUM(Id)), 0) FROM calc.CalculationRun WHERE Status = N'Current' AND PeriodKey = {periodKey}) AS RunChecksum
                """)
            .SingleAsync(ct)
            .ConfigureAwait(false);

        var registry = await RegistryPartAsync(ct).ConfigureAwait(false);
        return new FingerprintRow(
            run.RunCount, run.RunIdSum, run.RunChecksum,
            registry.RegistryCount, registry.RegistryChecksum, registry.UseCount, registry.UseIdSum);
    }

    /// <summary>
    /// Відбиток довідників з кешем 60 с (D-430). Чинний, поки: вік &lt; <see cref="RegistryFingerprintTtl"/> І епоха
    /// процесу не рухалась (власна правка довідника піднімає її на коміті - <c>UnitOfWork</c>). Single-flight за
    /// семафором: слідувачі чекають лідера й перечитують запис. Збій читання: запис до 2xTTL віддається як є,
    /// старіший - помилка йде вгору, як і до кешу.
    /// </summary>
    private async Task<RegistryPart> RegistryPartAsync(CancellationToken ct)
    {
        var holder = RegistryHolderFor();
        var epochNow = epoch?.GlobalValue ?? 0;
        if (RegistryFresh(holder, epochNow, out var fresh))
        {
            return fresh;
        }

        await holder.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (RegistryFresh(holder, epochNow, out fresh))
            {
                return fresh;
            }

            try
            {
                var part = await db.Database
                    .SqlQuery<RegistryPart>($"""
                        SELECT
                            (SELECT COUNT_BIG(*) FROM cfg.RegistryDef) AS RegistryCount,
                            (SELECT COALESCE(CHECKSUM_AGG(CHECKSUM(Id, DataChangedAt)), 0) FROM cfg.RegistryDef) AS RegistryChecksum,
                            (SELECT COUNT_BIG(*) FROM cfg.RegistryUse WHERE SourceKind = 1) AS UseCount,
                            (SELECT COALESCE(SUM(CAST(Id AS bigint)), 0) FROM cfg.RegistryUse WHERE SourceKind = 1) AS UseIdSum
                        """)
                    .SingleAsync(ct)
                    .ConfigureAwait(false);
                holder.Entry = new RegistryEntry(part, clock!.UtcNow, epochNow);
                return part;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && holder.Entry is { } old
                && old.Epoch == epochNow && clock!.UtcNow - old.LoadedAt < RegistryFingerprintTtl * 2)
            {
                return old.Part;
            }
        }
        finally
        {
            holder.Gate.Release();
        }
    }

    private bool RegistryFresh(RegistryHolder holder, long epochNow, out RegistryPart part)
    {
        if (holder.Entry is { } e && e.Epoch == epochNow && clock!.UtcNow - e.LoadedAt < RegistryFingerprintTtl)
        {
            part = e.Part;
            return true;
        }

        part = null!;
        return false;
    }

    private RegistryHolder RegistryHolderFor()
    {
        lock (cache!)
        {
            if (!cache.TryGetValue(RegistryFingerprintKey, out RegistryHolder? found) || found is null)
            {
                found = new RegistryHolder();
                cache.Set(RegistryFingerprintKey, found);
            }

            return found;
        }
    }

    /// <summary>
    /// Прогрів карти періоду для будь-якого автора («*»; ключ - той самий, що в запиті переліку): синхронне
    /// завантаження з single-flight, без SWR. Повертає кількість застарілих документів або <c>null</c> -
    /// переповнення. Для служби прогріву (<see cref="StaleMapWarmer"/>).
    /// </summary>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="window">Вікно SWR запису (кільце - 15 хв, щоб запис не зникав через 135 с).</param>
    internal async Task<int?> WarmAsync(int periodKey, CancellationToken ct, TimeSpan? window = null)
    {
        var maps = await GetAsync(periodKey, [null], ct, window).ConfigureAwait(false);
        return maps[0]?.Count;
    }
}
