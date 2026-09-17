using System.Globalization;
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
///
/// ⛔ <b>Звірка міряла лише ПЕРШУ речовину.</b> Тут стояло
/// <c>output.Values.FirstOrDefault(v =&gt; v.OutputCode == code)</c>, а
/// <see cref="CalculationOutput.Values"/> іде ПО ОДНОМУ РЯДКУ НА
/// (речовина × вихід). Отже для методології, що рахує вихід по десятку
/// речовин, звірялася рівно одна з них — та, яка трапилася в списку першою;
/// решта могли бути якими завгодно, і версія публікувалася зеленою. Для
/// системи, найтвердіша вимога якої — «числа мусять збігатися з чинними»,
/// сторож, що дивиться на одну речовину з N, гірший за відсутній: він займає
/// місце перевірки, якої через нього ніхто вже не напише. Саме цей мовчазний
/// пропуск і стояв за екраном «Різниці на золотому наборі — жодне число не
/// змінилося».
/// </remarks>
public static class GoldenSet
{
    /// <summary>
    /// Роздільник коду виходу й речовини в ключі очікування:
    /// <c>tons@901</c> — «вихід <c>tons</c> речовини 901».
    /// </summary>
    /// <remarks>
    /// ⚠ У коді виходу цього символу бути не може за побудовою
    /// (<see cref="Ecr.Domain.ValueObjects.EcrCode.Pattern"/> — літери, цифри
    /// й підкреслення), тож ключ розбирається однозначно.
    ///
    /// ⚠ Ключ БЕЗ речовини лишився законним і означає «кожен рядок цього
    /// виходу», а не «перший». Стара форма набору (<c>{"tons": 0.9}</c>) через
    /// це не перестала працювати — навпаки, вона вперше почала перевіряти те,
    /// що обіцяла: усі речовини виходу, а не одну.
    /// </remarks>
    public const char SubstanceSeparator = '@';

    /// <summary>Звіряє один прогін з очікуваннями тесту.</summary>
    /// <param name="testCase">Тест: входи, очікувані числа й допуск.</param>
    /// <param name="output">Те, що видав рушій.</param>
    /// <returns>Вердикт із переліком розбіжностей.</returns>
    /// <exception cref="InvalidOperationException">
    /// Ключ очікування називає речовину, яка не є цілим числом. Мовчки звести
    /// такий ключ до «виходу без речовини» означало б перевіряти не те, що
    /// оголошено, — і зелений набір знову нічого б не доводив.
    /// </exception>
    /// <remarks>
    /// ⚠ Форма проходу — НЕ «запит на кожне очікування». Результати
    /// групуються один раз (лінійно за довжиною <c>output.Values</c>), і далі
    /// кожне очікування бере свою групу за ключем. Звірка йде при публікації
    /// на всьому наборі, тож <c>FirstOrDefault</c> у циклі — це
    /// (речовини × виходи)² порівнянь на КОЖЕН випадок набору.
    /// </remarks>
    public static TestCaseVerdict Judge(MethodologyTestCase testCase, CalculationOutput output)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(output);

        var mismatches = new List<TestCaseMismatch>();

        // Один прохід будує обидва покажчики: «всі рядки виходу» — для
        // очікування без речовини, «рядки виходу цієї речовини» — для
        // поречовинного. Списками, а не одним значенням: дубль (вихід,
        // речовина) в результаті звіриться, а не сховається за «першим».
        var byOutput = new Dictionary<string, List<CalculationOutputValue>>(StringComparer.Ordinal);
        var bySubstance = new Dictionary<(string Output, int Substance), List<CalculationOutputValue>>();

        foreach (var value in output.Values)
        {
            Add(byOutput, value.OutputCode, value);

            if (value.SubstanceEntryId is { } entry)
            {
                Add(bySubstance, (value.OutputCode, entry), value);
            }
        }

