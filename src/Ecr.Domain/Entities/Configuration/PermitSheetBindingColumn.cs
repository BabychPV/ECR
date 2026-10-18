// src/Ecr.Domain/Entities/Configuration/PermitSheetBindingColumn.cs
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Колонка аркуша в прив'язці до дозволу: яке поле дозволу вона несе й у якій ролі
/// (PM-02). Без сурогатного ключа: пара <c>(BindingId, PermitFieldCode)</c> і є
/// ідентичністю.
/// </summary>
/// <remarks>
/// Створюється лише разом із прив'язкою (<see cref="PermitSheetBinding.AddColumn"/>);
/// єдиний зв'язок із каскадним видаленням у схемі — від прив'язки до її колонок.
/// </remarks>
public sealed class PermitSheetBindingColumn
{
    private PermitSheetBindingColumn() { }

    /// <summary>Створює колонку прив'язки.</summary>
    /// <param name="role">Роль колонки.</param>
    /// <param name="permitFieldCode">Код поля дозволу (поля таблиці типу).</param>
    /// <param name="sheetColumnDefId">Колонка аркуша.</param>
    /// <param name="ordinal">Позиція в прив'язці.</param>
    public PermitSheetBindingColumn(PermitColumnRole role, EcrCode permitFieldCode, int sheetColumnDefId, int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sheetColumnDefId);

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Невідома роль колонки.");
        }

        Role = role;
        PermitFieldCode = permitFieldCode.Value;
        SheetColumnDefId = sheetColumnDefId;
        Ordinal = ordinal;
    }

    /// <summary>Прив'язка, якій належить колонка.</summary>
    public int BindingId { get; private set; }

    /// <summary>Роль колонки.</summary>
    public PermitColumnRole Role { get; private set; }

    /// <summary>Код поля дозволу.</summary>
    public string PermitFieldCode { get; private set; } = null!;

    /// <summary>Колонка аркуша.</summary>
    public int SheetColumnDefId { get; private set; }

    /// <summary>Позиція в прив'язці.</summary>
    public int Ordinal { get; private set; }
}
