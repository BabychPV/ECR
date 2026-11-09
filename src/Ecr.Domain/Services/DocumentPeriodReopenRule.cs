using Ecr.Domain.Enums;

namespace Ecr.Domain.Services;

/// <summary>
/// Ефективний стан періоду ДЛЯ ДОКУМЕНТА з урахуванням винятку «місяць відкрито лише для цього документа»
/// (<see cref="Entities.Documents.DocumentPeriodReopen"/>, приймальна 08.11 (i)).
/// </summary>
/// <remarks>
/// ⛔ Одне правило на всі місця, де стан періоду вирішує запис чи перерахунок документа: рішення про доступ
/// (<c>AccessDecisionService</c>), стан для пізньої правки й автоперерахунку (<c>PeriodStore.FindPeriodStateAsync</c>),
/// гейт перерахунку (<c>RecalculationJob</c>), повернення аркуша (<c>ClosedPeriodGuard</c>). Дві копії розійшлися б.
/// <para>
/// ⚠ Піднімає ЛИШЕ <see cref="PeriodState.Closed"/> і лише до <see cref="PeriodState.Grace"/> — як ручний Reopen
/// періоду (ФВ-1.10, D-70): правки в такому місяці пізні. <c>Scheduled</c>/<c>Open</c>/<c>Grace</c> не змінюються.
/// </para>
/// </remarks>
public static class DocumentPeriodReopenRule
{
    /// <summary>Стан періоду для документа.</summary>
    /// <param name="effective">Ефективний стан періоду (той самий для всіх документів місяця).</param>
    /// <param name="documentReopened">У документа є діючий виняток за цей місяць.</param>
    public static PeriodState Apply(PeriodState effective, bool documentReopened)
        => effective == PeriodState.Closed && documentReopened ? PeriodState.Grace : effective;
}
