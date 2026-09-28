using System.Data;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRegistryKeyStore"/> над <see cref="EcrDbContext"/> (RT-10a).</summary>
public sealed class RegistryKeyStore(EcrDbContext db) : IRegistryKeyStore
{
    /// <summary>Хешів в одному запиті <see cref="FindLiveHoldersAsync"/> і <see cref="LockLiveHoldersAsync"/>.</summary>
    public const int HashesPerQuery = 500;

    /// <summary>
    /// Ідентифікаторів в одному запиті <see cref="PreloadAsync"/> — та сама порція, що
    /// <c>RegistryStore.LookupChunkSize</c> у <c>ListValuesForEntriesAsync</c> (те саме читання
    /// значень): число параметрів команди обмежене явно, незалежно від режиму перекладу колекцій EF.
    /// </summary>
    public const int IdsPerQuery = 1000;

    /// <summary>Значення записів, прочитані <see cref="PreloadAsync"/>, — за самим об'єктом запису.</summary>
    private readonly Dictionary<RegistryEntry, List<RegistryValue>> _preloadedValues =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Рядки ключів збережених записів, прочитані <see cref="PreloadAsync"/>.</summary>
    private readonly Dictionary<long, List<RegistryEntryKey>> _preloadedKeys = [];

    /// <summary>Тримачі, заблоковані <see cref="LockLiveHoldersAsync"/>, — за (ключ, хеш у hex).</summary>
    private readonly Dictionary<(int KeyDefId, string Hash), List<RegistryKeyHolder>> _lockedHolders = [];

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryKeyDef>> ListActiveKeysAsync(int registryDefId, CancellationToken ct)
        => await db.RegistryKeyDefs.AsNoTracking()
            .Include(k => k.Fields)
            .Where(k => k.RegistryDefId == registryDefId && k.IsActive)
            .OrderBy(k => k.Id)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Запит до бази сам по собі не бачить значень, доданих у цій одиниці роботи (нове поле
    /// наявного запису, новий запис цілком), — їх дає <c>Local</c>. Навпаки, <c>Local</c> не має
    /// значень, яких цей запит не завантажив (виклик без <c>values</c>), — їх дає база.
    /// Відстежувані рядки з бази повертаються тими самими об'єктами, тож змінене в пам'яті
    /// значення видно саме зміненим.
    /// </remarks>
    public async Task<IReadOnlyList<RegistryValue>> ListCurrentValuesAsync(RegistryEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_preloadedValues.TryGetValue(entry, out var preloaded))
        {
            return preloaded;
        }

        var result = new List<RegistryValue>();
        if (entry.IsPersisted)
        {
            var entryId = entry.Id;
            result.AddRange(await db.RegistryValues
                .Where(v => v.RegistryEntryId == entryId)
                .ToListAsync(ct).ConfigureAwait(false));
        }

        var seen = new HashSet<RegistryValue>(result, ReferenceEqualityComparer.Instance);
        result.AddRange(db.RegistryValues.Local.Where(v =>
            (ReferenceEquals(v.Entry, entry) || (entry.IsPersisted && v.RegistryEntryId == entry.Id))
            && seen.Add(v)));

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryEntryKey>> ListEntryKeysAsync(long registryEntryId, CancellationToken ct)
        => _preloadedKeys.TryGetValue(registryEntryId, out var preloaded)
            ? preloaded
            : await db.RegistryEntryKeys
                .Where(k => k.RegistryEntryId == registryEntryId)
                .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Той самий зміст, що в поштучних <see cref="ListCurrentValuesAsync"/> і
    /// <see cref="ListEntryKeysAsync"/>: відстежувані рядки з бази (зі змінами в пам'яті) плюс
    /// додані в цій одиниці роботи значення з <c>Local</c>. Різниця лише в числі звернень:
    /// ⌈N / <see cref="IdsPerQuery"/>⌉ на значення й стільки ж на рядки ключів, а <c>Local</c>
    /// (кожне звернення до нього — <c>DetectChanges</c> по всьому контексту) проходиться один раз
    /// на пакет, а не раз на запис.
    /// </remarks>
    public async Task PreloadAsync(IReadOnlyCollection<RegistryEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);

        ForgetPreloaded();

        var byId = entries.Where(e => e.IsPersisted)
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.ToList());
        var stored = new Dictionary<long, List<RegistryValue>>();

