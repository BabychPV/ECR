namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Контекст, який уміє обчислювати «хостові» функції — ті, ім'я яких починається з
/// <c>$</c>.
/// </summary>
/// <remarks>
/// ⚠ Парсери наявних діалектів імен на <c>$</c> не породжують (лексер не знає цього
/// символу), тому для наявних виразів гачок недосяжний: поведінка не змінюється.
/// Гачок призначений для дерев, які будує не парсер, а компілятор скриптів методологій
/// (<c>$lookup</c>, <c>$call:f</c>, <c>$entry</c> …): їх обчислює контекст скрипта, а не
/// <c>MethodologyFunctions</c>.
///
/// ⚠ Аргументи вже обчислено (<c>EvaluateScalar</c>, під сторожем глибини). Реалізація,
/// яка всередині викликає обчислювач ще раз (тіло власної функції скрипта), мусить
/// передати той самий бюджет (параметр <c>budget</c>): вкладене обчислення витрачає бюджет
/// зовнішнього.
/// </remarks>
public interface IHostFunctionContext : IEvaluationContext
{
    /// <summary>Обчислює хостову функцію (ім'я починається з '$'); false — функції немає.</summary>
    /// <param name="name">Ім'я функції разом із <c>$</c>.</param>
    /// <param name="args">Обчислені аргументи.</param>
    /// <param name="budget">Бюджет поточного обчислення.</param>
    /// <param name="result">Результат, якщо функція є.</param>
    public bool TryInvokeHost(
        string name,
        IReadOnlyList<ExpressionValue> args,
        EvaluationBudget budget,
        out ExpressionValue result);
}
