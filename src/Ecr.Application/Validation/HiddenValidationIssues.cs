using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Validation;

/// <summary>
/// Зауваження валідації, яких читач не бачить через заборону (S6, ФВ-6.6), —
/// одним знеособленим повідомленням замість мовчання.
/// </summary>
/// <remarks>
/// ⛔ Що було після S6: повідомлення про приховані таблиці й колонки просто
/// відкидалися. Користувач, чиї ЄДИНІ зауваження лежать під забороною, бачив
/// «зауважень немає» — а «Подати» відмовляло, бо подання рахує аркуш цілком.
/// Зелений напис під документом, який подати не можна, — неправда про
/// готовність (той самий клас, що й `A7-04`).
///
/// ⚠ Знеособлене — ЦІЛКОМ: без числа прихованих, без таблиці, рядка, колонки й
/// тексту правила. Кожна з цих подробиць — зміст прихованого, і саме його
/// заборона й закриває. Лише код <see cref="ErrorCodes.SubmitBlocked"/> і
/// ключ каталогу <see cref="MessageKey"/>.
///
/// ⚠ Лише для <c>Error</c>: подання блокує саме він. Приховані попередження
/// нічого не блокують — їх відкидає фільтр, як і раніше.
/// </remarks>
public static class HiddenValidationIssues
{
    /// <summary>Ключ каталогу (<c>sys_ecr.UiString</c>) знеособленого зауваження.</summary>
    public const string MessageKey = "err.ECR-SUB-4221.hiddenIssues";

    /// <summary>
    /// Запасний текст — той самий, що рядок <c>en</c> у <c>09-seed.sql</c>
    /// (каталог рядків наразі лише англійський).
    /// </summary>
    public const string FallbackText =
        "There are issues outside your visibility — submission is blocked. Contact the project owner.";

    /// <summary>Знеособлене зауваження: ні таблиці, ні рядка, ні колонки.</summary>
    public static ValidationMessage Placeholder { get; } = new(
        ValidationSeverity.Error,
        ErrorCodes.SubmitBlocked,
        FallbackText,
        TableDefId: 0,
        RowKey: null,
        ColumnCode: null,
        BlocksSave: false);

    /// <summary>
    /// Чи бачить читач повідомлення: місце повідомлення І, для зв'язку Check, місце джерела —
    /// текст містить значення обох сторін (T1-01).
    /// </summary>
    /// <param name="scope">Межі читання в періоді.</param>
    /// <param name="message">Повідомлення.</param>
    public static bool CanSee(Security.DocumentReadScope scope, ValidationMessage message)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(message);

        // D-PS: порожнє обов'язкове поле шапки — шапка не має видимості за таблицею/колонкою, текст несе лише коди.
        if (RequiredHeaderCheck.IsHeaderMessage(message))
        {
            return true;
        }

        return scope.CanReadAt(message.TableDefId, message.ColumnCode)
               && (message.SourceTableDefId is not { } source || scope.CanReadAt(source, message.SourceColumnCode));
    }

    /// <summary>Чи це знеособлене зауваження, а не повідомлення правила.</summary>
    /// <param name="message">Повідомлення.</param>
    public static bool IsPlaceholder(ValidationMessage message)
        => ReferenceEquals(message, Placeholder)
           || (message.TableDefId == 0 && message.RuleCode == ErrorCodes.SubmitBlocked);

    /// <summary>
    /// Видимі читачеві повідомлення; якщо бодай одна блокувальна помилка
    /// прихована — плюс рівно одне знеособлене зауваження в кінці.
    /// </summary>
    /// <param name="messages">Усі повідомлення.</param>
    /// <param name="canSee">Чи бачить читач місце повідомлення.</param>
    public static List<ValidationMessage> ForViewer(
        IEnumerable<ValidationMessage> messages, Func<ValidationMessage, bool> canSee)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(canSee);

        var visible = new List<ValidationMessage>();
        var hiddenError = false;

        foreach (var message in messages)
        {
            if (canSee(message))
            {
                visible.Add(message);
            }
            else if (message.Severity == ValidationSeverity.Error)
            {
                hiddenError = true;
            }
        }

        if (hiddenError)
        {
            visible.Add(Placeholder);
        }

        return visible;
    }
}
