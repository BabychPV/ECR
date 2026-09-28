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
    /// <remarks>⛔ Має сенс лише всередині <see cref="IUnitOfWork.ExecuteInTransactionAsync"/>.</remarks>
    /// <param name="registryKeyDefId">Ключ довідника.</param>
    /// <param name="keyHash">SHA-256 канонічного рядка.</param>
    /// <param name="exceptEntryId">Запис, що зберігається (0 — новий): сам із собою не конфліктує.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryKeyHolder>> FindLiveHoldersForUpdateAsync(
        int registryKeyDefId, byte[] keyHash, long exceptEntryId, CancellationToken ct);

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
}

/// <summary>Живий запис, що вже тримає значення ключа.</summary>
/// <param name="EntryId">Запис.</param>
/// <param name="EntryCode">Код запису — для повідомлення про конфлікт.</param>
/// <param name="KeyText">Людський вигляд ключа, як його записано для цього запису.</param>
/// <param name="Window">Вікно чинності, яке тримає рядок ключа.</param>
public sealed record RegistryKeyHolder(long EntryId, string EntryCode, string KeyText, ValidityWindow Window);
