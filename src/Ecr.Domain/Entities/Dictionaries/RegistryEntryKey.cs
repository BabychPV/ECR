// src/Ecr.Domain/Entities/Dictionaries/RegistryEntryKey.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Dictionaries;

/// <summary>
/// Похідний рядок унікальності: хеш значень одного ключа одного запису
/// (FEATURE-REGISTRY-TABLES §3.2, §4.2, рішення <c>D-151</c>).
/// </summary>
/// <remarks>
/// ⛔ Руками не пишеться: будує й оновлює лише служба ключів (крок RT-10a)
/// на кожному шляху запису. Дані відтворювані (<c>D-71</c>): таблицю можна
/// перебудувати зі значень <c>dic.RegistryValue</c>.
///
/// ⚠ Унікальність тримає БАЗА — фільтрований <c>UX_RegistryEntryKey_Live</c>
/// на <c>(RegistryKeyDefId, KeyHash, ValidFromKey) WHERE IsLive = 1</c>. Тому
/// вікно й живість тут не задаються окремо, а ДЗЕРКАЛЯТЬ запис: другий
/// незалежний примірник тих самих фактів розійшовся б із записом рівно на
/// тому рядку, де індекс мав спрацювати.
/// </remarks>
public sealed class RegistryEntryKey : Entity<long>
{
    /// <summary>Довжина SHA-256 канонічного рядка, байт.</summary>
    public const int KeyHashLength = 32;

    /// <summary>Межа колонки <c>KeyText</c>.</summary>
    public const int MaxKeyTextLength = 900;

    private RegistryEntryKey() { }

    /// <summary>Створює рядок ключа для запису.</summary>
    /// <param name="entry">
    /// Запис. Навігація, а не число: у нового запису <c>Id</c> дорівнює нулю
    /// до збереження, і EF підставить справжній ключ сам (так само, як
    /// <see cref="RegistryValue"/>).
    /// </param>
    /// <param name="registryKeyDefId">Ключ довідника.</param>
    /// <param name="keyHash">SHA-256 канонічного рядка, рівно <see cref="KeyHashLength"/> байт.</param>
    /// <param name="keyText">Людський вигляд ключа для повідомлень.</param>
    /// <exception cref="ArgumentException">Хеш не тієї довжини або порожній текст.</exception>
    public RegistryEntryKey(RegistryEntry entry, int registryKeyDefId, byte[] keyHash, string keyText)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
        RegistryEntryId = entry.Id;
        RegistryKeyDefId = registryKeyDefId;
        Apply(entry, keyHash, keyText);
    }

    /// <summary>Запис довідника.</summary>
    public long RegistryEntryId { get; private set; }

    /// <summary>Запис-власник. Потрібна лише для вставки разом із новим записом.</summary>
    public RegistryEntry? Entry { get; private set; }

    /// <summary>Ключ довідника.</summary>
    public int RegistryKeyDefId { get; private set; }

    /// <summary>SHA-256 канонічного рядка (§4.2).</summary>
    public byte[] KeyHash { get; private set; } = [];

    /// <summary>Людський вигляд ключа («1D-2 · 370 Winter»); у порівнянні не бере участі.</summary>
    public string KeyText { get; private set; } = null!;

    /// <summary>
    /// Перший чинний день запису; для «від початку» — <see cref="DateOnly.MinValue"/>,
    /// а не <c>null</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Не <c>null</c> навмисно: колонка входить в унікальний індекс, а
    /// SQL Server вважає два <c>NULL</c> рівними. Сентинел дає нетемпоральному
    /// довіднику ПОВНУ унікальність, а темпоральному — точний збіг початку
    /// вікна; перетин вікон перевіряє служба ключів (§4.4).
    /// </remarks>
    public DateOnly ValidFromKey { get; private set; }

    /// <summary>Перший НЕчинний день запису (виключна межа); <c>null</c> — без обмеження.</summary>
    public DateOnly? ValidTo { get; private set; }

    /// <summary>Чи бере рядок участь в унікальності: запис не видалений.</summary>
    public bool IsLive { get; private set; }

    /// <summary>Вікно чинності запису на момент останнього перерахунку.</summary>
    public ValidityWindow Window
        => new(ValidFromKey == DateOnly.MinValue ? null : ValidFromKey, ValidTo);

    /// <summary>Перераховує рядок після зміни значень, вікна чи видалення запису.</summary>
    /// <param name="entry">Той самий запис, що й при створенні.</param>
    /// <param name="keyHash">Новий хеш.</param>
    /// <param name="keyText">Новий людський вигляд.</param>
    /// <exception cref="ArgumentException">Інший запис, хеш не тієї довжини або порожній текст.</exception>
    public void Recompute(RegistryEntry entry, byte[] keyHash, string keyText)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.IsPersisted && RegistryEntryId != 0 && entry.Id != RegistryEntryId)
        {
            throw new ArgumentException(
                $"Рядок ключа належить запису {RegistryEntryId}, а не {entry.Id}.", nameof(entry));
        }

        Apply(entry, keyHash, keyText);
    }

    /// <summary>Тимчасово виводить рядок з унікальності.</summary>
    /// <remarks>
    /// Перша фаза обміну ключами в одному пакеті (§4.3): SQL Server перевіряє
    /// унікальний індекс на кожну інструкцію, тож A: k1→k2 і B: k2→k1 одним
    /// проходом порушили б його на півдорозі. Друга фаза —
    /// <see cref="Recompute"/>, який повертає живість із запису.
    /// </remarks>
    public void Retire() => IsLive = false;

    private void Apply(RegistryEntry entry, byte[] keyHash, string keyText)
    {
        ArgumentNullException.ThrowIfNull(keyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyText);

        if (keyHash.Length != KeyHashLength)
        {
            throw new ArgumentException(
                $"Хеш ключа має {keyHash.Length} байт, а SHA-256 — {KeyHashLength}.", nameof(keyHash));
        }

        KeyHash = keyHash.ToArray();

        // ⚠ Обрізання, а не відмова: текст лише для повідомлень, унікальність
        // тримає хеш. Відмова тут перетворила б довгий, але цілком законний
        // ключ на 500 при збереженні запису.
        KeyText = keyText.Length <= MaxKeyTextLength
            ? keyText
            : string.Concat(keyText.AsSpan(0, MaxKeyTextLength - 1), "…");

        ValidFromKey = entry.ValidFrom ?? DateOnly.MinValue;
        ValidTo = entry.ValidTo;
        IsLive = !entry.IsDeleted;
    }
}
