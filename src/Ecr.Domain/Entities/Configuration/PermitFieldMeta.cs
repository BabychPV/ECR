// src/Ecr.Domain/Entities/Configuration/PermitFieldMeta.cs
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Надбудова дозволу над полем реєстру: група, точність і область переліку
/// (PM-02). Ключ — саме поле реєстру, окремого сурогата немає.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <see cref="RegistryFieldDef"/> не змінюється: усе, чого полю дозволу бракує,
/// живе тут, і поле звичайного реєстру про дозвіл не знає.
/// </para>
/// <para>
/// ⚠ <see cref="ListScope"/> лише у полів-переліків; <c>null</c> — поле без переліку
/// (число, дата, рядок). Тоді обидва посилання порожні. Той самий інваріант тримає
/// <c>CK_PermitField_List</c> — для вставок повз домен.
/// </para>
/// </remarks>
public sealed class PermitFieldMeta
{
    /// <summary>Найбільша точність числа (як <c>decimal(38, s)</c>).</summary>
    public const byte MaxPrecision = 38;

    private PermitFieldMeta() { }

    /// <summary>Створює надбудову поля.</summary>
    /// <param name="registryFieldDefId">Поле реєстру.</param>
    /// <param name="permitTypeDefId">Тип дозволу, якому належить поле.</param>
    /// <param name="groupCode">Код групи для відображення; <c>null</c> — поза групою.</param>
    /// <param name="precision">Загальна кількість цифр; <c>null</c> — не число.</param>
    /// <param name="scale">Цифр після коми; не більше точності.</param>
    /// <param name="listScope">Область переліку; <c>null</c> — поле без переліку.</param>
    /// <param name="listRegistryDefId">Реєстр переліку (для <see cref="PermitListScope.SharedRegistry"/> і <see cref="PermitListScope.TypeOnly"/>).</param>
    /// <param name="listSiblingTableCode">Код сестринської таблиці (для <see cref="PermitListScope.SamePermitTable"/>).</param>
    /// <exception cref="ArgumentException">Порушено узгодженість області переліку чи точності.</exception>
    public PermitFieldMeta(
        int registryFieldDefId,
        int permitTypeDefId,
        EcrCode? groupCode,
        byte? precision,
        byte? scale,
        PermitListScope? listScope,
        int? listRegistryDefId,
        EcrCode? listSiblingTableCode)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registryFieldDefId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitTypeDefId);

        RegistryFieldDefId = registryFieldDefId;
        PermitTypeDefId = permitTypeDefId;
        GroupCode = groupCode?.Value;
        Configure(precision, scale, listScope, listRegistryDefId, listSiblingTableCode?.Value);
    }

    /// <summary>Поле реєстру; він же ключ.</summary>
    public int RegistryFieldDefId { get; private set; }

    /// <summary>Тип дозволу, якому належить поле.</summary>
    public int PermitTypeDefId { get; private set; }

    /// <summary>Код групи для відображення.</summary>
    public string? GroupCode { get; private set; }

    /// <summary>Загальна кількість цифр числа.</summary>
    public byte? Precision { get; private set; }

    /// <summary>Цифр після коми.</summary>
    public byte? Scale { get; private set; }

    /// <summary>Область переліку; <c>null</c> — поле без переліку.</summary>
    public PermitListScope? ListScope { get; private set; }

    /// <summary>Реєстр переліку (області <c>SharedRegistry</c> і <c>TypeOnly</c>).</summary>
    public int? ListRegistryDefId { get; private set; }

    /// <summary>Код сестринської таблиці (область <c>SamePermitTable</c>).</summary>
    public string? ListSiblingTableCode { get; private set; }

    /// <summary>Перевіряє, чи узгоджена область переліку з її посиланнями (правило <c>CK_PermitField_List</c>).</summary>
    /// <returns><c>true</c>, якщо комбінація допустима.</returns>
    public static bool IsListConsistent(PermitListScope? scope, int? registryDefId, string? siblingTableCode) => scope switch
    {
        null => registryDefId is null && siblingTableCode is null,
        PermitListScope.SharedRegistry or PermitListScope.TypeOnly => registryDefId is not null && siblingTableCode is null,
        PermitListScope.SamePermitTable => siblingTableCode is not null && registryDefId is null,
        _ => false,
    };

    /// <summary>Замінює групу, точність і область переліку; поле й тип лишаються.</summary>
    /// <param name="groupCode">Код групи.</param>
    /// <param name="precision">Точність.</param>
    /// <param name="scale">Масштаб.</param>
    /// <param name="listScope">Область переліку.</param>
    /// <param name="listRegistryDefId">Реєстр переліку.</param>
    /// <param name="listSiblingTableCode">Код сестринської таблиці.</param>
    /// <exception cref="ArgumentException">Порушено узгодженість області переліку чи точності.</exception>
    public void Update(
        EcrCode? groupCode,
        byte? precision,
        byte? scale,
        PermitListScope? listScope,
        int? listRegistryDefId,
        EcrCode? listSiblingTableCode)
    {
        GroupCode = groupCode?.Value;
        Configure(precision, scale, listScope, listRegistryDefId, listSiblingTableCode?.Value);
    }

    private void Configure(
        byte? precision, byte? scale, PermitListScope? listScope, int? listRegistryDefId, string? listSiblingTableCode)
    {
        if (precision is { } p && (p < 1 || p > MaxPrecision))
        {
            throw new ArgumentOutOfRangeException(nameof(precision), precision, $"Точність має бути від 1 до {MaxPrecision}.");
        }

        if (scale is { } s && (precision is null || s > precision))
        {
            throw new ArgumentException("Масштаб задається лише разом із точністю й не перевищує її.", nameof(scale));
        }

        if (listScope is { } known && !Enum.IsDefined(known))
        {
            throw new ArgumentOutOfRangeException(nameof(listScope), listScope, "Невідома область переліку.");
        }

        if (!IsListConsistent(listScope, listRegistryDefId, listSiblingTableCode))
        {
            throw new ArgumentException(
                "Область переліку не узгоджена з посиланнями: SharedRegistry і TypeOnly потребують лише реєстр, "
                + "SamePermitTable — лише код сестринської таблиці, без області обидва порожні.",
                nameof(listScope));
        }

        Precision = precision;
        Scale = scale;
        ListScope = listScope;
        ListRegistryDefId = listRegistryDefId;
        ListSiblingTableCode = listSiblingTableCode;
    }
}
