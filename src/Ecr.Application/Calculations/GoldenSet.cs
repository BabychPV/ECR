using Ecr.Application.Ports;

namespace Ecr.Application.Calculations;

/// <summary>
/// Що означає «золотий набір зійшовся» (<c>ФВ-13.7</c>, <c>ФВ-9.12</c>).
/// </summary>
/// <remarks>
/// ⛔ Клас існує заради ОДНОГО визначення зеленого. До нього порівняння з
/// очікуваними числами жило приватним методом усередині публікації — тобто
/// побачити його результат можна було, лише спробувавши опублікувати. Прогін
/// «без запису» (<c>ФВ-13.5</c>) проганяв ті самі тести і **жодного разу не
/// звіряв їх з очікуваннями**: людина бачила числа й різницю з чинною версією,
/// але не бачила, зійшлися вони чи ні, — а публікація відмовляла саме тому.
///
/// ⚠ Друге визначення зеленого розійшлося б із першим, і розбіжність була б
/// видима не як помилка, а як відмова публікації після зеленого прогону.
/// </remarks>
public static class GoldenSet
{
    /// <summary>Звіряє один прогін з очікуваннями тесту.</summary>
    /// <param name="testCase">Тест: входи, очікувані числа й допуск.</param>
    /// <param name="output">Те, що видав рушій.</param>
    /// <returns>Вердикт із переліком розбіжностей.</returns>
    public static TestCaseVerdict Judge(MethodologyTestCase testCase, CalculationOutput output)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(output);

        var mismatches = new List<TestCaseMismatch>();

        foreach (var (code, expected) in testCase.Expected)
        {
            var actual = output.Values.FirstOrDefault(v => v.OutputCode == code);

            // ⛔ Відсутній вихід — це розбіжність, а не «нема з чим порівняти».
            // Методологія, яка перестала рахувати оголошений вихід, мовчки
            // пройшла б набір, якби його відсутність нічого не означала.
            if (actual is null)
            {
                mismatches.Add(new TestCaseMismatch(code, null, expected, testCase.Tolerance));
                continue;
            }

            if (Math.Abs(actual.Value - expected) > testCase.Tolerance)
            {
                mismatches.Add(
                    new TestCaseMismatch(code, actual.Value, expected, testCase.Tolerance));
            }
        }

        return new TestCaseVerdict(testCase.Code, mismatches.Count == 0, mismatches);
    }

    /// <summary>Чи зійшовся весь набір.</summary>
    /// <param name="verdicts">Вердикти всіх тестів версії.</param>
    /// <returns><c>true</c> — набір зелений і версію можна публікувати.</returns>
    /// <remarks>
    /// ⛔ Порожній набір — **НЕ зелений**. «Тестів немає, отже все гаразд» —
    /// саме та підміна, через яку публікація без перевірки виглядає як
    /// публікація з перевіркою (<c>ФВ-9.12</c>).
    /// </remarks>
    public static bool IsGreen(IReadOnlyList<TestCaseVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        return verdicts.Count > 0 && verdicts.All(v => v.IsGreen);
    }
}

/// <summary>Вердикт одного тесту золотого набору.</summary>
/// <param name="Code">Код тесту — те, що потрапляє в повідомлення про провал.</param>
/// <param name="IsGreen">Чи зійшлися всі очікувані числа в межах допуску.</param>
/// <param name="Mismatches">Розбіжності; порожньо, якщо тест зелений.</param>
public sealed record TestCaseVerdict(
    string Code,
    bool IsGreen,
    IReadOnlyList<TestCaseMismatch> Mismatches);

/// <summary>Одна розбіжність: очікували одне, отримали інше.</summary>
/// <param name="OutputCode">Який вихід розійшовся.</param>
/// <param name="Actual">Що вийшло; <c>null</c> — виходу не було взагалі.</param>
/// <param name="Expected">Що мало вийти.</param>
/// <param name="Tolerance">Допуск порівняння; нуль означає точний збіг.</param>
public sealed record TestCaseMismatch(
    string OutputCode,
    decimal? Actual,
    decimal Expected,
    decimal Tolerance);
