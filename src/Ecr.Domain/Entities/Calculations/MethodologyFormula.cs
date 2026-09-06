// src/Ecr.Domain/Entities/Calculations/MethodologyFormula.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Формула методології. <see cref="EvaluationOrder"/> — **обчислюваний**, а не
/// введений: порядок топологічний і рахується при публікації (ФВ-9.4).
/// </summary>
/// <remarks>
/// Дозволити людині задати порядок руками означало б, що додана формула тихо
/// зміщує решту, а помилка виявиться числом у звіті, не помилкою публікації.
/// </remarks>
public sealed class MethodologyFormula : Entity<int>
{
    private MethodologyFormula() { }

    public MethodologyFormula(int methodologyVersionId, EcrCode code, string expression)
    {
        MethodologyVersionId = methodologyVersionId;
        Code = code.Value;
        Expression = expression;
    }

    public int MethodologyVersionId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Expression { get; private set; } = null!;

    /// <summary>Топологічний порядок. Заповнюється при `Publish`, не користувачем.</summary>
    public int EvaluationOrder { get; private set; }

    public int? OutputUnitId { get; private set; }

    /// <summary>
    /// Що формула повертає: число чи текст (директива ПК-1 №05, поправка 2-біс).
    /// </summary>
    /// <remarks>
    /// ⚠ Не декоративне поле. У корпусі формула повертає
    /// <c>'В пределе норматива'</c>, <c>'Сверхнорматив'</c>,
    /// <c>'Превышение!!!'</c> — це значення, а не повідомлення, і воно лягає в
    /// текстову колонку звіту (02b §8). Без оголошеного типу такий результат
    /// пішов би в <c>calc.CalculationResult.Value decimal(28,10)</c> і
    /// перетворився б на нуль або на аварію конверсії — залежно від того, хто
    /// перший його прочитає.
    /// </remarks>
    public FormulaResultType ResultType { get; private set; }

    /// <summary>Проставляє порядок, отриманий із графа залежностей.</summary>
    /// <param name="order">Позиція в топологічному порядку.</param>
    /// <remarks>
    /// ⚠ Викликається **лише** з процедури публікації після топологічного
    /// сортування. Дозволити людині задати порядок руками означало б, що додана
    /// формула тихо зміщує решту, а помилка виявиться числом у звіті, не
    /// помилкою публікації (ФВ-9.4).
    /// </remarks>
    public void SetEvaluationOrder(int order)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(order);
        EvaluationOrder = order;
    }

    /// <summary>Оголошує одиницю результату формули (ФВ-16.6).</summary>
    /// <param name="unitId">Одиниця з <c>uom.Unit</c>.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-CALC-0422</c> — одиниця на формулі, що повертає текст.
    /// </exception>
    /// <remarks>
    /// ⚠ Друга половина того самого правила, що й у <see cref="SetResultType"/>:
    /// без неї порядок викликів визначав би, спрацює перевірка чи ні.
    /// </remarks>
    public void SetOutputUnit(int unitId)
    {
        if (ResultType == FormulaResultType.Text)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Формула «{Code}» повертає текст: одиниця результату для неї не має сенсу (ФВ-16.6).");
        }

        OutputUnitId = unitId;
    }

    /// <summary>Оголошує тип результату формули.</summary>
    /// <param name="resultType">Число або текст.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-CALC-0422</c> — текстовий результат з оголошеною одиницею.
    /// </exception>
    /// <remarks>
    /// ⛔ Одиниця на текстовому результаті — суперечність, і саме та, що не
    /// має симптому: перевірка розмірностей при публікації (02b §12, крок 10)
    /// порівняла б <c>'Сверхнорматив'</c> з тоннами і не знайшла б розбіжності,
    /// бо порівнює вона одиниці, а не значення.
    /// </remarks>
    public void SetResultType(FormulaResultType resultType)
    {
        if (resultType == FormulaResultType.Text && OutputUnitId is not null)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Формула «{Code}» повертає текст і не може мати одиниці результату: "
                + "вимір — властивість числа (ФВ-16.6).");
        }

        ResultType = resultType;
    }
}
