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
/// </remarks>
public sealed class RegistryExternalKey : Entity<long>
{
    private RegistryExternalKey() { }

    public RegistryExternalKey(long registryEntryId, string systemCode, string externalId)
    {
        RegistryEntryId = registryEntryId;
        SystemCode = systemCode;
        ExternalId = externalId;
    }

    public long RegistryEntryId { get; private set; }

    /// <summary>Яка зовнішня система: <c>PiAf</c>, <c>Flert</c>.</summary>
    public string SystemCode { get; private set; } = null!;

    public string ExternalId { get; private set; } = null!;
}
