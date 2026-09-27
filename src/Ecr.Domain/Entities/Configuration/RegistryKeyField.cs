// src/Ecr.Domain/Entities/Configuration/RegistryKeyField.cs
namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Частина складеного ключа: поле довідника на позиції <see cref="Ordinal"/>.
/// </summary>
/// <remarks>
/// Без сурогатного ключа: пара <c>(RegistryKeyDefId, Ordinal)</c> і є
/// ідентичністю. Створюється лише разом із ключем
/// (<see cref="RegistryKeyDef"/>) і після цього не змінюється.
/// </remarks>
public sealed class RegistryKeyField
{
    private RegistryKeyField() { }

    /// <summary>Створює частину ключа.</summary>
    /// <param name="ordinal">Позиція від 1; порядок аргументів <c>REGFIND</c>.</param>
    /// <param name="registryFieldDefId">Поле довідника.</param>
    internal RegistryKeyField(byte ordinal, int registryFieldDefId)
    {
        Ordinal = ordinal;
        RegistryFieldDefId = registryFieldDefId;
    }

    /// <summary>Ключ, якому належить частина.</summary>
    public int RegistryKeyDefId { get; private set; }

    /// <summary>Позиція частини від 1.</summary>
    public byte Ordinal { get; private set; }

    /// <summary>Поле довідника.</summary>
    public int RegistryFieldDefId { get; private set; }
}
