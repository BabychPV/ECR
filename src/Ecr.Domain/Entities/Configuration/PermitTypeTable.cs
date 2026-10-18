// src/Ecr.Domain/Entities/Configuration/PermitTypeTable.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Таблиця типу дозволу: рядок упорядкованого складу типу, що вказує на реєстр,
/// який її зберігає (PM-02).
/// </summary>
/// <remarks>
/// Один реєстр може бути таблицею лише одного типу (<c>UQ_PermitTypeTable_Registry</c>):
/// інакше правка складу одного типу мовчки змінила б інший. Код унікальний у межах типу.
/// </remarks>
public sealed class PermitTypeTable : Entity<int>
{
    private PermitTypeTable() { }

    /// <summary>Створює таблицю типу дозволу.</summary>
    /// <param name="permitTypeDefId">Тип дозволу.</param>
    /// <param name="code">Код таблиці в межах типу; не змінюється.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="ordinal">Позиція в складі типу.</param>
    /// <param name="registryDefId">Реєстр, у якому лежать рядки таблиці.</param>
    public PermitTypeTable(int permitTypeDefId, EcrCode code, LocalizedText name, int ordinal, int registryDefId)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitTypeDefId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registryDefId);

        PermitTypeDefId = permitTypeDefId;
        Code = code.Value;
        NameL10n = name;
        Ordinal = ordinal;
        RegistryDefId = registryDefId;
    }

    /// <summary>Тип дозволу.</summary>
    public int PermitTypeDefId { get; private set; }

    /// <summary>Код таблиці в межах типу.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Назва мовами каталогу.</summary>
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Позиція в складі типу.</summary>
    public int Ordinal { get; private set; }

    /// <summary>Реєстр, у якому лежать рядки таблиці.</summary>
    public int RegistryDefId { get; private set; }

    /// <summary>Змінює назву й позицію; код і реєстр лишаються.</summary>
    public void Update(LocalizedText name, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(name);

        NameL10n = name;
        Ordinal = ordinal;
    }
}
