// src/Ecr.Application/Documents/GetCalculationResultsHandler.cs
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Documents;

/// <summary>
/// Числа, які дав розрахунок методологій на цьому документі за цей період.
/// </summary>
/// <remarks>
/// ⛔ Окреме читання, а не поле зрізу таблиці, і це не оформлення, а <c>D-69</c>:
/// результат методології НЕ потрапляє в <c>doc.CellValue</c> — інакше нічний
/// перерахунок писав би десятки мільйонів рядків у партиції документів і
/// роздував <c>aud.CellChange</c>. У документ він приходить посиланням через
/// <c>cfg.CalculationBinding</c>.
///
/// ⛔ Доти побачити результат методології було НІДЕ. Перерахунок завершувався
/// успіхом, число лягало в <c>calc.CalculationResult</c> — і жоден маршрут його
/// не віддавав: єдиним способом переконатися, що методологія порахувала саме
/// те, був <c>SELECT</c> у базі. Тобто приймання «методологія дає правильне
/// число на реальному документі» (директива №09, <c>W6</c>) не мало на чому
/// відбутися.
///
/// ⚠ Право — <c>Calculation.View</c>, а не <c>Document.View</c>. Питання тут не
/// «що в документі», а «що порахувала методологія і якою версією»; саме за цим
/// правом видно й самі методології.
/// </remarks>
public sealed class GetCalculationResultsHandler(
    ICalculationResultStore results,
    IMethodologyStore methodologies,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання результатів розрахунку (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Віддає числа актуального прогону.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період; результати партиційовані за ним.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Результати в порядку рядка й коду виходу; порожньо — актуального прогону
    /// за цей період немає.
    /// </returns>
    /// <remarks>
    /// ⚠ Порожній перелік, а не <c>404</c>: документ існує, просто його ще не
    /// рахували — або рахували, і жодна методологія до нього не прив'язана.
    /// Помилка тут виглядала б як «документа немає».
    /// </remarks>
    public async Task<IReadOnlyList<CalculationResultDto>> HandleAsync(
        long documentId, int periodKey, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        // ⛔ І ГРАНТ на проєкт документа (Q-175, аудит фази 2). `Calculation.View`
        // (а не `Document.View`) відповідає на питання «чи бачить ця людина
        // результати методологій узагалі» — але «яких САМЕ документів» і далі
        // визначає грант на проєкт, так само як для читання самого документа.
        // Без цієї перевірки будь-хто з `Calculation.View` бачив показники
        // викидів чужого проєкту.
        // ⛔ B-08: невидимий документ — 404, як і `GET /documents/{id}`, а не 403
        // «NoGrant»: різниця відповідей сама розкривала б, що документ існує.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var rows = await results.ReadCurrentAsync(documentId, periodKey, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        // ⛔ F-05 (четвертий раунд UX): свіжість. Доти після зміни входів панель
        // показувала старі числа як чинні, і документ подавали з результатами,
        // що вже не відповідали даним. Тепер кожне число каже, чи змінилися
        // входи після прогону, що його дав.
        var freshness = await methodologies
            .GetCalculationFreshnessAsync(documentId, periodKey, ct)
            .ConfigureAwait(false);

        // ⚠ F-21: номер версії й код методології замість голого ідентифікатора.
        var labels = await methodologies
            .GetVersionLabelsAsync([.. rows.Select(r => r.MethodologyVersionId).Distinct()], ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r =>
        {
            var label = labels.GetValueOrDefault(r.MethodologyVersionId);

            return new CalculationResultDto(
                r.MethodologyVersionId,
                r.SourceRowKey,
                r.OutputCode,
                r.Value,
                r.UnitId,
                r.SubstanceEntryId,
                label?.MethodologyCode,
                label?.Version,
                freshness.IsStale,
                freshness.CalculatedAt,
                freshness.InputsChangedAt);
        })];
    }
}
