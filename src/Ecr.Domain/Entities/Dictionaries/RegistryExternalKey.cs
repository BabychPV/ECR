// src/Ecr.Domain/Entities/Dictionaries/RegistryExternalKey.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Dictionaries;

/// <summary>
/// Зовнішній ідентифікатор запису довідника (ФВ-8.10): GUID-и, розкидані
/// сьогодні по клітинках конфігурації, стають іменованим полем.
/// </summary>
/// <remarks>
/// ⚠ Зіставлення при міграції йде **за бізнес-ключем**, а GUID зберігається
/// лише як наслідок (ФВ-11.6). Зворотний порядок — зіставити за GUID —
/// виглядає надійніше, але прив'язує нашу історію до ідентифікаторів системи,
/// яку ми виводимо з експлуатації.
/// <para>
/// Система-джерело позначена <see cref="DataSourceId"/>, а не текстовим кодом,
/// як було в скелеті: у схемі це число (`dic.RegistryExternalKey`), і воно
/// входить у ключ унікальності разом із <see cref="ExternalId"/>. Це
/// `dic`-частина `Q-027`.
/// </para>
/// </remarks>
public sealed class RegistryExternalKey : Entity<long>
{
    private RegistryExternalKey() { }

    /// <summary>Створює зовнішній ключ.</summary>
    /// <param name="registryEntryId">Запис довідника.</param>
    /// <param name="dataSourceId">Джерело: <c>ext.DataSource</c>.</param>
    /// <param name="externalId">Ідентифікатор у джерелі.</param>
    public RegistryExternalKey(long registryEntryId, int dataSourceId, string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(externalId.Length, MaxExternalIdLength, nameof(externalId));

        RegistryEntryId = registryEntryId;
        DataSourceId = dataSourceId;
        ExternalId = externalId;
    }

    public long RegistryEntryId { get; private set; }

    /// <summary>Яка зовнішня система: рядок <c>ext.DataSource</c>.</summary>
    public int DataSourceId { get; private set; }

    public string ExternalId { get; private set; } = null!;

    /// <summary>Шлях в ієрархії джерела: <c>\Server\Db\Element</c> для PI AF.</summary>
    /// <remarks>
    /// Зберігається окремо від <see cref="ExternalId"/> тому, що змінюється
    /// незалежно: елемент AF можна перенести в іншу гілку, не змінивши GUID.
    /// </remarks>
    public string? ExternalPath { get; private set; }

    /// <summary>Коли востаннє зіставлено з джерелом.</summary>
    public DateTime? LastSyncedAt { get; private set; }

    /// <summary>Фіксує успішну синхронізацію.</summary>
    /// <param name="externalPath">Актуальний шлях; <c>null</c> — не змінювати.</param>
    /// <param name="utcNow">Час синхронізації в UTC.</param>
    /// <exception cref="ArgumentOutOfRangeException">Шлях довший за <see cref="MaxExternalPathLength"/>.</exception>
    public void MarkSynced(string? externalPath, DateTime utcNow)
    {
        if (externalPath is not null)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(externalPath.Length, MaxExternalPathLength, nameof(externalPath));
            ExternalPath = externalPath;
        }

        LastSyncedAt = utcNow;
    }

    /// <summary>Стеля зовнішнього ідентифікатора — ширина колонки <c>ExternalId</c>.</summary>
    public const int MaxExternalIdLength = 200;

    /// <summary>Стеля шляху в джерелі — ширина колонки <c>ExternalPath</c>.</summary>
    /// <remarks>
    /// Аудит 2026-10-03 (L4-03): задовгий шлях елемента AF доїжджав до <c>SaveChanges</c> і валив
    /// прогін синку. Синк відсікає його ще в знімку (шлях не оновлюється, відмова в журналі).
    /// </remarks>
    public const int MaxExternalPathLength = 400;

    /// <summary>
    /// З якого моменту (UTC) елемента немає в джерелі; <c>null</c> — є або не
    /// перевірялося (<c>D-212</c>).
    /// </summary>
    public DateTime? MissingInSourceSince { get; private set; }

    /// <summary>Фіксує, що синк не знайшов елемент у джерелі.</summary>
    /// <param name="utcNow">Час прогону синку в UTC.</param>
    /// <remarks>
    /// ⚠ Повторний виклик НЕ зсуває момент: поле відповідає на «відколи», і
    /// кожен нічний прогін, що переписує його на «сьогодні», стер би саме ту
    /// відповідь, заради якої воно є.
    /// </remarks>
    public void MarkMissing(DateTime utcNow) => MissingInSourceSince ??= utcNow;

    /// <summary>Знімає позначку зникнення: елемент знову знайдено в джерелі.</summary>
    public void ClearMissing() => MissingInSourceSince = null;

    /// <summary>
    /// Перев'язує запис на інший елемент джерела (елемент AF перестворено з
    /// новим GUID). Знімає позначку зникнення.
    /// </summary>
    /// <param name="newExternalId">Новий ідентифікатор у тому самому джерелі.</param>
    /// <exception cref="ArgumentException">Порожній або довший за <see cref="MaxExternalIdLength"/>.</exception>
    public void Relink(string newExternalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newExternalId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(newExternalId.Length, MaxExternalIdLength, nameof(newExternalId));

        ExternalId = newExternalId;
        MissingInSourceSince = null;
    }
}