        foreach (var chunk in byId.Keys.Chunk(IdsPerQuery))
        {
            var wanted = chunk.ToList();
            foreach (var value in await db.RegistryValues
                         .Where(v => wanted.Contains(v.RegistryEntryId))
                         .ToListAsync(ct).ConfigureAwait(false))
            {
                Bucket(stored, value.RegistryEntryId).Add(value);
            }

            foreach (var key in await db.RegistryEntryKeys
                         .Where(k => wanted.Contains(k.RegistryEntryId))
                         .ToListAsync(ct).ConfigureAwait(false))
            {
                Bucket(_preloadedKeys, key.RegistryEntryId).Add(key);
            }
        }

        var seen = new Dictionary<RegistryEntry, HashSet<RegistryValue>>(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries)
        {
            var values = entry.IsPersisted && stored.TryGetValue(entry.Id, out var fromDb) ? [.. fromDb] : new List<RegistryValue>();
            _preloadedValues[entry] = values;
            seen[entry] = new HashSet<RegistryValue>(values, ReferenceEqualityComparer.Instance);

            if (entry.IsPersisted)
            {
                _preloadedKeys.TryAdd(entry.Id, []);
            }
        }

        // Один прохід по Local на весь пакет — та сама умова, що в поштучному методі:
        // значення цього запису за посиланням або (для збереженого) за його Id.
        foreach (var value in db.RegistryValues.Local)
        {
            if (value.Entry is { } owner && _preloadedValues.ContainsKey(owner))
            {
                AddOnce(owner, value);
            }

            if (byId.TryGetValue(value.RegistryEntryId, out var owners))
            {
                foreach (var entry in owners)
                {
                    AddOnce(entry, value);
                }
            }
        }

