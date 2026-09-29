// src/Ecr.Application/Ports/IRegistryKeyStore.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Складені ключі довідника при записі (RT-10a, FEATURE-REGISTRY-TABLES §4.3): опис ключів,
/// поточні значення запису й рядки <c>dic.RegistryEntryKey</c>.
/// </summary>
/// <remarks>
/// ⛔ Рядки <c>dic.RegistryEntryKey</c> пише ЛИШЕ <c>RegistryKeyService</c> через цей порт
/// (<c>D-151</c>): другий шлях запису розійшовся б із хешем рівно там, де мав спрацювати індекс.
///
/// ⚠ Окремий порт, а не члени <see cref="IRegistryStore"/>: у того десятки тестових підробок, і
/// кожна мусила б навчитися нових методів. Так само відокремлено <see cref="IDocumentKeyStore"/>.
/// </remarks>
public interface IRegistryKeyStore
{
    /// <summary>Активні ключі довідника з полями, упорядковані за <c>Id</c>.</summary>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryKeyDef>> ListActiveKeysAsync(int registryDefId, CancellationToken ct);

    /// <summary>
    /// Усі ключі довідника — і вимкнені — з полями, упорядковані за <c>Id</c>, <b>з відстеженням</b>
    /// (RT-11): опис змінює їм назву й активність.
    /// </summary>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryKeyDef>> ListKeysForUpdateAsync(int registryDefId, CancellationToken ct);

    /// <summary>Ставить новий ключ довідника на вставку (RT-11, опис довідника).</summary>
    /// <param name="key">Ключ; його поля вже збережені.</param>
    public void AddKey(RegistryKeyDef key);

    /// <summary>
    /// Значення полів запису такими, якими їх побачить збереження: з бази — відстежувані
    /// (зі змінами в пам'яті) — і щойно додані, ще не збережені.
    /// </summary>
    /// <param name="entry">Запис; новий (без <c>Id</c>) дає лише додані значення.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryValue>> ListCurrentValuesAsync(RegistryEntry entry, CancellationToken ct);

    /// <summary>Рядки ключів запису — відстежувані, щоб перерахунок оновив їх на місці.</summary>
    /// <param name="registryEntryId">Збережений запис.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryEntryKey>> ListEntryKeysAsync(long registryEntryId, CancellationToken ct);

