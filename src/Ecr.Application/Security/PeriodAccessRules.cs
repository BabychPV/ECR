using System.Globalization;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>
/// Факти про комірку, потрібні правилам доступу до періоду (<c>ФВ-2.15</c>).
/// </summary>
/// <remarks>
/// ⛔ Усе, що потрібно правилам, зібране в один запис ЗАЗДАЛЕГІДЬ. Правило,
/// яке саме ходить у базу, перетворює перевірку прав на 30 000 запитів для
/// таблиці 500×60, а на права відведено 50 мс на весь запит (<c>ФВ-6.10</c>).
/// </remarks>
/// <param name="SheetDefId">Аркуш комірки.</param>
/// <param name="TableDefId">Таблиця комірки.</param>
/// <param name="RowKind">Вид рядка: заголовок, елемент, підсумок.</param>
/// <param name="PeriodSequence">Порядковий номер періоду комірки.</param>
/// <param name="PeriodYear">Рік періоду; потрібен, щоб місяць колонки став датою.</param>
/// <param name="CurrentSequence">
/// Поточний номер періоду проєкту; <c>null</c> — невідомий, і тоді
/// <see cref="PeriodAccessRuleKind.RelativeWindow"/> не застосовується.
/// </param>
/// <param name="ColumnMonthNumber">
/// Місяць колонки (<c>1…12</c>); <c>null</c> — колонка не місячна, і
/// <see cref="PeriodAccessRuleKind.SourceWindow"/> до неї не має стосунку.
/// </param>
/// <param name="SourceValues">
/// Вікна чинності записів довідника, на які посилається РЯДОК: код колонки → вікно.
/// Відсутній ключ означає «рядок нічого не обрав».
/// </param>
/// <param name="ExpressionResults">
/// Наперед обчислені результати умов <see cref="PeriodAccessRuleKind.Expression"/>:
/// вираз → чи виконується він для цього рядка.
/// </param>
public readonly record struct PeriodRuleFacts(
    int SheetDefId,
    int TableDefId,
    RowKind RowKind,
    byte PeriodSequence,
    int PeriodYear,
    byte? CurrentSequence,
    byte? ColumnMonthNumber,
    IReadOnlyDictionary<int, SourceValidity> SourceValues,
    IReadOnlyDictionary<string, bool> ExpressionResults);

/// <summary>Вікно чинності запису довідника, на який посилається рядок.</summary>
/// <param name="ValidFrom"><c>null</c> — без початкової межі.</param>
/// <param name="ValidTo"><c>null</c> — без кінцевої.</param>
public readonly record struct SourceValidity(DateOnly? ValidFrom, DateOnly? ValidTo);

/// <summary>Результат перевірки правил доступу до періоду.</summary>
/// <param name="Reason">Причина заборони; <see cref="EditDenyReason.None"/> — дозволено.</param>
/// <param name="Detail">Пояснення для користувача.</param>
/// <param name="Behavior">Що робити поза вікном (<c>ФВ-2.16</c>).</param>
public readonly record struct PeriodRuleOutcome(
    EditDenyReason Reason, string? Detail, OutOfWindowBehavior Behavior)
{
    /// <summary>Правила не заперечують.</summary>
    public static PeriodRuleOutcome Allowed { get; }
        = new(EditDenyReason.None, null, OutOfWindowBehavior.ReadOnly);

    /// <summary>Чи блокує результат запис.</summary>
    /// <remarks>
    /// ⚠ <c>Warn</c> і <c>AllowWithConfirmation</c> <b>не блокують</b>
    /// (<c>ФВ-2.16</c>): перше лишає правку з позначкою, друге вимагає
    /// підтвердження в інтерфейсі. Заборона тут перетворила б обидві на
    /// <c>ReadOnly</c>, і три поведінки з вимоги стали б однією.
    /// </remarks>
    public bool Blocks => Reason != EditDenyReason.None
                          && Behavior is OutOfWindowBehavior.Hide or OutOfWindowBehavior.ReadOnly;
}

