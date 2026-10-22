// src/Ecr.Application/Permits/Groups/PermitGroupDtos.cs
namespace Ecr.Application.Permits.Groups;

/// <summary>Тип дозволу у складі групи.</summary>
/// <param name="Code">Код типу.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="Category"><c>Air</c>, <c>Water</c>, <c>Waste</c> або <c>Other</c>.</param>
/// <param name="HeaderRegistryCode">Код реєстру шапки типу (<c>PRM_&lt;TYPE&gt;</c>).</param>
public sealed record PermitGroupTypeDto(
    string Code,
    Dictionary<string, string> NameL10n,
    string Category,
    string HeaderRegistryCode);

/// <summary>Група типів дозволів: назва, опис, склад, використання.</summary>
/// <param name="Code">Код групи; не змінюється.</param>
/// <param name="NameL10n">Назва мовами каталогу (en/ru/kz).</param>
/// <param name="DescriptionL10n">Опис мовами каталогу; показується під час вибору цілі Lookup; порожній словник - без опису.</param>
/// <param name="IsActive">Чи групу можна обрати новою ціллю.</param>
/// <param name="RegistryCode">Код прихованого реєстру-«ідентичності» групи (<c>PRMG_&lt;КОД&gt;</c>) - ціль Lookup колонки/поля.</param>
/// <param name="RegistryDefId">Його ідентифікатор (так ціль зберігає колонка).</param>
/// <param name="Types">Типи складу в порядку показу.</param>
/// <param name="UsedInColumns">Скільки колонок/полів шаблонів мають групу ціллю.</param>
/// <param name="UsedInTemplates">У скількох шаблонах.</param>
/// <param name="RowVersion">Токен оптимістичної конкуренції (base64); повертається в наступному запиті зміни.</param>
/// <param name="UpdatedAt">Остання зміна, UTC.</param>
public sealed record PermitGroupDto(
    string Code,
    Dictionary<string, string> NameL10n,
    Dictionary<string, string> DescriptionL10n,
    bool IsActive,
    string RegistryCode,
    int RegistryDefId,
    IReadOnlyList<PermitGroupTypeDto> Types,
    int UsedInColumns,
    int UsedInTemplates,
    string RowVersion,
    DateTime UpdatedAt);

/// <summary>Створення групи.</summary>
/// <param name="Code">Код групи (не довше 59 символів: реєстр групи <c>PRMG_</c> + код вміщається в 64).</param>
/// <param name="NameL10n">Назва; хоча б одна мова непорожня.</param>
/// <param name="DescriptionL10n">Опис (до 1000 символів на мову); <c>null</c> - без опису.</param>
/// <param name="TypeCodes">Початковий склад (опубліковані типи, без повторів); <c>null</c> - порожня група.</param>
public sealed record CreatePermitGroupRequest(
    string Code,
    Dictionary<string, string> NameL10n,
    Dictionary<string, string>? DescriptionL10n,
    IReadOnlyList<string>? TypeCodes);

/// <summary>Зміна назви, опису й активності групи (деактивація - <c>IsActive = false</c>).</summary>
/// <param name="NameL10n">Назва.</param>
/// <param name="DescriptionL10n">Опис; <c>null</c> - прибрати.</param>
/// <param name="IsActive">Активність.</param>
/// <param name="RowVersion">Токен, прочитаний разом із групою.</param>
public sealed record UpdatePermitGroupRequest(
    Dictionary<string, string> NameL10n,
    Dictionary<string, string>? DescriptionL10n,
    bool IsActive,
    string RowVersion);

/// <summary>Заміна складу групи цілком.</summary>
/// <param name="TypeCodes">Коди опублікованих типів у порядку показу, без повторів; порожній список - склад очищено.</param>
/// <param name="RowVersion">Токен, прочитаний разом із групою.</param>
public sealed record ReplacePermitGroupTypesRequest(
    IReadOnlyList<string> TypeCodes,
    string RowVersion);
