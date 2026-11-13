// src/Ecr.Application/Calculations/RecalculationWritePolicy.cs
using System.Globalization;
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations;

/// <summary>Чому перерахунок не має права писати в період.</summary>
public enum RecalculationWriteDenial
{
    /// <summary>Заперечень немає.</summary>
    None = 0,

    /// <summary>Період закритий (ФВ-9.7); шлях — Reopen, D-468.</summary>
    PeriodClosed = 1,

    /// <summary>У періоді є поданий або затверджений аркуш (ФВ-9.17).</summary>
    SheetsSubmitted = 2,

    /// <summary>Проєкт архівований або архівується (B69): архівні дані не змінюються, жоден виняток не діє.</summary>
    ProjectArchived = 3,
}

/// <summary>
/// ЄДИНЕ рішення «чи можна писати результати перерахунку в цей період».
/// </summary>
/// <remarks>
/// ⛔ Клас існує тому, що рішення було РОЗМНОЖЕНЕ й через це діяло лише на
/// одному з трьох маршрутів. <c>RunCalculationHandler.HandleAsync</c>
/// (перерахунок проєкту) перевіряв стан періоду сам, у себе в тілі; маршрут
/// ДОКУМЕНТА (<c>RecalculateDocumentHandler</c>) і нічний розклад
/// (<c>NightlyRecalculationScheduling</c>) кладуть <c>IRecalculationJob</c> у
/// чергу НАПРЯМУ, повз цей обробник, а сама задача кличе з нього тільки
/// <c>CompleteAsync</c> — завершення прогону, де періодів немає. Перевірка,
/// що живе в тілі одного з трьох викликачів, не є гейтом: вона є звичкою
/// цього викликача.
///
/// ⚠ Чиста функція без портів — навмисно, з тієї самої причини, що й
/// <c>EditRules</c>: факти («який стан», «чи є подані аркуші») кожен маршрут
/// дістає у своїй гранулярності (проєкт — з <c>IPeriodStore</c>/
/// <c>IWorkflowStore</c>, задача — своїм <c>EcrDbContext</c>), а САМЕ ПРАВИЛО
/// мусить бути одне. Два місця, які вирішують «чи можна писати», рано чи
/// пізно розходяться — і це дефект на рівень вищий за той, що тут лагодиться.
/// </remarks>
public static class RecalculationWritePolicy
{
    /// <summary>Код відмови; стабільний за контрактом (`02-contracts.md` §9).</summary>
    public const string ErrorCode = Domain.Errors.ErrorCodes.RecalculateClosedPeriod;

    /// <summary>Чи можна писати перерахунок у період із такими фактами.</summary>
    /// <param name="state">Стан періоду.</param>
    /// <param name="hasSubmittedSheets">Чи є в періоді поданий або затверджений аркуш.</param>
    /// <returns><see cref="RecalculationWriteDenial.None"/> — писати можна.</returns>
    /// <remarks>
    /// ⚠ Писати можна в <c>Open</c> і <c>Grace</c> — той самий перелік, що й
    /// <c>Period.AllowsEditing</c> та <c>EditRules.CanEdit</c> для людини.
    /// <c>Grace</c> тут ключовий і не є послабленням: <c>Period.Reopen</c>
    /// переводить закритий період саме в <c>Grace</c>, і відкриття існує рівно
    /// для того, щоб виправлення порахувалося. Гейт, який ламає Reopen,
    /// перетворив би штатний шлях виправлення на глухий кут.
    ///
    /// ⚠ <c>Scheduled</c> тут НЕ відмовляється, хоча <c>EditRules</c> ручну
    /// правку в ньому забороняє. Це свідоме звуження цього фіксу до дефекту,
    /// який він лагодить (запис у ЗАКРИТЕ й ПОДАНЕ): у не розпочатому періоді
    /// даних немає за визначенням, тож перерахунок там пише нуль комірок і
    /// нічого не псує, а розширити гейт на <c>Scheduled</c> означало б у тому
    /// самому пакеті змінити поведінку, якої ніхто не міряв.
    ///
    /// ⛔ ✎ 07.11 (квіз людини, D-468): закритий місяць має ОДИН шлях — Reopen (з причиною) →
    /// звичайний перерахунок. Погодження перерахунку закритого періоду (<c>calc.RecalculationApproval</c>,
    /// аудит S1) прибрано: винятку, за яким системний перерахунок заходить у <c>Closed</c>, більше немає.
    /// </remarks>
    public static RecalculationWriteDenial Check(PeriodState state, bool hasSubmittedSheets)
    {
        if (state == PeriodState.Closed)
        {
            return RecalculationWriteDenial.PeriodClosed;
        }

        return hasSubmittedSheets
            ? RecalculationWriteDenial.SheetsSubmitted
            : RecalculationWriteDenial.None;
    }