/// <summary>
/// Правила доступу до періоду — заміна кнопки <c>Protect</c> (<c>ФВ-2.15</c>).
/// </summary>
/// <remarks>
/// ⛔ Шість видів правил, і жоден не кидає «не підтримується». Перелік,
/// половина якого не працює, — це та сама мертва гілка, з якою боровся весь
/// <c>A7</c>: механізм оголошений і недосяжний.
///
/// ⛔ Чиста функція, як і <see cref="EditRules"/>, і з тієї самої причини:
/// рішення про доступ найважче перевірити й найдорожче помилитися. Усі шість
/// видів проганяються за мілісекунди й без бази.
///
/// ⚠ <b>Заборона виграє</b>: якщо хоч одне правило блокує, комірка
/// заблокована. Це те саме правило, що для грантів (<c>ФВ-6.6</c>), і
/// свідома жорсткість: альтернатива «конкретніше правило перекриває
/// загальніше» дає доступ, який ніхто не може пояснити.
/// </remarks>
public static class PeriodAccessRules
{
    /// <summary>Перевіряє всі правила, що стосуються комірки.</summary>
    /// <param name="rules">Правила версії шаблону.</param>
    /// <param name="facts">Факти про комірку.</param>
    /// <param name="roleIds">Ролі користувача; правило з <c>RoleId</c> діє лише на них.</param>
    /// <returns>Перша заборона або <see cref="PeriodRuleOutcome.Allowed"/>.</returns>
    public static PeriodRuleOutcome Evaluate(
        IReadOnlyList<PeriodAccessRuleDef> rules,
        PeriodRuleFacts facts,
        IReadOnlySet<int> roleIds)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(roleIds);

        PeriodRuleOutcome? softest = null;

        foreach (var rule in rules)
        {
            if (!Targets(rule, facts, roleIds))
            {
                continue;
            }

            var outcome = Check(rule, facts);
            if (outcome.Reason == EditDenyReason.None)
            {
                continue;
            }

            // ⛔ Блокувальна заборона повертається ОДРАЗУ: далі шукати нічого,
            // а перебирати решту означало б дати м'якшому правилу шанс її
            // перекрити.
            if (outcome.Blocks)
            {
                return outcome;
            }

            // М'яка (`Warn`, `AllowWithConfirmation`) запам'ятовується, але
            // пошук триває: раптом далі є справжня заборона.
            softest ??= outcome;
        }