        foreach (var (declaration, expected) in testCase.Expected)
        {
            var (code, substance) = ParseKey(declaration, testCase.Code);

            var actual = substance is { } entry
                ? Find(bySubstance, (code, entry))
                : Find(byOutput, code);

            // ⛔ Відсутній вихід — це розбіжність, а не «нема з чим
            // порівняти». Методологія, яка перестала рахувати оголошений
            // вихід — або оголошену РЕЧОВИНУ цього виходу, — мовчки пройшла б
            // набір, якби її відсутність нічого не означала.
            if (actual.Count == 0)
            {
                mismatches.Add(new TestCaseMismatch(code, substance, null, expected, testCase.Tolerance));
                continue;
            }

            // ⛔ Кожен рядок групи, а не перший: саме тут жила вада. Одне
            // очікування без речовини стосується ВСІХ речовин виходу, і
            // розійтися може будь-яка з них.
            foreach (var value in actual)
            {
                if (Math.Abs(value.Value - expected) > testCase.Tolerance)
                {
                    mismatches.Add(new TestCaseMismatch(
                        code, value.SubstanceEntryId, value.Value, expected, testCase.Tolerance));
                }
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

    /// <summary>Перелік розбіжностей набору — по рядку на кожну.</summary>
    /// <param name="verdicts">Вердикти всіх тестів версії.</param>
    /// <returns>Рядки виду «випадок: вихід (речовина): очікували … отримали …».</returns>
    /// <remarks>
    /// ⛔ Перелік, а не лічба. «Знайдено проблем — 3» не дає зробити нічого:
    /// невідомо ні яка речовина розійшлася, ні наскільки, ні в який бік — а
    /// саме це методолог і мусить побачити, щоб виправити формулу.
    /// </remarks>
    public static IReadOnlyList<string> Divergences(IReadOnlyList<TestCaseVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        return
        [
            .. verdicts
                .Where(v => !v.IsGreen)
                .SelectMany(v => v.Mismatches.Select(m => $"{v.Code}: {m.Describe()}")),
        ];
    }

    /// <summary>Додає значення до групи, створюючи її за потреби.</summary>
    private static void Add<TKey>(
        Dictionary<TKey, List<CalculationOutputValue>> index, TKey key, CalculationOutputValue value)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var group))
        {
            group = [];
            index[key] = group;
        }

        group.Add(value);
    }

    /// <summary>Група значень за ключем; порожня, якщо такої немає.</summary>
    private static List<CalculationOutputValue> Find<TKey>(
        Dictionary<TKey, List<CalculationOutputValue>> index, TKey key)
        where TKey : notnull
        => index.TryGetValue(key, out var group) ? group : [];

    /// <summary>Розбирає ключ очікування на вихід і (необов'язкову) речовину.</summary>
    /// <param name="declaration">Ключ: <c>tons</c> або <c>tons@901</c>.</param>
    /// <param name="testCode">Код тесту — щоб зіпсований ключ було де шукати.</param>
    /// <returns>Код виходу і речовина; <c>null</c> — «усі речовини виходу».</returns>
    private static (string Code, int? Substance) ParseKey(string declaration, string testCode)
    {
        var separator = declaration.IndexOf(SubstanceSeparator, StringComparison.Ordinal);
        if (separator < 0)
        {
            return (declaration, null);
        }

        var code = declaration[..separator];
        var substance = declaration[(separator + 1)..];

        // ⛔ Зіпсований ключ кидає, а не стає «виходом без речовини». Тихе
        // зведення означало б, що набір перевіряє не те, що в ньому написано:
        // очікування для однієї речовини мовчки поширилося б на всі — або, що
        // гірше, на жодну.
        if (code.Length == 0
            || !int.TryParse(substance, NumberStyles.Integer, CultureInfo.InvariantCulture, out var entry))
        {
            throw new InvalidOperationException(
                $"Тест «{testCode}»: ключ очікування «{declaration}» не читається. "
                + $"Очікується «код_виходу» або «код_виходу{SubstanceSeparator}ідентифікатор_речовини».");
        }

        return (code, entry);
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
/// <param name="SubstanceEntryId">
/// Чия саме це величина; <c>null</c> — вихід без речовини.
/// ⛔ Поля не було, і без нього звіт не можна було прочитати: розбіжності
/// десяти речовин одного виходу виглядали як десять однакових рядків.
/// </param>
/// <param name="Actual">Що вийшло; <c>null</c> — виходу не було взагалі.</param>
/// <param name="Expected">Що мало вийти.</param>
/// <param name="Tolerance">Допуск порівняння; нуль означає точний збіг.</param>
public sealed record TestCaseMismatch(
    string OutputCode,
    int? SubstanceEntryId,
    decimal? Actual,
    decimal Expected,
    decimal Tolerance)
{
    /// <summary>Рядок звіту — з речовиною, обома числами й допуском.</summary>
    /// <returns>Опис, стабільний і не залежний від локалі.</returns>
    public string Describe()
    {
        var culture = CultureInfo.InvariantCulture;

        var substance = SubstanceEntryId is { } entry
            ? $"речовина {entry.ToString(culture)}"
            : "без речовини";

        var actual = Actual is { } value
            ? value.ToString(culture)
            : "виходу не було";

        return $"{OutputCode} ({substance}): очікували {Expected.ToString(culture)} "
               + $"± {Tolerance.ToString(culture)}, отримали {actual}";
    }
}