    /// <summary>Чи можна писати перерахунок у проєкт із таким станом (B69).</summary>
    /// <param name="status">Стан проєкту.</param>
    /// <param name="isArchiving">Чи триває архівація.</param>
    /// <returns><see cref="RecalculationWriteDenial.ProjectArchived"/> — проєкт архівований чи архівується.</returns>
    /// <remarks>
    /// ⛔ B69 (слово людини: «заархівовані дані не мають змінюватися»). Окремо від <see cref="Check"/> навмисно: виняток
    /// «місяць відкрито для документа» (<c>doc.DocumentPeriodReopen</c>) піднімає ЗАКРИТИЙ період до <c>Grace</c> ще до
    /// <see cref="Check"/>, і виняток, що пережив архівацію, пускав ручний перерахунок в архівований проєкт. Стан
    /// ПРОЄКТУ цим винятком не підміняється.
    /// </remarks>
    public static RecalculationWriteDenial CheckProject(ProjectStatus status, bool isArchiving)
        => status == ProjectStatus.Archived || isArchiving
            ? RecalculationWriteDenial.ProjectArchived
            : RecalculationWriteDenial.None;

    /// <summary>Пояснення відмови для людини; містить ключ періоду.</summary>
    /// <param name="denial">Причина відмови.</param>
    /// <param name="periodKey">Період, якого стосується відмова.</param>
    /// <remarks>
    /// ⚠ Ключ періоду в тексті обов'язковий: запит на перерахунок буває на
    /// цілий рік, і «період закрито» без номера змушує оператора шукати, який
    /// саме з дванадцяти.
    /// </remarks>
    public static string Explain(RecalculationWriteDenial denial, int periodKey)
    {
        var key = periodKey.ToString(CultureInfo.InvariantCulture);

        return denial switch
        {
            RecalculationWriteDenial.PeriodClosed =>
                $"Період {key} закритий: закритий період не перераховується — "
                + "спершу відкрийте його повторно (Reopen, з причиною), далі звичайний перерахунок (ФВ-9.7, D-468).",

            RecalculationWriteDenial.SheetsSubmitted =>
                $"Період {key} має подані аркуші: перерахунок змінив би числа, "
                + "які вже пішли на погодження. Штатний шлях — Reopen (ФВ-9.17).",

            RecalculationWriteDenial.ProjectArchived =>
                $"Проєкт архівований або архівується: його дані (період {key}) не перераховуються (B69).",

            _ => $"Період {key} перераховувати можна.",
        };
    }

    /// <summary>Ключ каталогу для причини відмови (<c>err.ECR-CALC-4221.&lt;причина&gt;</c>).</summary>
    /// <param name="denial">Причина відмови.</param>
    public static string MessageKey(RecalculationWriteDenial denial) => denial switch
    {
        RecalculationWriteDenial.PeriodClosed => "err.ECR-CALC-4221.periodClosed",
        RecalculationWriteDenial.SheetsSubmitted => "err.ECR-CALC-4221.sheetsSubmitted",
        RecalculationWriteDenial.ProjectArchived => "err.ECR-CALC-4221.projectArchived",
        _ => throw new ArgumentOutOfRangeException(nameof(denial), denial, "Відмови немає."),
    };

    /// <summary>
    /// Відмова перерахунку — однакова для всіх трьох маршрутів (проєкт, документ, задача).
    /// </summary>
    /// <param name="denial">Причина відмови (не <see cref="RecalculationWriteDenial.None"/>).</param>
    /// <param name="periodKey">Період, якого стосується відмова.</param>
    /// <param name="documentId">Документ, якщо маршрут про нього знає.</param>
    /// <remarks>
    /// Сирі <c>periodKey</c> (число) і <c>denial</c> лишаються для клієнта без змін;
    /// резолвер підставляє лише рядки, тому для тексту — окреме поле <c>period</c>.
    /// </remarks>
    public static Errors.BusinessRuleException Reject(
        RecalculationWriteDenial denial, int periodKey, object? documentId = null)
    {
        var details = new Dictionary<string, object?>
        {
            ["messageKey"] = MessageKey(denial),
            ["period"] = periodKey.ToString(CultureInfo.InvariantCulture),
            ["periodKey"] = periodKey,
            ["denial"] = denial.ToString(),
        };

        if (documentId is not null)
        {
            details["documentId"] = documentId;
        }

        return new Errors.BusinessRuleException(ErrorCode, Explain(denial, periodKey), details);
    }
}
