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
        => await db.RegistryEntryKeys
            .Where(k => k.RegistryEntryId == registryEntryId)
            .ToListAsync(ct).ConfigureAwait(false);

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
}
