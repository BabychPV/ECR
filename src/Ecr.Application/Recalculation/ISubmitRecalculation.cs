using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Перерахунок формул шаблону одного аркуша всередині транзакції подання,
/// ДО побудови зрізу.
/// </summary>
/// <remarks>
/// ⛔ Навіщо. Правка входу ставить каскадний перерахунок у ЧЕРГУ
/// (<c>FormulaRecalculationJob</c>) і виконується після коміту правки. Подання,
/// що прийшло раніше за задачу, заморожувало в <c>calc.SubmissionSnapshot</c>
/// свіжий вхід і ЗАСТАРІЛЕ обчислене число, а сама задача після подання поданий
/// аркуш уже законно пропускає (ФВ-9.17). Тепер подання рахує формули свого
/// аркуша саме — під своїм винятковим блокуванням, тож зріз, жива комірка й
/// формула завжди узгоджені (<c>SubmitRecalculationRaceTests</c>).
///
/// ⚠ Окремий інтерфейс, а не пряма залежність від
/// <see cref="RecalculationService"/>: обробник подання тестується з підставками,
/// а сервіс перерахунку тягне дванадцять залежностей.
///
/// ⛔ Лише ВСЕРЕДИНІ транзакції, у якій викликач уже тримає ВИНЯТКОВЕ
/// блокування цього аркуша (<c>ISheetEditGate.EnterSubmitAsync</c>): спільного
/// блокування цього аркуша прогін не бере.
/// </remarks>
public interface ISubmitRecalculation
{
    /// <summary>Перераховує формули шаблону аркуша за період і записує результати.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш, який подається.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки комірок записано.</returns>
    public Task<int> RecalculateSheetUnderSubmitLockAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);
}
