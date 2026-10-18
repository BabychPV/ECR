// src/Ecr.Domain/Enums/PermitEnums.cs
namespace Ecr.Domain.Enums;

/// <summary>
/// Категорія дозволу (<c>cfg.PermitTypeDef.Category</c>, PM-02).
/// </summary>
/// <remarks>
/// ⛔ Числа — значення колонки <c>tinyint</c> і CHECK-обмежень; змінювати й переставляти не можна.
/// </remarks>
public enum PermitCategory : byte
{
    /// <summary>Викиди в атмосферне повітря.</summary>
    Air = 0,

    /// <summary>Водокористування і скиди.</summary>
    Water = 1,

    /// <summary>Відходи.</summary>
    Waste = 2,

    /// <summary>Інше.</summary>
    Other = 3,
}

/// <summary>
/// Стан типу дозволу (<c>cfg.PermitTypeDef.Status</c>): лише <c>Draft → Published</c>, назад не повертається.
/// </summary>
/// <remarks>
/// ⛔ Числа — значення колонки <c>tinyint</c> і CHECK-обмежень; змінювати й переставляти не можна.
/// </remarks>
public enum PermitTypeStatus : byte
{
    /// <summary>Чернетка: склад типу можна правити.</summary>
    Draft = 0,

    /// <summary>Опубліковано: склад типу заморожено.</summary>
    Published = 1,
}

/// <summary>
/// Звідки береться перелік значень поля дозволу (<c>cfg.PermitFieldMeta.ListScope</c>).
/// </summary>
/// <remarks>
/// ⛔ Числа — значення колонки <c>tinyint</c> і CHECK-обмежень; змінювати й переставляти не можна.
/// </remarks>
public enum PermitListScope : byte
{
    /// <summary>Спільний довідник системи (<c>ListRegistryDefId</c>).</summary>
    SharedRegistry = 0,

    /// <summary>Довідник, що існує лише в межах цього типу дозволу (<c>ListRegistryDefId</c>).</summary>
    TypeOnly = 1,

    /// <summary>Значення беруться з рядків сестринської таблиці того самого дозволу (<c>ListSiblingTableCode</c>).</summary>
    SamePermitTable = 2,
}

/// <summary>
/// Режим прив'язки аркуша до таблиці дозволу (<c>cfg.PermitSheetBinding.Mode</c>).
/// </summary>
/// <remarks>
/// ⛔ Числа — значення колонки <c>tinyint</c> і CHECK-обмежень; змінювати й переставляти не можна.
/// </remarks>
public enum PermitBindingMode : byte
{
    /// <summary>Рядки аркуша породжуються рядками таблиці дозволу.</summary>
    Rows = 0,

    /// <summary>Аркуш містить формули над таблицею дозволу.</summary>
    Formulas = 1,
}

/// <summary>
/// Як обирається дозвіл для аркуша (<c>cfg.PermitSheetBinding.SelectionRule</c>).
/// </summary>
/// <remarks>
/// ⛔ Числа — значення колонки <c>tinyint</c> і CHECK-обмежень; змінювати й переставляти не можна.
/// </remarks>
public enum PermitSelectionRule : byte
{
    /// <summary>Дозвіл, чинний на кінець періоду для об'єкта документа.</summary>
    ByFacilityPeriodEnd = 0,

    /// <summary>Дозвіл, обраний у полі шапки документа (<c>HeaderFieldDefId</c>).</summary>
    FromHeaderLookup = 1,
}

/// <summary>
/// Роль колонки аркуша в прив'язці (<c>cfg.PermitSheetBindingColumn.Role</c>).
/// </summary>
/// <remarks>
/// ⛔ Числа — значення колонки <c>tinyint</c> і CHECK-обмежень; змінювати й переставляти не можна.
/// </remarks>
public enum PermitColumnRole : byte
{
    /// <summary>Ключ рядка.</summary>
    RowKey = 0,

    /// <summary>Ліміт дозволу.</summary>
    Limit = 1,

    /// <summary>Довідкова колонка.</summary>
    Info = 2,
}
