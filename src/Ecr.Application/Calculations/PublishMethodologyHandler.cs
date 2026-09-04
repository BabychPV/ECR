// src/Ecr.Application/Calculations/PublishMethodologyHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Calculations;

/// <summary>
/// Публікація версії методології — **найнебезпечніша операція в системі**:
/// вона змінює числа, які вже подані.
/// </summary>
/// <remarks>
/// Тому тут diff **результатів** на золотому наборі, а не diff коду (ФВ-9.6):
/// змінений рядок формули нічого не каже, а змінена на 4 % емісія — каже все.
/// </remarks>
public sealed class PublishMethodologyHandler(
    ICalculationModule module,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public Task HandleAsync(int methodologyVersionId, string changeReason, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) чотири очі: публікує НЕ автор останньої правки → ECR-CALC-0409 (D-40);\n" +
            "2) зелений тест обов'язковий → інакше ECR-CALC-0422 (ФВ-9.12);\n" +
            "3) перевірка одиниць: несумісні величини без CONVERT → ECR-TMPL-4223 (ФВ-16.6);\n" +
            "4) перетин і покриття правил прив'язки (ФВ-13.9);\n" +
            "5) топологічний порядок формул → SetEvaluationOrder; цикл → відмова;\n" +
            "6) DIFF РЕЗУЛЬТАТІВ на золотому наборі; NumericMode і CalendarMode " +
            "   ОБОВ'ЯЗКОВО в diff (ФВ-7.8) — їх зміна тихо змінює всі числа;\n" +
            "7) changeReason обов'язковий (ФВ-14.7);\n" +
            "8) ⛔ закриті періоди не перераховувати автоматично навіть після " +
            "   публікації (ФВ-9.7).");
}
