// src/Ecr.Application/Permits/Bindings/PermitBindingErrors.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Permits.Bindings;

/// <summary>Відмови прив'язок аркуша до дозволів (<c>ECR-PRM-*</c>, Permits PM-09).</summary>
/// <remarks>
/// ⚠ Ключ каталогу - повним літералом у точці створення винятку: сторож <c>ErrorTitleCatalogTests</c> звіряє з сідом
/// саме літерали.
/// </remarks>
internal static class PermitBindingErrors
{
    /// <summary><c>404 ECR-PRM-0404 binding</c>: прив'язки з таким кодом у версії немає.</summary>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="templateVersionId">Версія шаблону.</param>
    internal static NotFoundException NotFound(string code, int templateVersionId)
        => new(
            ErrorCodes.PermitTypeNotFound,
            $"Прив'язки «{code}» у версії {templateVersionId} немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0404.binding",
                ["bindingCode"] = code,
                ["versionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary><c>422 ECR-PRM-0422 binding…</c>: прив'язка не складається; випадки - у <c>caseKeys</c>.</summary>
    /// <param name="errors">Помилки перевірки (не порожні).</param>
    internal static BusinessRuleException Invalid(IReadOnlyList<PermitBindingIssue> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentOutOfRangeException.ThrowIfZero(errors.Count);

        // messageKey першої помилки веде локалізований текст; усі випадки перелічені для клієнта в caseKeys.
        var details = new Dictionary<string, object?>
        {
            ["messageKey"] = errors[0].MessageKey,
            ["caseKeys"] = string.Join(',', errors.Select(e => e.Case)),
        };
        foreach (var arg in errors[0].Args)
        {
            details[arg.Key] = arg.Value;
        }

        return new BusinessRuleException(
            ErrorCodes.PermitTypeInvalid, string.Join(' ', errors.Select(e => e.Text)), details);
    }

    /// <summary><c>422 ECR-PRM-0422 bindingTypeUnknown</c>: типу дозволу з таким кодом немає.</summary>
    /// <param name="typeCode">Код типу з запиту.</param>
    internal static BusinessRuleException TypeUnknown(string typeCode)
        => new(
            ErrorCodes.PermitTypeInvalid,
            $"Типу дозволу «{typeCode}» не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0422.bindingTypeUnknown",
                ["typeCode"] = typeCode,
            });

    /// <summary><c>422 ECR-PRM-0422 bindingPermitTableUnknown</c>: у типу немає такої таблиці.</summary>
    /// <param name="typeCode">Код типу.</param>
    /// <param name="tableCode">Код таблиці з запиту.</param>
    internal static BusinessRuleException PermitTableUnknown(string typeCode, string tableCode)
        => new(
            ErrorCodes.PermitTypeInvalid,
            $"У типу дозволу «{typeCode}» немає таблиці «{tableCode}».",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0422.bindingPermitTableUnknown",
                ["typeCode"] = typeCode,
                ["tableCode"] = tableCode,
            });

