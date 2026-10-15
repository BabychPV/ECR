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
/// ⛔ Чинність запису = ВСІ умови:
/// 1) TTL 15 с (<see cref="Ttl"/>, годинник <c>IClock</c>) — страховка від усього, чого не видно нижче;
/// 2) ПОВНА епоха процесу (<see cref="StaleCountsEpoch.ValueFor"/>): перемикання актуального прогону й правка
///    довідника піднімають її ПІСЛЯ коміту — синхронне перезавантаження;
/// 3) відбиток стану прогонів періоду (кількість і суми <c>Id</c> актуальних прогонів): міжпроцесна подія —
///    прогін завершила служба Worker, документ видалено разом із прогонами — змінює його, навіть якщо епоха цього
///    процесу не рухалась;
/// 4) відбиток довідників (<c>cfg.RegistryDef.DataChangedAt</c> і ребра <c>cfg.RegistryUse</c>), теж з іншого процесу;
/// 5) НАКЛАДКА «брудних документів» (D-432): правка комірки повної епохи НЕ піднімає, а позначає документ
///    (<see cref="StaleCountsEpoch.MarkDirty(int, IEnumerable{long})"/>). Читач, чий запис відстає від поточного номера
///    позначок, лікує його ВУЗЬКИМ запитом по цих документах (<c>IX_CellChange_Cell</c>, провідна колонка
///    <c>DocumentId</c>) і замінює записи лише цих документів: «власна правка видна одразу» (D-424) збережено без
///    повного завантаження. Понад <see cref="MaxIncremental"/> документів або усічений журнал позначок - повне
///    синхронне завантаження, як і раніше.
/// Відбитки — СТАН закомічених рядків (кількість, суми, <c>CHECKSUM_AGG</c>), а не «максимум часу»: будь-який
/// наступний коміт, що змінює вхід предиката, змінює й відбиток, хоч би в якому порядку закомітились писачі.
/// Відбиток, епоха й номер позначок читаються ДО запиту карти: запис, що закомітився між ними, лише зайвий раз
/// ще раз полікує/скине кеш.
///
/// ⚠ ПРОГАЛИНА, названа прямо: нові рядки <c>aud.CellChange</c>, записані ІНШИМ процесом (імпорт і збір у Worker;
/// це не <c>Recalculation</c>, яку предикат ігнорує), відбитком і позначками не видно — дешевого відбитка стану
/// для партиційованого журналу на мільйони рядків немає. Власні PATCH — одразу.
///
/// ⚠ SWR (D-424, LP-45): коли епоха й відбиток ЗБІГАЮТЬСЯ, а запис протермінований за TTL не більш ніж на
/// <see cref="StaleWhileRevalidateWindow"/> (120 с), читач одразу отримує СТАРУ карту (вже ВИЛІКУВАНУ накладкою), а
/// оновлення йде у фоні одним завантаженням (single-flight за ключем, власний scope через <c>IServiceScopeFactory</c>,
/// без токена запиту). Зміна повної епохи чи відбитка — синхронне перезавантаження. НОВА МЕЖА видимості:
/// між-процесні правки журналу (імпорт, збір у Worker) видно до TTL (15 с) + час фонового завантаження. Без
/// <c>IServiceScopeFactory</c> (тести без DI) SWR вимкнено.
///
/// ⛔ Single-flight: N одночасних читачів одного (період, ключ) у ОДНОМУ стані (епоха + відбиток + номер позначок)
/// запускають ОДИН запит; решта чекає його результат. Ключ спільного завантаження містить епоху, відбиток і номер
/// позначок, тож читач ПІСЛЯ коміту не приєднується до завантаження, що почалось ДО нього. Завантаження робить
/// перший читач на СВОЄМУ контексті й доводить його до кінця, не зважаючи на власний токен (контекст не можна
/// кидати посеред команди, а решта читачів чекає); читачі-слідувачі чекають через <c>WaitAsync(ct)</c> — їх
/// скасування спільного запиту не чіпає. Помилка не кешується: запис про завантаження знімається.
///
/// ⚠ Пам'ять: два записи на період — «*» (будь-який автор + довідники) і «автори» (один запис зі словником
/// <c>userId → карта</c>, D-432: один <c>GROUP BY ChangedByUserId, DocumentId</c> замість запиту на кожного автора).
/// Карта містить лише застарілі документи періоду. Понад <c>maxDocuments</c> (для авторів — пар, ×2) — не
/// кидаємо: карта «переповнена» (кешується лише цей факт), викликачі беруть прямий запит (як у 1885c2c77), у журнал
/// іде одне Warning.
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

    /// <summary>
    /// Скільки брудних документів ще лікує вузький запит (D-432); більше - повне завантаження. Поріг НЕ запускає
    /// SWR на епоху (крок 2 дизайну LP-45 не реалізовано: лише якщо імпорт покаже шторм).
    /// </summary>
    public const int MaxIncremental = 200;

    /// <summary>Життя запису (страховка): між-процесні правки видно ≤15 с; власні PATCH - одразу.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Життя кешу відбитка довідників (D-430): довідники міняються рідко, а відбиток був запитом на КОЖЕН виклик
    /// переліку. Власна правка довідника скидає кеш одразу (епоха), чужий процес - до 60 с.
    /// </summary>
    public static readonly TimeSpan RegistryFingerprintTtl = TimeSpan.FromSeconds(60);

    /// <summary>Тег вузького запиту накладки (видно в тексті SQL: тести рахують такі запити окремо від повних).</summary>
    public const string DeltaTag = "StaleMap:delta";

    /// <summary>Тег повного згрупованого запиту «автори».</summary>
    public const string AuthorsTag = "StaleMap:authors";

    private const string RegistryFingerprintKey = "doc-stale-map:registry-fingerprint";
    private const string InFlightKey = "doc-stale-map:in-flight";
    private const string OverflowWarnedKey = "doc-stale-map:overflow-warned";

    private static readonly IReadOnlyDictionary<long, DateTime?> NoDocuments = new Dictionary<long, DateTime?>();

    private static int overflowWarned;

    /// <summary>Рядок відбитка стану.</summary>
    public sealed record FingerprintRow(
        long RunCount, long RunIdSum, int RunChecksum, long RegistryCount, int RegistryChecksum, long UseCount, long UseIdSum);

    /// <summary>Карти кожного автора періоду: <c>userId → (документ → відколи)</c>.</summary>
    private sealed record AuthorMaps(IReadOnlyDictionary<int, IReadOnlyDictionary<long, DateTime?>> ByUser);

    /// <summary>
    /// Запис кешу. Ключ «*»: <c>Map = null</c> - переповнення. Ключ «автори»: <c>Authors = null</c> - переповнення.
    /// <c>DirtySeq</c> - номер позначок «брудних документів» на момент (завантаження | останнього лікування).
    /// </summary>
    private sealed record Entry(
        IReadOnlyDictionary<long, DateTime?>? Map, AuthorMaps? Authors, long Epoch, FingerprintRow Fingerprint,
        DateTime ExpiresAt, TimeSpan Window, long DirtySeq);

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
        var dirtySeq = epoch?.DirtySeq(periodKey) ?? 0;
        var fingerprint = await FingerprintAsync(periodKey, ct).ConfigureAwait(false);

        // Запис «*» і запис «автори» беруться ОДИН раз на виклик, хоч би скільки фільтрів автора просили.
        Entry? all = null;
        Entry? authors = null;
        for (var i = 0; i < byUserIds.Count; i++)
        {
            if (byUserIds[i] is not { } user)
            {
                all ??= await GetEntryAsync(false, periodKey, epochValue, dirtySeq, fingerprint, window, ct).ConfigureAwait(false);
                result[i] = all.Map;
                continue;
            }

            authors ??= await GetEntryAsync(true, periodKey, epochValue, dirtySeq, fingerprint, window, ct).ConfigureAwait(false);
            result[i] = authors.Authors is null
                ? null
                : authors.Authors.ByUser.TryGetValue(user, out var own) ? own : NoDocuments;
        }

        return result;
    }

    private static string KeyFor(int periodKey, bool authors)
        => authors ? $"doc-stale-map:{periodKey}:authors" : $"doc-stale-map:{periodKey}:*";

    private static bool IsOverflow(Entry entry, bool authors) => authors ? entry.Authors is null : entry.Map is null;

    private async Task<Entry> GetEntryAsync(
        bool authors, int periodKey, long epochValue, long dirtySeq, FingerprintRow fingerprint, TimeSpan? window,
        CancellationToken ct)
    {
        var key = KeyFor(periodKey, authors);

        // ⛔ Епоха й відбиток ЗБІГАЮТЬСЯ: стан не змінювався ні цим процесом, ні іншим (крім правок комірок, які
        // лікує накладка). Зміна повної епохи чи відбитка - синхронне перезавантаження нижче.
        if (cache!.TryGetValue(key, out Entry? found) && found is not null
            && found.Epoch == epochValue && found.Fingerprint == fingerprint)
        {
            var current = found;
            if (current.DirtySeq < dirtySeq && !IsOverflow(current, authors))
            {
                current = await HealAsync(key, authors, periodKey, current, epochValue, fingerprint, dirtySeq)
                    .ConfigureAwait(false);
            }

            if (current is not null)
            {
                var now = clock!.UtcNow;
                if (current.ExpiresAt > now)
                {
                    return current;
                }

                // ⛔ SWR лише на TTL-складову (D-424): віддається вже вилікувана карта.
                if (scopeFactory is not null && now - current.ExpiresAt <= current.Window)
                {
                    StartBackgroundRefresh(key, authors, periodKey, epochValue, fingerprint, current.Window, dirtySeq);
                    return current;
                }
            }
        }

        var keep = window ?? StaleWhileRevalidateWindow;
        var loaded = await SingleFlightAsync(
            $"{key}|{epochValue}|{fingerprint}|{dirtySeq}",
            () => cache!.TryGetValue(key, out Entry? raced) && raced is not null && raced.ExpiresAt > clock!.UtcNow
                  && raced.Epoch == epochValue && raced.Fingerprint == fingerprint && raced.DirtySeq >= dirtySeq
                ? raced
                : null,
            async () =>
            {
                // ⚠ Токен читача сюди НЕ йде (див. remarks): лідер доводить власний контекст до кінця.
                var (map, byUser) = await LoadSnapshotAsync(authors, periodKey, CancellationToken.None).ConfigureAwait(false);
                var entry = new Entry(map, byUser, epochValue, fingerprint, clock!.UtcNow + Ttl, keep, dirtySeq);
                cache!.Set(key, entry, Ttl + keep);
                return entry;
            },
            ct).ConfigureAwait(false);

        return loaded!;
    }

    /// <summary>
    /// Лікує запис вузьким запитом по брудних документах (D-432). <c>null</c> - накладку застосувати не можна
    /// (журнал позначок усічено, документів понад <see cref="MaxIncremental"/>, карта перетнула стелю): викликач
    /// робить повне завантаження.
    /// </summary>
    private Task<Entry?> HealAsync(
        string key, bool authors, int periodKey, Entry old, long epochValue, FingerprintRow fingerprint, long dirtySeq)
        => SingleFlightAsync(
            $"{key}|{epochValue}|{fingerprint}|heal|{old.DirtySeq}|{dirtySeq}",
            () => cache!.TryGetValue(key, out Entry? raced) && raced is not null
                  && raced.Epoch == epochValue && raced.Fingerprint == fingerprint && raced.DirtySeq >= dirtySeq
                ? raced
                : null,
            () => ApplyDeltaAsync(key, authors, periodKey, old),
            CancellationToken.None);

    private async Task<Entry?> ApplyDeltaAsync(string key, bool authors, int periodKey, Entry old)
    {
        // ⛔ Номер читається ДО запиту: правка, що закомітилась під час запиту, лише дасть ще одне лікування.
        if (epoch is null || !epoch.TryDirtySince(periodKey, old.DirtySeq, MaxIncremental, out var ids, out var seq))
        {
            return null;
        }

        var healed = ids.Count == 0
            ? old with { DirtySeq = seq }
            : authors
                ? await HealAuthorsAsync(old, periodKey, ids, seq).ConfigureAwait(false)
                : await HealAllAsync(old, periodKey, ids, seq).ConfigureAwait(false);
        if (healed is null)
        {
            return null;
        }

        // Строк чинності лишається ПЕРВІСНИМ: лікування не продовжує TTL (між-процесні правки - страховка TTL).
        cache!.Set(key, healed, Ttl + healed.Window);
        return healed;
    }

    private async Task<Entry?> HealAllAsync(Entry old, int periodKey, IReadOnlyList<long> ids, long seq)
    {
        var rows = await StaleResultsQuery.Documents(db, periodKey)
            .Where(r => ids.Contains(r.DocumentId))
            .TagWith(DeltaTag)
            .OrderBy(r => r.DocumentId)
            .Take(MaxIncremental + 1)
            .ToListAsync(CancellationToken.None)
            .ConfigureAwait(false);

        var map = new Dictionary<long, DateTime?>(old.Map!);
        foreach (var id in ids)
        {
            map.Remove(id);
        }

        foreach (var row in rows)
        {
            map[row.DocumentId] = Utc(row.Since);
        }

        // Карта перетнула стелю: повне завантаження сам вирішить, що з цим робити.
        return map.Count > maxDocuments ? null : old with { Map = map, DirtySeq = seq };
    }

    private async Task<Entry?> HealAuthorsAsync(Entry old, int periodKey, IReadOnlyList<long> ids, long seq)
    {
        var rows = await StaleResultsQuery.AuthorDocuments(db, periodKey)
            .Where(r => ids.Contains(r.DocumentId))
            .TagWith(DeltaTag)
            .OrderBy(r => r.UserId)
            .ThenBy(r => r.DocumentId)
            .Take(AuthorPairLimit + 1)
            .ToListAsync(CancellationToken.None)
            .ConfigureAwait(false);

        var dirty = ids.ToHashSet();
        var byUser = new Dictionary<int, IReadOnlyDictionary<long, DateTime?>>(old.Authors!.ByUser.Count);
        var pairs = 0;
        foreach (var (user, map) in old.Authors.ByUser)
        {
            // Незачеплені автори ділять словник зі старим записом (словники після публікації не змінюються).
            var copy = ids.Any(map.ContainsKey) ? new Dictionary<long, DateTime?>(map) : null;
            if (copy is not null)
            {
                foreach (var id in dirty)
                {
                    copy.Remove(id);
                }
            }

            byUser[user] = copy ?? map;
        }

        foreach (var group in rows.GroupBy(r => r.UserId))
        {
            var map = byUser.TryGetValue(group.Key, out var existing)
                ? new Dictionary<long, DateTime?>(existing)
                : new Dictionary<long, DateTime?>();
            foreach (var row in group)
            {
                map[row.DocumentId] = Utc(row.Since);
            }

            byUser[group.Key] = map;
        }

        foreach (var map in byUser.Values)
        {
            pairs += map.Count;
        }

        foreach (var user in byUser.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList())
        {
            byUser.Remove(user);
        }

        return pairs > AuthorPairLimit ? null : old with { Authors = new AuthorMaps(byUser), DirtySeq = seq };
    }

    private int AuthorPairLimit => checked(maxDocuments * 2);

    private static DateTime? Utc(DateTime? since)
        // Kind::Unspecified з datetime2 - у JSON без `Z` читався б як локальний час; журнал пише UTC (L3-01).
        => since is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;

    /// <summary>
    /// Single-flight за ключем: перший читач виконує <paramref name="work"/>, решта чекає його результат. Реєстр
    /// живе в кеші. <paramref name="recheck"/> - між переглядом і взяттям лідерства попередній лідер міг закінчити.
    /// </summary>
    private async Task<Entry?> SingleFlightAsync(
        string flightKey, Func<Entry?> recheck, Func<Task<Entry?>> work, CancellationToken ct)
    {
        var inFlight = InFlightRegistry();
        var mine = new TaskCompletionSource<Entry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = inFlight.GetOrAdd(flightKey, mine);

        if (!ReferenceEquals(existing, mine))
        {
            return await existing.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var raced = recheck();
            if (raced is not null)
            {
                mine.SetResult(raced);
                return raced;
            }

            var done = await work().ConfigureAwait(false);
            mine.SetResult(done);
            return done;
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
    private ConcurrentDictionary<string, TaskCompletionSource<Entry?>> InFlightRegistry()
    {
        lock (cache!)
        {
            if (!cache.TryGetValue(InFlightKey, out ConcurrentDictionary<string, TaskCompletionSource<Entry?>>? found)
                || found is null)
            {
                found = new ConcurrentDictionary<string, TaskCompletionSource<Entry?>>();
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

    /// <summary>
    /// Фонове оновлення протермінованого запису: ОДНЕ на (ключ, епоха, відбиток) - решта читачів отримує стару карту й
    /// нічого не запускає. Власний scope і контекст (контекст читача завершиться разом із запитом), без токена запиту.
    /// Помилка не кешується: стара карта лишається до кінця вікна, наступний читач запускає нову спробу.
    /// </summary>
    private void StartBackgroundRefresh(
        string key, bool authors, int periodKey, long epochValue, FingerprintRow fingerprint, TimeSpan window, long dirtySeq)
    {
        var inFlight = InFlightRegistry();
        var flightKey = $"{key}|{epochValue}|{fingerprint}|bg";
        var mine = new TaskCompletionSource<Entry?>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                var (map, byUser) = await new StaleResultsMapCache(backgroundDb, null, null, null, log, max)
                    .LoadSnapshotAsync(authors, periodKey, CancellationToken.None).ConfigureAwait(false);

                // Не затирати запис новішої епохи, що встиг покласти синхронний читач. Запис із новішим номером
                // позначок затирається безпечно: його номер лишається старим, наступний читач полікує дельту.
                var entry = new Entry(map, byUser, epochValue, fingerprint, time.UtcNow + Ttl, window, dirtySeq);
                if (!(memory.TryGetValue(key, out Entry? current) && current is not null && current.Epoch > epochValue))
                {
                    memory.Set(key, entry, Ttl + window);
                }

                mine.TrySetResult(entry);
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

    private async Task<(IReadOnlyDictionary<long, DateTime?>? Map, AuthorMaps? Authors)> LoadSnapshotAsync(
        bool authors, int periodKey, CancellationToken ct)
        => authors
            ? (null, await LoadAuthorsAsync(periodKey, ct).ConfigureAwait(false))
            : (await LoadAsync(periodKey, null, ct).ConfigureAwait(false), null);

    /// <summary>
    /// Карти всіх авторів періоду ОДНИМ запитом (D-432); <c>null</c> - пар «автор-документ» понад стелю.
    /// Еквівалентність поштучним <see cref="StaleResultsQuery.Documents"/> доводить
    /// <c>StaleResultsAuthorsEquivalenceTests</c>.
    /// </summary>
    private async Task<AuthorMaps?> LoadAuthorsAsync(int periodKey, CancellationToken ct)
    {
        var limit = AuthorPairLimit;
        var rows = await StaleResultsQuery.AuthorDocuments(db, periodKey)
            .TagWith(AuthorsTag)
            .OrderBy(r => r.UserId)
            .ThenBy(r => r.DocumentId)
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count > limit)
        {
            if (logger is not null && FirstOverflow())
            {
                LogOverflow(logger, periodKey, maxDocuments);
            }

            return null;
        }

        return new AuthorMaps(rows
            .GroupBy(r => r.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<long, DateTime?>)g.ToDictionary(r => r.DocumentId, r => Utc(r.Since))));
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
            if (logger is not null && FirstOverflow())
            {
                LogOverflow(logger, periodKey, maxDocuments);
            }

            return null;
        }

        return rows.ToDictionary(r => r.DocumentId, r => Utc(r.Since));
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