    /// <summary>
    /// Живі записи з тим самим хешем ключа, крім <paramref name="exceptEntryId"/>. Читає під
    /// <c>UPDLOCK, HOLDLOCK</c>: паралельний запис того самого ключа чекає кінця транзакції,
    /// навіть коли ключ ще вільний (блокується діапазон, а не лише знайдені рядки).
    /// </summary>
    /// <remarks>
    /// ⛔ Має сенс лише всередині <see cref="IUnitOfWork.ExecuteInTransactionAsync"/>.
    ///
    /// ⚠ Блокування зупиняє ВСТАВКУ того самого ключа, але не другу таку саму перевірку: дві
    /// одночасні транзакції обидві бачать вільний ключ, і розводить їх лише
    /// <c>UX_RegistryEntryKey_Live</c> → 409 <c>keyTakenConcurrently</c> (RT-10b,
    /// <c>RegistryKeyRaceTests</c>).
    /// </remarks>
    /// <param name="registryKeyDefId">Ключ довідника.</param>
    /// <param name="keyHash">SHA-256 канонічного рядка.</param>
    /// <param name="exceptEntryId">Запис, що зберігається (0 — новий): сам із собою не конфліктує.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryKeyHolder>> FindLiveHoldersForUpdateAsync(
        int registryKeyDefId, byte[] keyHash, long exceptEntryId, CancellationToken ct);

    /// <summary>
    /// Живі записи, що тримають будь-який із хешів ключа, — пакетом і БЕЗ блокування (RT-10b):
    /// імпорт CSV знаходить за первинним ключем наявний запис рядка (§4.6).
    /// </summary>
    /// <remarks>
    /// ⚠ Лише для пошуку, не для перевірки: вільність ключа перед записом і далі стверджує
    /// <see cref="FindLiveHoldersForUpdateAsync"/> у транзакції.
    /// </remarks>
    /// <param name="registryKeyDefId">Ключ довідника.</param>
    /// <param name="keyHashes">Хеші; порожній набір — порожній результат без запиту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Тримачі з заповненим <see cref="RegistryKeyHolder.KeyHash"/>.</returns>
    public Task<IReadOnlyList<RegistryKeyHolder>> FindLiveHoldersAsync(
        int registryKeyDefId, IReadOnlyCollection<byte[]> keyHashes, CancellationToken ct);

    /// <summary>Коди записів за ідентифікаторами — для людського вигляду частини <c>Lookup</c>.</summary>
    /// <param name="registryEntryIds">Ідентифікатори; відсутні в результат не потрапляють.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyDictionary<long, string>> FindEntryCodesAsync(
        IReadOnlyCollection<long> registryEntryIds, CancellationToken ct);

    /// <summary>Коди одиниць за ідентифікаторами — для людського вигляду частини <c>Unit</c>.</summary>
    /// <param name="unitIds">Ідентифікатори; відсутні в результат не потрапляють.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyDictionary<int, string>> FindUnitCodesAsync(IReadOnlyCollection<int> unitIds, CancellationToken ct);

    /// <summary>Ставить новий рядок ключа на вставку разом із записом.</summary>
    /// <param name="key">Рядок ключа.</param>
    public void Add(RegistryEntryKey key);

    /// <summary>
    /// Пакетне читання для перерахунку ключів пакета (аудит P9): поточні значення й рядки ключів
    /// УСІХ записів — сталим числом запитів замість двох на запис. Доки не викликано
    /// <see cref="ForgetPreloaded"/>, <see cref="ListCurrentValuesAsync"/> і
    /// <see cref="ListEntryKeysAsync"/> для цих записів віддають прочитане тут, без звернення до бази.
    /// </summary>
    /// <remarks>
    /// ⚠ Лише оптимізація, не зміна змісту: результат поштучних методів той самий, що й без
    /// попереднього читання. Тому типова реалізація — нічого не робити (підробка, що її не знає,
    /// просто читає поштучно).
    ///
    /// ⛔ Прочитане живе до <see cref="ForgetPreloaded"/>: після збереження в базі з'являються
    /// рядки ключів, яких цей знімок не бачив (<c>ReleaseAsync</c> мусить читати базу).
    /// </remarks>
    /// <param name="entries">Записи пакета; нові (без <c>Id</c>) дають лише додані значення.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task PreloadAsync(IReadOnlyCollection<RegistryEntry> entries, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Бере <c>UPDLOCK, HOLDLOCK</c> на всі хеші ключа одним запитом на порцію (аудит P9) і
    /// запам'ятовує їхніх живих тримачів: <see cref="FindLiveHoldersForUpdateAsync"/> для цих
    /// хешів далі не ходить у базу.
    /// </summary>
    /// <remarks>
    /// ⛔ Те саме блокування діапазону, що й поштучний <see cref="FindLiveHoldersForUpdateAsync"/>
    /// (кожне значення <c>IN (…)</c> — окремий seek по <c>(RegistryKeyDefId, KeyHash)</c>), тож
    /// гонка RT-10b розводиться так само. Має сенс лише в транзакції.
    ///
    /// ⚠ Типова реалізація нічого не робить: тоді блокування бере поштучний метод.
    /// </remarks>
    /// <param name="registryKeyDefId">Ключ довідника.</param>
    /// <param name="keyHashes">Хеші; порожній набір — без запиту.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task LockLiveHoldersAsync(int registryKeyDefId, IReadOnlyCollection<byte[]> keyHashes, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Забуває прочитане <see cref="PreloadAsync"/> і <see cref="LockLiveHoldersAsync"/>: далі
    /// поштучні методи знову читають базу.
    /// </summary>
    public void ForgetPreloaded()
    {
    }
}

/// <summary>Живий запис, що вже тримає значення ключа.</summary>
/// <param name="EntryId">Запис.</param>
/// <param name="EntryCode">Код запису — для повідомлення про конфлікт.</param>
/// <param name="KeyText">Людський вигляд ключа, як його записано для цього запису.</param>
/// <param name="Window">Вікно чинності, яке тримає рядок ключа.</param>
/// <param name="KeyHash">
/// Хеш ключа; заповнює лише пакетний <see cref="IRegistryKeyStore.FindLiveHoldersAsync"/>, де
/// тримачі різних хешів ідуть одним списком.
/// </param>
public sealed record RegistryKeyHolder(
    long EntryId, string EntryCode, string KeyText, ValidityWindow Window, byte[]? KeyHash = null);
