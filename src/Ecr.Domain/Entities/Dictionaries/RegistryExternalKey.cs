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
    public void MarkSynced(string? externalPath, DateTime utcNow)
    {
        if (externalPath is not null)
        {
            ExternalPath = externalPath;
        }

        LastSyncedAt = utcNow;
    }
}
