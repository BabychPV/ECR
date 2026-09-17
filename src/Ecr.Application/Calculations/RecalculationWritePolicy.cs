// src/Ecr.Application/Calculations/RecalculationWritePolicy.cs
using System.Globalization;
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations;

/// <summary>Чому перерахунок не має права писати в період.</summary>
public enum RecalculationWriteDenial
{
    /// <summary>Заперечень немає.</summary>
    None = 0,

    /// <summary>Період закритий, і погодження на перерахунок немає (ФВ-9.7).</summary>
    PeriodClosed = 1,

    /// <summary>У періоді є поданий або затверджений аркуш (ФВ-9.17).</summary>
    SheetsSubmitted = 2,
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
    /// <param name="hasClosedPeriodApproval">
    /// Чи є ЧИННЕ погодження на перерахунок закритого періоду
    /// (<c>ClosedPeriodApproval</c>: причина + друга людина). Саме воно —
    /// названий виняток, за яким системний перерахунок таки заходить у
    /// закритий період; перевірку якості погодження робить той, хто його
    /// приймає (<see cref="RunCalculationHandler"/>), а не ця функція.
    /// </param>
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
    /// ⛔ Поданий аркуш блокує НЕЗАЛЕЖНО від погодження (ФВ-9.17):
    /// <c>ClosedPeriodApproval</c> погоджує перерахунок ЗАКРИТОГО періоду, а
    /// не зміну поданої цифри. Для поданої цифри шлях один — Reopen, який
    /// лишає слід у робочому процесі.
    /// </remarks>
    public static RecalculationWriteDenial Check(
        PeriodState state, bool hasSubmittedSheets, bool hasClosedPeriodApproval)
    {
        if (state == PeriodState.Closed && !hasClosedPeriodApproval)
        {
            return RecalculationWriteDenial.PeriodClosed;
        }

        return hasSubmittedSheets
            ? RecalculationWriteDenial.SheetsSubmitted
            : RecalculationWriteDenial.None;
    }

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
                $"Період {key} закритий: закриті періоди не перераховуються автоматично — "
                + "потрібне окреме погодження (ФВ-9.7).",

            RecalculationWriteDenial.SheetsSubmitted =>
                $"Період {key} має подані аркуші: перерахунок змінив би числа, "
                + "які вже пішли на погодження. Штатний шлях — Reopen (ФВ-9.17).",

            _ => $"Період {key} перераховувати можна.",
        };
    }
}