        return softest ?? PeriodRuleOutcome.Allowed;
    }

    /// <summary>Чи стосується правило саме цієї комірки.</summary>
    private static bool Targets(
        PeriodAccessRuleDef rule, PeriodRuleFacts facts, IReadOnlySet<int> roleIds)
    {
        if (rule.SheetDefId is { } sheet && sheet != facts.SheetDefId)
        {
            return false;
        }

        if (rule.TableDefId is { } table && table != facts.TableDefId)
        {
            return false;
        }

        if (rule.RowKind is { } rowKind && rowKind != facts.RowKind)
        {
            return false;
        }

        // ⚠ `RoleId = null` означає «для всіх ролей», а не «для нікого».
        // Зворотне прочитання зробило б кожне загальне правило мертвим.
        return rule.RoleId is not { } roleId || roleIds.Contains(roleId);
    }

    /// <summary>Застосовує правило одного виду.</summary>
    private static PeriodRuleOutcome Check(PeriodAccessRuleDef rule, PeriodRuleFacts facts)
        => rule.RuleKind switch
        {
            // Завжди лише читання: жодних умов, саме в цьому сенс.
            PeriodAccessRuleKind.AlwaysReadOnly => Deny(
                rule, EditDenyReason.BusinessRule, "Правило доступу: лише читання."),

            // Рядки-заголовки. Вид рядка вже звірений у `Targets`, тому сюди
            // доходять лише ті, що під правило підпадають.
            PeriodAccessRuleKind.HeaderRows => Deny(
                rule, EditDenyReason.RowReadOnly, "Рядок-заголовок не заповнюється."),

            PeriodAccessRuleKind.EditablePeriodOnly => rule.AppliesTo(facts.PeriodSequence)
                ? PeriodRuleOutcome.Allowed
                : Deny(rule, EditDenyReason.OutOfAccessWindow,
                       $"Період {facts.PeriodSequence} поза вікном введення."),

            PeriodAccessRuleKind.RelativeWindow => RelativeWindow(rule, facts),
            PeriodAccessRuleKind.SourceWindow => SourceWindow(rule, facts),
            PeriodAccessRuleKind.Expression => ExpressionRule(rule, facts),

            _ => PeriodRuleOutcome.Allowed,
        };

    /// <summary>Вікно «поточний період ± N».</summary>
    /// <remarks>
    /// ⚠ Без відомого поточного періоду правило НЕ застосовується. Взяти
    /// «сьогодні» тут означало б рахувати доступ від годинника сервера, а не
    /// від календаря проєкту (<c>D-77</c>): проєкт із закріпленим періодом
    /// поводився б інакше, ніж показує.
    /// </remarks>
    private static PeriodRuleOutcome RelativeWindow(PeriodAccessRuleDef rule, PeriodRuleFacts facts)
    {
        if (facts.CurrentSequence is not { } current || rule.RelativeOffset is not { } offset)
        {
            return PeriodRuleOutcome.Allowed;
        }

        var distance = Math.Abs(facts.PeriodSequence - current);

        return distance <= offset
            ? PeriodRuleOutcome.Allowed
            : Deny(rule, EditDenyReason.OutOfAccessWindow,
                   $"Період {facts.PeriodSequence} далі, ніж {offset} від поточного {current}.");
    }

    /// <summary>
    /// Вікно береться з довідника: дати дії запису, на який посилається рядок
    /// (<c>ФВ-5.20</c>, перенос <c>ApplyPermitMonthLocks</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Рядок БЕЗ значення в колонці-джерелі правило не блокує. Зворотне
    /// перетворило б «дозвіл ще не обрали» на «нічого не можна заповнити», і
    /// заповнити рядок стало б неможливо в принципі — включно з самим
    /// вибором дозволу.
    ///
    /// ⚠ Немісячна колонка теж не блокується: правило про МІСЯЦІ, а колонка
    /// без місяця не належить жодному з них.
    /// </remarks>
    private static PeriodRuleOutcome SourceWindow(PeriodAccessRuleDef rule, PeriodRuleFacts facts)
    {
        if (rule.SourceColumnDefId is not { } columnId || facts.ColumnMonthNumber is not { } month)
        {
            return PeriodRuleOutcome.Allowed;
        }

        if (!facts.SourceValues.TryGetValue(columnId, out var window))
        {
            return PeriodRuleOutcome.Allowed;
        }

        // ⚠ Місяць порівнюється як ВІДРІЗОК, а не як дата. Дозвіл, чинний до
        // 15 червня, червень усе-таки покриває: викид за першу половину
        // місяця стався в межах дозволу, і заборонити ввід означало б
        // втратити реальні дані.
        var monthStart = new DateOnly(facts.PeriodYear, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var startsAfter = window.ValidTo is { } to && monthStart > to;
        var endsBefore = window.ValidFrom is { } from && monthEnd < from;

        if (!startsAfter && !endsBefore)
        {
            return PeriodRuleOutcome.Allowed;
        }

        return Deny(
            rule,
            EditDenyReason.OutsidePermitWindow,
            $"Місяць {month:00}.{facts.PeriodYear} поза вікном дії "
            + $"({Show(window.ValidFrom)} — {Show(window.ValidTo)}).");
    }

    /// <summary>Довільна умова над значеннями рядка.</summary>
    /// <remarks>
    /// ⚠ Вираз обчислює ВИКЛИКАЧ і кладе результат у факти: рушій виразів
    /// живе в іншому шарі, і тягнути його сюди означало б зробити чисту
    /// функцію залежною від розбору тексту.
    ///
    /// ⛔ Невідомий вираз (його не обчислили) НЕ блокує. Заборона за
    /// невідомістю зробила б будь-яку помилку конфігурації тихою відмовою в
    /// доступі, а причину шукали б у правах.
    /// </remarks>
    private static PeriodRuleOutcome ExpressionRule(PeriodAccessRuleDef rule, PeriodRuleFacts facts)
    {
        if (rule.ConditionExpr is not { } expression
            || !facts.ExpressionResults.TryGetValue(expression, out var holds))
        {
            return PeriodRuleOutcome.Allowed;
        }

        // Умова описує ЗАБОРОНУ: «якщо виконується — не можна».
        return holds
            ? Deny(rule, EditDenyReason.BusinessRule, $"Правило доступу: {expression}.")
            : PeriodRuleOutcome.Allowed;
    }

    /// <summary>Межа вікна для повідомлення; <c>…</c> — межі немає.</summary>
    private static string Show(DateOnly? date)
        => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "…";

    private static PeriodRuleOutcome Deny(
        PeriodAccessRuleDef rule, EditDenyReason reason, string detail)
        => new(reason, detail, rule.OnOutOfWindow);
}
