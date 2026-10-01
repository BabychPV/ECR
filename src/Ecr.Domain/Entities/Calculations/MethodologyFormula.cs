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
    /// пішов би в <c>calc.CalculationResult.Value decimal(34,16)</c> і
    /// перетворився б на нуль або на аварію конверсії — залежно від того, хто
    /// перший його прочитає.
    /// </remarks>
    public FormulaResultType ResultType { get; private set; }

    /// <summary>
    /// Оголошені аргументи — <c>;</c>-список, як у <c>FInfo_Arguments</c>
    /// (директива ПК-1 №05 §7, пастка 2). <c>null</c> — список не оголошено.
    /// </summary>
    /// <remarks>
    /// ⛔ Це **джерело істини про аргументи**, а не текст виразу. Чинна збірка
    /// підставляє рівно те, що перелічено тут; токен, якого в списку немає, у
    /// вираз **не потрапляє** — і формула рахується з невизначеним параметром
    /// без жодної ознаки збою. Саме тому <c>ECR-CALC-0432</c> — помилка
    /// публікації, а не попередження.
    ///
    /// ⚠ Зберігається **як написано**, без нормалізації. Заміну <c>.</c> на
    /// <c>_</c> і зняття регістру робить звірка
    /// (<c>ArgumentDeclarationChecker</c>), а не запис: нормалізувати при
    /// збереженні означало б утратити те, що набрав методолог, і показувати
    /// йому в редакторі чуже.
    ///
    /// ⚠ <c>null</c> і порожній рядок — різні речі. <c>null</c> означає «списку
    /// немає», і тоді звірка мовчить; порожній — «оголошено нуль аргументів»,
    /// і тоді будь-який токен у виразі є порушенням.
    /// </remarks>
    public string? ArgumentsCsv { get; private set; }

    /// <summary>
    /// Область формули: на кожну речовину чи раз на рядок (<c>D-176</c>, V-7).
    /// </summary>
    /// <remarks>
    /// ⛔ Типове — <see cref="MethodologyFormulaScope.Substance"/>, тобто рівно
    /// поведінка до цього поля: рушій рахує кожну формулу на кожну речовину.
    /// Інше типове значення тихо змінило б кожну наявну методологію — Row-формула
    /// не бачить <c>SUBSTANCE(…)</c> і констант, заданих по речовинах.
    /// </remarks>
    public MethodologyFormulaScope Scope { get; private set; } = MethodologyFormulaScope.Substance;

    /// <summary>
    /// Показувати значення формули як проміжний результат (<c>D-175</c>, V-6):
    /// рядок <c>calc.CalculationResult</c> з <c>Kind = Intermediate</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Типове — <c>false</c>: без явного прапорця обсяг <c>calc.*</c> не росте
    /// на жодній наявній методології (HR-8).
    /// </remarks>
    public bool IsVisible { get; private set; }

    /// <summary>Задає область формули.</summary>
    /// <param name="scope">Речовина або рядок.</param>
    /// <exception cref="ArgumentOutOfRangeException">Значення поза переліком.</exception>
    /// <remarks>
    /// ⚠ Перевірка «Row-формула не посилається на Substance» — справа публікації
    /// (<c>PublishMethodologyHandler</c>): формула не бачить сусідніх формул.
    /// </remarks>
    public void SetScope(MethodologyFormulaScope scope)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Невідома область формули.");
        }

        Scope = scope;
    }

    /// <summary>Вмикає або вимикає показ формули як проміжного результату.</summary>
    /// <param name="isVisible"><c>true</c> — значення пишеться в результати.</param>
    public void SetVisible(bool isVisible) => IsVisible = isVisible;

    /// <summary>Оголошує список аргументів формули.</summary>
    /// <param name="argumentsCsv"><c>;</c>-список; <c>null</c> — списку немає.</param>
    public void SetArguments(string? argumentsCsv) => ArgumentsCsv = argumentsCsv;

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

    /// <summary>Замінює вираз формули.</summary>
    /// <param name="expression">Новий вираз діалекту методологій.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-CALC-0422</c> — порожній вираз.
    /// </exception>
    /// <remarks>
    /// ⛔ Метод <b>internal</b>, і це не оформлення. Формула не знає, чи
    /// опублікована її версія, — знає це <see cref="MethodologyVersion"/>.
    /// Публічний сетер означав би другий шлях зміни виразу, на якому перевірки
    /// «опублікована версія незмінна» (ФВ-13.2) немає, і саме ним скористався б
    /// перший обробник, якому вона здалася зайвою. Єдиний вхід —
    /// <see cref="MethodologyVersion.EditFormula"/>.
    /// <para>
    /// ⚠ Порожній вираз відхиляється тут, а не в формі. Формула без виразу не
    /// зникає з розрахунку: вона лишається оголошеним виходом і дає нуль, який
    /// нічим не відрізняється від порахованого.
    /// </para>
    /// </remarks>
    internal void SetExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Формула «{Code}» без виразу: порожній вираз не прибирає формулу з розрахунку, "
                + "а робить її тихим нулем.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.formulaNoExpression",
                    ["code"] = Code,
                });
        }

        // ⛔ Межа колонки `calc.MethodologyFormula.Expression`: задовгий вираз доходив до
        // `SaveChanges` і падав обрізанням рядка в SQL — тобто 500 без жодного натяку.
        if (expression.Length > MaxExpressionLength)
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Вираз формули «{Code}» довший за {MaxExpressionLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.formulaTooLong",
                    ["code"] = Code,
                    ["maxLength"] = MaxExpressionLength,
                });
        }

        Expression = expression;
    }

    /// <summary>Межа виразу — довжина колонки <c>Expression</c> у <c>MethodologyFormulaConfiguration</c>.</summary>
    public const int MaxExpressionLength = 2000;

    /// <summary>Знімає одиницю результату.</summary>
    /// <remarks>
    /// ⛔ Без цього методу числову формулу з тоннами неможливо було б перевести
    /// в текстову взагалі: <see cref="SetResultType"/> відхиляє текст на
    /// формулі з одиницею, а зняти одиницю не було чим. Заборона, яка не має
    /// виходу, — це не інваріант, а глухий кут.
    /// </remarks>
    internal void ClearOutputUnit() => OutputUnitId = null;

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
                $"Формула «{Code}» повертає текст: одиниця результату для неї не має сенсу (ФВ-16.6).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.textFormulaUnit",
                    ["code"] = Code,
                });
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
                + "вимір — властивість числа (ФВ-16.6).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.textFormulaUnit",
                    ["code"] = Code,
                });
        }

        ResultType = resultType;
    }
}