        void AddOnce(RegistryEntry entry, RegistryValue value)
        {
            if (seen[entry].Add(value))
            {
                _preloadedValues[entry].Add(value);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Ті самі <c>UPDLOCK, HOLDLOCK</c> і той самий параметр <c>binary(32)</c>, що в
    /// <see cref="FindLiveHoldersForUpdateAsync"/>; хеші — порціями по <see cref="HashesPerQuery"/>.
    /// Блокується діапазон кожного хеша, навіть вільного, — вставка того самого ключа паралельною
    /// транзакцією чекає кінця цієї
    /// (<c>RegistryKeyBatchQueryTests.Пакетне_блокування_тримає_вільний_ключ_до_кінця_транзакції</c>).
    /// </remarks>
    public async Task LockLiveHoldersAsync(
        int registryKeyDefId, IReadOnlyCollection<byte[]> keyHashes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyHashes);

        foreach (var chunk in keyHashes.Chunk(HashesPerQuery))
        {
            foreach (var hash in chunk)
            {
                _lockedHolders.TryAdd((registryKeyDefId, Convert.ToHexString(hash)), []);
            }

            foreach (var holder in await LiveHoldersAsync(registryKeyDefId, chunk, forUpdate: true, ct).ConfigureAwait(false))
            {
                _lockedHolders[(registryKeyDefId, Convert.ToHexString(holder.KeyHash!))].Add(holder with { KeyHash = null });
            }
        }
    }

    /// <inheritdoc />
    public void ForgetPreloaded()
    {
        _preloadedValues.Clear();
        _preloadedKeys.Clear();
        _lockedHolders.Clear();
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ <c>HOLDLOCK</c> — не прикраса: без нього порожній результат не блокує НІЧОГО, і два
    /// одночасні записи вільного ключа обидва проходять перевірку. З ним блокується діапазон
    /// <c>(RegistryKeyDefId, KeyHash)</c>, і вставка того самого ключа чекає кінця транзакції
    /// (<c>RegistryKeyStoreTests.Блокування_тримає_вільний_ключ_до_кінця_транзакції</c>).
    /// Прецедент — <c>DocumentKeyStore.IsKeyTakenAsync</c>.
    ///
    /// ⚠ Хеш — параметром <c>binary(32)</c>, а не виведеним <c>varbinary(max)</c>: з типом
    /// <c>max</c> порівняння з колонкою індексу пошуком не буде.
    /// </remarks>
    public async Task<IReadOnlyList<RegistryKeyHolder>> FindLiveHoldersForUpdateAsync(
        int registryKeyDefId, byte[] keyHash, long exceptEntryId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyHash);

        // Хеш уже заблоковано пакетом у цій транзакції (LockLiveHoldersAsync): діапазон тримається
        // до її кінця, тож прочитане тоді — те саме, що дав би повторний запит.
        if (_lockedHolders.TryGetValue((registryKeyDefId, Convert.ToHexString(keyHash)), out var holders))
        {
            return [.. holders.Where(h => h.EntryId != exceptEntryId)];
        }

        var locked = db.RegistryEntryKeys.FromSqlRaw(
            "SELECT * FROM dic.RegistryEntryKey WITH (UPDLOCK, HOLDLOCK) "
            + "WHERE RegistryKeyDefId = @keyDefId AND KeyHash = @keyHash AND IsLive = 1",
            new SqlParameter("@keyDefId", SqlDbType.Int) { Value = registryKeyDefId },
            new SqlParameter("@keyHash", SqlDbType.Binary, RegistryEntryKey.KeyHashLength) { Value = keyHash });

        var rows = await (
                from k in locked.AsNoTracking()
                join e in db.RegistryEntries.AsNoTracking() on k.RegistryEntryId equals e.Id
                where k.RegistryEntryId != exceptEntryId
                orderby k.RegistryEntryId
                select new { e.Id, e.Code, k.KeyText, k.ValidFromKey, k.ValidTo })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(r => new RegistryKeyHolder(
                r.Id,
                r.Code,
                r.KeyText,
                new ValidityWindow(r.ValidFromKey == DateOnly.MinValue ? null : r.ValidFromKey, r.ValidTo)))
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Хеші — окремими параметрами <c>binary(32)</c> порціями по <see cref="HashesPerQuery"/>
    /// (стеля SQL Server — 2100 параметрів на запит), а не <c>Contains</c> по колекції: переклад
    /// колекції <c>byte[]</c> у параметр EF дає <c>varbinary(max)</c>, з яким порівняння з колонкою
    /// індексу пошуком не буде (та сама причина, що в <see cref="FindLiveHoldersForUpdateAsync"/>).
    /// </remarks>
    public async Task<IReadOnlyList<RegistryKeyHolder>> FindLiveHoldersAsync(
        int registryKeyDefId, IReadOnlyCollection<byte[]> keyHashes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyHashes);

        var result = new List<RegistryKeyHolder>();
        foreach (var chunk in keyHashes.Chunk(HashesPerQuery))
        {
            result.AddRange(await LiveHoldersAsync(registryKeyDefId, chunk, forUpdate: false, ct).ConfigureAwait(false));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, string>> FindEntryCodesAsync(
        IReadOnlyCollection<long> registryEntryIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryEntryIds);

        var ids = registryEntryIds.ToList();
        return await db.RegistryEntries.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.Code })
            .ToDictionaryAsync(e => e.Id, e => e.Code, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, string>> FindUnitCodesAsync(
        IReadOnlyCollection<int> unitIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unitIds);

        var ids = unitIds.ToList();
        return await db.Units.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Code })
            .ToDictionaryAsync(u => u.Id, u => u.Code, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Add(RegistryEntryKey key) => db.RegistryEntryKeys.Add(key);

    /// <summary>
    /// Живі тримачі порції хешів одним запитом; з <paramref name="forUpdate"/> — під
    /// <c>UPDLOCK, HOLDLOCK</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Хеші — окремими параметрами <c>binary(32)</c>, а не <c>Contains</c> по колекції: переклад
    /// колекції <c>byte[]</c> у параметр EF дає <c>varbinary(max)</c>, з яким порівняння з колонкою
    /// індексу пошуком не буде (та сама причина, що в <see cref="FindLiveHoldersForUpdateAsync"/>).
    /// </remarks>
    private async Task<List<RegistryKeyHolder>> LiveHoldersAsync(
        int registryKeyDefId, byte[][] chunk, bool forUpdate, CancellationToken ct)
    {
        var parameters = new List<object>(chunk.Length + 1)
        {
            new SqlParameter("@keyDefId", SqlDbType.Int) { Value = registryKeyDefId },
        };
        var names = new List<string>(chunk.Length);
        for (var i = 0; i < chunk.Length; i++)
        {
            var name = "@h" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            names.Add(name);
            parameters.Add(new SqlParameter(name, SqlDbType.Binary, RegistryEntryKey.KeyHashLength) { Value = chunk[i] });
        }

        // ⚠ Конкатенація в змінну (як `ConsistencyCheckJob`): підставляються лише ІМЕНА
        // параметрів `@h0`..`@hN` і сталий хінт, не значення — аналізатор EF1003 цього статично
        // не доводить.
        //
        // ⛔ FORCESEEK по (RegistryKeyDefId, KeyHash) — не прикраса. Без нього оптимізатор обирав
        // то пошук по UQ_RegistryEntryKey_Entry (усі рядки ключа), то скан кластерного індексу —
        // залежно від статистики на мить компіляції. Дві одночасні транзакції з РІЗНИМИ планами
        // брали діапазонні замки на різних індексах, обидві проходили перевірку, і вставки
        // ловили взаємоблокування 1205 (граф: RangeS-U на PK_RegistryEntryKey проти RangeS-U на
        // UQ_RegistryEntryKey_Entry, RegistryKeyRaceTests у повному прогоні після великих вставок).
        // Пошук по IX_RegistryEntryKey_Hash блокує рівно діапазон кожного хеша — той самий для
        // будь-якої транзакції, тож друга чекає першу, а не блокує її у відповідь.
        var sql =
            "SELECT * FROM dic.RegistryEntryKey"
            + (forUpdate ? " WITH (UPDLOCK, HOLDLOCK, FORCESEEK (IX_RegistryEntryKey_Hash (RegistryKeyDefId, KeyHash)))" : string.Empty)
            + " WHERE RegistryKeyDefId = @keyDefId AND IsLive = 1 AND KeyHash IN ("
            + string.Join(", ", names) + ")";
        var live = db.RegistryEntryKeys.FromSqlRaw(sql, [.. parameters]);

        var rows = await (
                from k in live.AsNoTracking()
                join e in db.RegistryEntries.AsNoTracking() on k.RegistryEntryId equals e.Id

                // ⚠ Дублює умову сирого SQL навмисно: межа вибірки видна в самому запиті
                // (сторож `LayerRulesTests.Правило_6…` не читає рядок SQL), а не лише в
                // рядку, зібраному вище.
                where k.RegistryKeyDefId == registryKeyDefId && k.IsLive
                orderby k.RegistryEntryId
                select new { e.Id, e.Code, k.KeyText, k.KeyHash, k.ValidFromKey, k.ValidTo })
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. rows.Select(r => new RegistryKeyHolder(
            r.Id,
            r.Code,
            r.KeyText,
            new ValidityWindow(r.ValidFromKey == DateOnly.MinValue ? null : r.ValidFromKey, r.ValidTo),
            r.KeyHash))];
    }

    private static List<T> Bucket<T>(Dictionary<long, List<T>> buckets, long id)
    {
        if (!buckets.TryGetValue(id, out var bucket))
        {
            buckets[id] = bucket = [];
        }

        return bucket;
    }
}
