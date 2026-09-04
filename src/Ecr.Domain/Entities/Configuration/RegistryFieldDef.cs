using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Поле реєстру.</summary>
public sealed class RegistryFieldDef : Entity<int>
{
    private RegistryFieldDef() { }

    public RegistryFieldDef(int registryDefId, EcrCode code, LocalizedText name, CellDataType dataType, int ordinal)
    {
        RegistryDefId = registryDefId;
        Code = code.Value;
        NameL10n = name;
        DataType = dataType;
        Ordinal = ordinal;
    }

    public int RegistryDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public CellDataType DataType { get; private set; }
    public int Ordinal { get; private set; }
    public bool IsRequired { get; private set; }

    /// <summary>Чи входить у бізнес-ключ запису.</summary>
    public bool IsKey { get; private set; }

    /// <summary>Одиниця значення поля (напр. ліміт дозволу в м³).</summary>
    public int? UnitId { get; private set; }

    /// <summary>Посилання на інший реєстр: вкладеність або M:N.</summary>
    public int? RefRegistryDefId { get; private set; }
}
