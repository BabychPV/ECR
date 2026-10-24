using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>
/// Одне ДІЮЧЕ надання учасникові документа (D-443): рівень і область (аркуш/таблиця за кодами, період
/// передачі місяця). Будує <see cref="DocumentParticipation.Active"/> з фактів передач.
/// </summary>
/// <param name="Level">Рівень надання (<c>Read</c>/<c>Write</c>/<c>Submit</c>/<c>Approve</c>).</param>
/// <param name="SheetCode">Аркуш; <c>null</c> — увесь документ.</param>
/// <param name="TableCode">Таблиця аркуша; <c>null</c> — усі таблиці.</param>
/// <param name="Period">Період (лише передача місяця <c>Write</c>); <c>null</c> — будь-який.</param>
public sealed record ParticipationGrant(GrantLevel Level, string? SheetCode, string? TableCode, PeriodKey? Period);

/// <summary>
/// Чисті правила «учасника документа» (D-443, AN-130): що надання дає в рішенні про доступ.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Надання ДАЄ видимість (скасовано «передача читання не дає ніколи»), але лише всередині ОДНОГО документа і
/// лише в його області. Заборону ролі (<c>IsDeny</c>) воно не обходить: <see cref="EditRules.Effective"/> повертає
/// <see cref="GrantLevel.None"/> на забороні РАНІШЕ, ніж дивиться на надання.
/// </para>
/// <para>
/// ⚠ Рівень <see cref="GrantLevel.Write"/> — колишня передача місяця: у рівень рішення він дає лише
/// <see cref="GrantLevel.Read"/> (видимість), а запис піднімає, як і доти, <see cref="EditAssignmentGuard"/>
/// (<c>DelegatedWrite</c>: лише комірки зрізу, лише до «Заповнено»/«Валідовано», з ексклюзивністю). Так передача
/// місяця поводиться побайтно як до D-443, а читання їй додано.
/// </para>
/// <para>
/// ⚠ Діє надання лише ВИКОНАВЦЯ (<see cref="EditAssignmentFact.MyProgress"/> не <c>null</c>), з активним
/// делегатором (R7) і не прострочене — ті самі умови, що й підйом запису. Невідомий вимір при заданому
/// селекторі — НЕ збіг (закрито за замовчуванням).
/// </para>
/// </remarks>
public static class DocumentParticipation
{
    /// <summary>Проєктні права, які надання учасника дає В ЙОГО документі (вхід у читання документа).</summary>
    public static readonly IReadOnlySet<string> ImpliedPermissions =
        new HashSet<string>(StringComparer.Ordinal) { "Document.View" };

    /// <summary>Діючі надання користувача з фактів передач одного документа.</summary>
    /// <param name="facts">Факти передач документа з погляду користувача.</param>
    /// <param name="utcNow">Поточний час (строк дії).</param>
    public static IReadOnlyList<ParticipationGrant> Active(IEnumerable<EditAssignmentFact> facts, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var result = new List<ParticipationGrant>();
        foreach (var fact in facts)
        {
            if (fact.MyProgress is null
                || !fact.DelegatorActive
                || (fact.ExpiresAt is { } until && until <= utcNow)
                || fact.Level == GrantLevel.None)
            {
                continue;
            }

            result.Add(new ParticipationGrant(fact.Level, fact.SheetCode, fact.TableCode, fact.Period));
        }

        return result;
    }

    /// <summary>
    /// Рівень, який надання дають у рішенні про аркуш (<paramref name="tableCode"/> = <c>null</c>) чи таблицю.
    /// </summary>
    /// <param name="grants">Діючі надання користувача в документі.</param>
    /// <param name="sheetCode">Код аркуша рішення; <c>null</c> — невідомий.</param>
    /// <param name="tableCode">Код таблиці; <c>null</c> — рішення про аркуш цілком (подання, погодження).</param>
    /// <param name="period">Період рішення; <c>null</c> — невідомий.</param>
    /// <remarks>
    /// ⛔ Рішення про аркуш бере лише надання на документ чи на цей аркуш: надання на одну таблицю не дає
    /// подавати чи погоджувати весь аркуш. <c>Write</c> → <c>Read</c> (запис — через <see cref="EditAssignmentGuard"/>).
    /// </remarks>
    public static GrantLevel LevelAt(
        IReadOnlyList<ParticipationGrant> grants, string? sheetCode, string? tableCode, PeriodKey? period)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var level = GrantLevel.None;
        foreach (var grant in grants)
        {
            if (!SheetMatches(grant, sheetCode)
                || !PeriodMatches(grant, period)
                || (grant.TableCode is not null
                    && (tableCode is null || !string.Equals(grant.TableCode, tableCode, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            var contributes = grant.Level == GrantLevel.Write ? GrantLevel.Read : grant.Level;
            if (contributes > level)
            {
                level = contributes;
            }
        }

        return level;
    }

    /// <summary>
    /// Чи відкриває надання АРКУШ для перегляду: надання на документ, на аркуш або на будь-яку його таблицю
    /// (аркуш з'являється, а всередині — лише надані таблиці).
    /// </summary>
    /// <param name="grants">Діючі надання користувача в документі.</param>
    /// <param name="sheetCode">Код аркуша; <c>null</c> — невідомий (не відкриває).</param>
    /// <param name="period">Період; <c>null</c> — невідомий.</param>
    public static bool OpensSheet(IReadOnlyList<ParticipationGrant> grants, string? sheetCode, PeriodKey? period)
    {
        ArgumentNullException.ThrowIfNull(grants);

        foreach (var grant in grants)
        {
            if (SheetMatches(grant, sheetCode) && PeriodMatches(grant, period))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ✎ D-449: чи ПРАЦЮЄ учасник над аркушем — має діюче надання рівня <see cref="GrantLevel.Write"/> чи
    /// <see cref="GrantLevel.Submit"/>, що відкриває цей аркуш (на документ, аркуш або його таблицю) у періоді.
    /// Повернений на доопрацювання аркуш — його задача («Повернуто на доопрацювання»).
    /// </summary>
    /// <param name="grants">Діючі надання користувача в документі.</param>
    /// <param name="sheetCode">Код аркуша; <c>null</c> — невідомий (не працює).</param>
    /// <param name="period">Період; <c>null</c> — невідомий.</param>
    /// <remarks>
    /// ⚠ Збіг області — ТІ САМІ правила, що <see cref="OpensSheet"/> (аркуш, період); рівень — сирий рівень надання, а
    /// не <see cref="LevelAt"/> (там <c>Write</c> зводиться до <c>Read</c> рішення). <c>Read</c> — лише перегляд, а
    /// <c>Approve</c> — погоджувач: повернення від погоджувача не є задачею погоджувача (рішення D-449).
    /// </remarks>
    public static bool WorksOnSheet(IReadOnlyList<ParticipationGrant> grants, string? sheetCode, PeriodKey? period)
    {
        ArgumentNullException.ThrowIfNull(grants);

        foreach (var grant in grants)
        {
            if (grant.Level is GrantLevel.Write or GrantLevel.Submit
                && SheetMatches(grant, sheetCode)
                && PeriodMatches(grant, period))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SheetMatches(ParticipationGrant grant, string? sheetCode)
        => grant.SheetCode is null
           || (sheetCode is not null && string.Equals(grant.SheetCode, sheetCode, StringComparison.OrdinalIgnoreCase));

    private static bool PeriodMatches(ParticipationGrant grant, PeriodKey? period)
        => grant.Period is null || (period is { } p && p.Value == grant.Period.Value.Value);
}