    /// <summary><c>422 ECR-PRM-0422 bindingEnumUnknown</c>: невідоме значення режиму, правила чи ролі.</summary>
    /// <param name="field">Поле запиту.</param>
    /// <param name="value">Значення.</param>
    internal static BusinessRuleException EnumUnknown(string field, string? value)
        => new(
            ErrorCodes.PermitTypeInvalid,
            $"Значення «{value}» поля {field} невідоме.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0422.bindingEnumUnknown",
                ["field"] = field,
                ["value"] = value ?? string.Empty,
            });

    /// <summary><c>422 ECR-PRM-0422 bindingIdentityChanged</c>: наявну прив'язку не можна перенацілити.</summary>
    /// <param name="code">Код прив'язки.</param>
    internal static BusinessRuleException IdentityChanged(string code)
        => new(
            ErrorCodes.PermitTypeInvalid,
            $"Прив'язку «{code}» не можна перенацілити на іншу таблицю чи тип: видаліть її й створіть нову.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0422.bindingIdentityChanged",
                ["bindingCode"] = code,
            });

    /// <summary><c>422 ECR-PRM-0422 bindingPreviewPeriodRequired</c>: перегляд з документа без періоду.</summary>
    internal static BusinessRuleException PreviewPeriodRequired()
        => new(
            ErrorCodes.PermitTypeInvalid,
            "Перегляд з документа потребує періоду (periodKey).",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PRM-0422.bindingPreviewPeriodRequired" });

    /// <summary><c>422 ECR-PRM-0422 bindingPreviewPeriodUnknown</c>: у проєкті документа немає такого періоду.</summary>
    /// <param name="periodKey">Період із запиту.</param>
    internal static BusinessRuleException PreviewPeriodUnknown(int periodKey)
        => new(
            ErrorCodes.PermitTypeInvalid,
            $"У проєкті документа немає періоду {periodKey}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0422.bindingPreviewPeriodUnknown",
                ["periodKey"] = periodKey.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary><c>409 ECR-PRM-4101 sheet</c>: таблицю аркуша вже зайняла інша прив'язка версії.</summary>
    /// <param name="sheetTableDefId">Таблиця аркуша.</param>
    /// <param name="takenBy">Код прив'язки, що її зайняла.</param>
    internal static BusinessRuleException SheetTaken(int sheetTableDefId, string takenBy)
        => new(
            ErrorCodes.PermitBindingDuplicate,
            $"Таблиця {sheetTableDefId} у цій версії вже прив'язана («{takenBy}»): одна таблиця аркуша - один дозвіл.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4101.sheet",
                ["tableDefId"] = sheetTableDefId.ToString(CultureInfo.InvariantCulture),
                ["bindingCode"] = takenBy,
            });

    /// <summary><c>409 ECR-PRM-4101 code</c>: код прив'язки зайнятий (код унікальний по всій системі).</summary>
    /// <param name="code">Код прив'язки.</param>
    internal static BusinessRuleException CodeTaken(string code)
        => new(
            ErrorCodes.PermitBindingDuplicate,
            $"Код прив'язки «{code}» уже зайнятий (код унікальний по всій системі).",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4101.code",
                ["bindingCode"] = code,
            });

    /// <summary><c>409 ECR-PRM-4102 rowMode</c>: таблицю з прив'язкою «рядки» не можна зробити <c>Fixed</c>.</summary>
    /// <param name="tableCode">Код таблиці аркуша.</param>
    /// <param name="bindingCode">Код прив'язки, що її тримає.</param>
    internal static BusinessRuleException SheetRowModeHeld(string tableCode, string bindingCode)
        => new(
            ErrorCodes.PermitBindingBlocksSheetChange,
            $"Таблиця «{tableCode}» прив'язана до рядків дозволу («{bindingCode}»): фіксовані рядки їх не приймають. Спершу видаліть прив'язку.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4102.rowMode",
                ["tableCode"] = tableCode,
                ["bindingCode"] = bindingCode,
            });

    /// <summary><c>409 ECR-PRM-4102 table</c>: таблицю з прив'язкою не можна видалити.</summary>
    /// <param name="tableCode">Код таблиці аркуша.</param>
    /// <param name="bindingCode">Код прив'язки, що її тримає.</param>
    internal static BusinessRuleException SheetTableHeld(string tableCode, string bindingCode)
        => new(
            ErrorCodes.PermitBindingBlocksSheetChange,
            $"Таблиця «{tableCode}» використовується прив'язкою до дозволу «{bindingCode}»: спершу видаліть прив'язку.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4102.table",
                ["tableCode"] = tableCode,
                ["bindingCode"] = bindingCode,
            });

    /// <summary><c>409 ECR-PRM-4102 column</c>: колонку, зіставлену прив'язкою, не можна видалити.</summary>
    /// <param name="columnCode">Код колонки аркуша.</param>
    /// <param name="bindingCode">Код прив'язки, що її зіставляє.</param>
    internal static BusinessRuleException SheetColumnHeld(string columnCode, string bindingCode)
        => new(
            ErrorCodes.PermitBindingBlocksSheetChange,
            $"Колонка «{columnCode}» зіставлена прив'язкою до дозволу «{bindingCode}»: спершу змініть або видаліть прив'язку.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4102.column",
                ["columnCode"] = columnCode,
                ["bindingCode"] = bindingCode,
            });

    /// <summary><c>409 ECR-PRM-4102 maxRows</c>: стелю рядків таблиці з прив'язкою «рядки» не можна знизити.</summary>
    /// <param name="tableCode">Код таблиці аркуша.</param>
    /// <param name="bindingCode">Код прив'язки, що її тримає.</param>
    /// <param name="previousMax">Стеля до правки; <c>null</c> - без стелі.</param>
    /// <param name="newMax">Стеля, яку просять.</param>
    internal static BusinessRuleException SheetMaxRowsHeld(string tableCode, string bindingCode, int? previousMax, int? newMax)
        => new(
            ErrorCodes.PermitBindingBlocksSheetChange,
            $"Таблиця «{tableCode}» приймає рядки дозволу через прив'язку «{bindingCode}»: стелю рядків не можна знижувати "
            + $"(було {Describe(previousMax)}, просять {Describe(newMax)}). Спершу видаліть прив'язку.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4102.maxRows",
                ["tableCode"] = tableCode,
                ["bindingCode"] = bindingCode,
                ["previousMax"] = Describe(previousMax),
                ["newMax"] = Describe(newMax),
            });

    /// <summary><c>409 ECR-PRM-4102 sheet</c>: аркуш, таблицю якого тримає прив'язка, не можна видалити.</summary>
    /// <param name="sheetCode">Код аркуша.</param>
    /// <param name="tableCode">Код прив'язаної таблиці аркуша.</param>
    /// <param name="bindingCode">Код прив'язки, що її тримає.</param>
    internal static BusinessRuleException SheetHeld(string sheetCode, string tableCode, string bindingCode)
        => new(
            ErrorCodes.PermitBindingBlocksSheetChange,
            $"Аркуш «{sheetCode}» містить таблицю «{tableCode}», прив'язану до дозволу («{bindingCode}»): спершу видаліть прив'язку.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4102.sheet",
                ["sheetCode"] = sheetCode,
                ["tableCode"] = tableCode,
                ["bindingCode"] = bindingCode,
            });

    /// <summary><c>409 ECR-SCHM-0409 permitBindingBreaking</c>: руйнівна зміна у версії з документами.</summary>
    /// <param name="code">Код прив'язки.</param>
    internal static ConcurrencyConflictException Breaking(string code)
        => new(
            ErrorCodes.BreakingChange,
            $"Видалення прив'язки «{code}» руйнівне: до цієї версії вже прив'язані документи, рядки яких створено з дозволу (ФВ-7.4).",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-SCHM-0409.permitBindingBreaking",
                ["bindingCode"] = code,
            });

    /// <summary><c>404 ECR-TMPL-0404 templateVersion</c>: версії шаблону немає.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    internal static NotFoundException VersionNotFound(int templateVersionId)
        => new(
            ErrorCodes.TemplateNotFound,
            $"Версії шаблону {templateVersionId} не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                ["versionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
            });

    private static string Describe(int? max) => max is { } m ? m.ToString(CultureInfo.InvariantCulture) : "none";
}
