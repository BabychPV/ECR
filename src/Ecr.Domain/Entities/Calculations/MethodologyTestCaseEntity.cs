using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Тест методології (<c>calc.TestCase</c>): вхід, очікуваний вихід, допуск.
/// </summary>
/// <remarks>
/// ⚠ <b>Тести — це дані</b> (ФВ-13.7), а не код: інженер-технолог заводить їх
/// разом із методологією і не чекає релізу. Саме на них тримається заборона
/// публікації без зеленого тесту (ФВ-9.12) — найнебезпечнішої операції
/// системи: опублікована методологія переписує числа за минулі періоди.
///
/// ⛔ Таблиці не було в схемі, хоч вимога прямо на неї посилалася (`P-08`).
/// Доки її не існувало, порт віддавав порожній набір, і публікація
/// відхилялася завжди — правило працювало, але користі не було.
/// </remarks>
public sealed class MethodologyTestCaseEntity : Entity<int>
{
    private MethodologyTestCaseEntity() { }

    /// <summary>Створює тест.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="code">Код тесту, унікальний у межах версії.</param>
    /// <param name="inputJson">Аргументи прогону.</param>
    /// <param name="expectedJson">Очікувані виходи: код виходу → число.</param>
    /// <param name="tolerance">Допуск порівняння; нуль — точна рівність.</param>
    public MethodologyTestCaseEntity(
        int methodologyVersionId, string code, string inputJson, string expectedJson, decimal tolerance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedJson);

        // ⛔ Від'ємний допуск — не «жорсткіше», а безглуздо: жодне число не
        // потрапить у діапазон, і тест не пройде ніколи. Мовчазне
        // перетворення на нуль приховало б помилку конфігурації.
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);

        MethodologyVersionId = methodologyVersionId;
        Code = code;
        InputJson = inputJson;
        ExpectedJson = expectedJson;
        Tolerance = tolerance;
    }

    public int MethodologyVersionId { get; private set; }

    /// <summary>Код тесту; унікальний у межах версії.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Аргументи прогону у формі <c>CalculationInput</c>.</summary>
    public string InputJson { get; private set; } = null!;

    /// <summary>Очікувані виходи: <c>{"gsec":12.5}</c>.</summary>
    public string ExpectedJson { get; private set; } = null!;

    /// <summary>
    /// Допуск порівняння.
    /// </summary>
    /// <remarks>
    /// ⚠ Потрібен саме тому, що числа звітності рахуються в <c>decimal</c> з
    /// округленням на кожному кроці: очікувати побітової рівності означало б
    /// червоний тест від зміни порядку доданків, яка нічого не змінює по суті.
    /// </remarks>
    public decimal Tolerance { get; private set; }

    /// <summary>Оновлює вміст тесту.</summary>
    /// <param name="inputJson">Аргументи прогону.</param>
    /// <param name="expectedJson">Очікувані виходи.</param>
    /// <param name="tolerance">Допуск.</param>
    public void Update(string inputJson, string expectedJson, decimal tolerance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedJson);
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);

        InputJson = inputJson;
        ExpectedJson = expectedJson;
        Tolerance = tolerance;
    }
}
