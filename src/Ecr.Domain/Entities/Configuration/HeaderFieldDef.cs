using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Поле шапки документа: адмін-визначений набір полів рівня всього документа
/// (не рядка, не таблиці — рівно один запис на документ, за зразком
/// <c>ColumnDef</c>/<c>RegistryFieldDef</c>). Належить <see cref="TemplateVersion"/>,
/// а не окремій таблиці: шапка — властивість документа цілком.
/// </summary>
/// <remarks>
/// ⛔ Конкретні поля (наприклад, «Area», «Contractor», «Region») тут НЕ
/// хардкодяться — адмін визначає довільний набір через
/// <c>PUT …/header-fields/{code}</c>, так само, як <c>ColumnDef</c> для
/// таблиці. Уже наявний <c>Document.BusinessKey</c> — окремий, раніше
/// заведений еквівалент «File Number»; полем шапки не дублюється.
/// </remarks>
public sealed class HeaderFieldDef : Entity<int>
{
    private HeaderFieldDef() { }

    public HeaderFieldDef(int templateVersionId, EcrCode code, LocalizedText label, int ordinal, CellDataType dataType)
    {
        if (dataType is CellDataType.Formula or CellDataType.Calculated)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Поле шапки документа не може мати тип {dataType}: шапка зберігає введене " +
                "значення, а не обчислює його.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.headerFieldTypeNotAllowed",
                    ["dataType"] = dataType.ToString(),
                });
        }

        TemplateVersionId = templateVersionId;
        Code = code.Value;
        LabelL10n = label;
        Ordinal = ordinal;
        DataType = dataType;
    }

    public int TemplateVersionId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText LabelL10n { get; private set; } = null!;
    public int Ordinal { get; private set; }
    public CellDataType DataType { get; private set; }
    public bool IsRequired { get; private set; }

    /// <summary>Довідник для типу <see cref="CellDataType.Lookup"/>.</summary>
    public int? LookupRegistryDefId { get; private set; }

    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    /// <summary>Змінює підпис поля мовами каталогу.</summary>
    public void Relabel(LocalizedText label)
    {
        ArgumentNullException.ThrowIfNull(label);
        LabelL10n = label;
    }

    /// <summary>Позиція поля в шапці.</summary>
    public void Reorder(int ordinal) => Ordinal = ordinal;

    /// <summary>Обов'язковість заповнення.</summary>
    public void SetRequired(bool required) => IsRequired = required;

    /// <summary>Прив'язує поле до довідника.</summary>
    /// <exception cref="DomainException">Тип поля не <see cref="CellDataType.Lookup"/>.</exception>
    public void SetLookup(int registryDefId)
    {
        if (DataType != CellDataType.Lookup)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Довідник можна прив'язати лише до поля шапки типу Lookup; у {Code} тип {DataType}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.headerFieldLookupRequiresLookupType",
                    ["headerFieldCode"] = Code,
                    ["dataType"] = DataType.ToString(),
                });
        }

        LookupRegistryDefId = registryDefId;
    }

    /// <summary>
    /// Логічне видалення: значення документів (<c>DocumentHeaderValue</c>)
    /// можуть посилатися на поле навіть у чернетці — той самий підхід, що й
    /// <see cref="ColumnDef.SoftDelete"/>.
    /// </summary>
    public void SoftDelete(int userId, DateTime utcNow)
    {
        IsDeleted = true;
        DeletedAt = utcNow;
        DeletedByUserId = userId;
    }

    /// <summary>Перевіряє, чи значення сумісне з типом і обов'язковістю поля.</summary>
    /// <returns>Код помилки з каталогу або <c>null</c>, якщо все гаразд.</returns>
    public string? ValidateValue(DocumentHeaderValueData value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!value.IsWellFormed())
        {
            return "ECR-HDR-0422";
        }

        if (value.IsEmpty)
        {
            return IsRequired ? "ECR-HDR-0422" : null;
        }

        var matchesType = DataType switch
        {
            CellDataType.String => value.ValueString is not null,
            CellDataType.Int => value.ValueNumeric is not null,
            CellDataType.Decimal => value.ValueNumeric is not null,
            CellDataType.Bool => value.ValueBool is not null,
            CellDataType.Date => value.ValueDate is not null,
            CellDataType.Lookup => value.ValueRegistryEntryId is not null,
            CellDataType.Unit => value.ValueUnitId is not null,
            _ => false,
        };

        if (!matchesType)
        {
            return "ECR-HDR-0422";
        }

        if (value.ValueString is { } text && text.Length > ColumnDef.MaxStringLength)
        {
            return "ECR-HDR-0422";
        }

        if (DataType == CellDataType.Int && value.ValueNumeric is { } asInt
            && decimal.Truncate(asInt) != asInt)
        {
            return "ECR-HDR-0422";
        }

        return null;
    }
}
