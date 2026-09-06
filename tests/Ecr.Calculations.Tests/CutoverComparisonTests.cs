using Ecr.Calculations;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Критерій приймання нового рушія (<c>ФВ-9.16</c>, <c>E-6</c>, <c>H-24d-2</c>).
/// </summary>
/// <remarks>
/// ⛔ До цього класу критерій жив **лише у словах**, і слова були нездійсненні:
/// <c>ФВ-9.9</c> вимагав чисел, «побітово сумісних з чинною системою», а чинна
/// система рахує в <c>double</c>. Побайтна рівність двох різних кодових шляхів
/// на <c>double</c> недосяжна в принципі — тобто вимога гарантувала червоний
/// звіт на кожному рядку, а такий звіт перестають відкривати.
///
/// ⚠ Другий бік тієї самої проблеми: звіт мав **дві** категорії, а чинна
/// система має тихі дірки (порожній <c>catch</c>, вихід зі ста ітерацій без
/// результату). Наші числа в цих місцях лягли б у «розбіжність» поряд із
/// нашими ж дефектами.
/// </remarks>
public sealed class CutoverComparisonTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    public void Розбіжність_нижче_подання_не_блокує_cutover()
    {
        // ⛔ Головне твердження E-6. Сімнадцятий знак `double`-джерела ніколи
        // не потрапляє людині на очі, і вимагати його збігу означає вимагати
        // неможливого.
        var verdict = CutoverComparison.Compare(
            legacy: 1.2340001m, actual: 1.2339998m, displayFormat: "#,##0.00");

        Assert.Equal(CutoverCategory.ExplainCaseByCase, verdict.Category);
        Assert.Equal(1.23m, verdict.RoundedLegacy);
        Assert.Equal(1.23m, verdict.RoundedActual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    public void Різниця_по_різні_боки_межі_округлення_блокує()
    {
        // ⛔ Чесна межа критерію, і її треба знати наперед. Різниця в
        // 0.0000009 — на дев'ять порядків менша за подання — усе одно блокує,
        // якщо числа лягли по РІЗНІ боки межі: у формі людина побачить 1.23 і
        // 1.24, а це різні числа, хай і від сімнадцятого знака.
        //
        // ⚠ Це не дефект критерію, а його зміст: E-6 звіряє те, що видно.
        // Таких випадків у звіті чекати треба, і кожен іде в покейсний розбір
        // руками — але через категорію «блокує», а не повз неї.
        var verdict = CutoverComparison.Compare(
            legacy: 1.2349995m, actual: 1.2350004m, displayFormat: "#,##0.00");

        Assert.Equal(CutoverCategory.Blocking, verdict.Category);
        Assert.Equal(1.23m, verdict.RoundedLegacy);
        Assert.Equal(1.24m, verdict.RoundedActual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    public void Розбіжність_у_поданні_блокує_cutover()
    {
        // ⚠ Зворотний бік: послаблення критерію не робить його беззубим.
        // Там, де число інакше видно у формі, — це блокування, без розборів.
        var verdict = CutoverComparison.Compare(
            legacy: 1.234m, actual: 1.236m, displayFormat: "#,##0.00");

        Assert.Equal(CutoverCategory.Blocking, verdict.Category);
        Assert.False(CutoverComparison.Summarize([verdict]).AllowsCutover);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    public void Точний_збіг_не_подається_як_пояснення()
    {
        // Рівні числа не мають потрапляти в «розібрати покейсно»: інакше звіт
        // покейсного розбору стане завдовжки в увесь корпус.
        var verdict = CutoverComparison.Compare(1.23m, 1.23m, "#,##0.00");

        Assert.Equal(CutoverCategory.Match, verdict.Category);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    public void Тиша_чинної_системи_окрема_категорія_і_не_блокує()
    {
        // ⛔ `H-24d-2`. Чинна система мовчить — ми рахуємо. Це покращення, і в
        // звіті воно має стояти окремо, інакше хтось тиждень доводитиме, що це
        // не наш дефект.
        var verdict = CutoverComparison.Compare(
            legacy: null, actual: 42.5m, displayFormat: "#,##0.00");

        Assert.Equal(CutoverCategory.LegacySilent, verdict.Category);
        Assert.True(CutoverComparison.Summarize([verdict]).AllowsCutover);
        Assert.Contains("не дала результату", CutoverComparison.Describe(verdict),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    public void Втрата_результату_нашим_рушієм_покращенням_не_є()
    {
        // ⚠ Симетрії тут немає і бути не повинно: чинна система дала число, ми
        // не дали — це втрата, і вона блокує. Порахувати її «тишею еталона»
        // означало б сховати найгірший з можливих регресів.
        var verdict = CutoverComparison.Compare(
            legacy: 42.5m, actual: null, displayFormat: "#,##0.00");

        Assert.Equal(CutoverCategory.Blocking, verdict.Category);
        Assert.Contains("нічого", CutoverComparison.Describe(verdict), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Мовчать_обидва_це_збіг_а_не_рядок_звіту()
    {
        var verdict = CutoverComparison.Compare(null, null, "#,##0.00");

        Assert.Equal(CutoverCategory.Match, verdict.Category);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16a")]
    public void Округлення_подання_від_нуля_а_не_банківське()
    {
        // ⛔ 1.225 за банківським правилом дало б 1.22, за `AwayFromZero` —
        // 1.23. Різниця видима лише тут: із банківським цей рядок став би
        // «блокує cutover» на рівному місці, і шукали б його у формулі.
        var verdict = CutoverComparison.Compare(
            legacy: 1.225m, actual: 1.2300m, displayFormat: "#,##0.00");

        Assert.Equal(1.23m, verdict.RoundedLegacy);
        Assert.Equal(CutoverCategory.ExplainCaseByCase, verdict.Category);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    public void Без_формату_подання_береться_розрядність_колонки()
    {
        var verdict = CutoverComparison.Compare(
            legacy: 1.234m, actual: 1.236m, displayFormat: null, columnScale: 1);

        Assert.Equal(1, verdict.Scale);
        Assert.Equal(CutoverCategory.ExplainCaseByCase, verdict.Category);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    public void Без_формату_і_без_розрядності_звіряються_числа_як_є()
    {
        // ⚠ Невідома розрядність НЕ підміняється нулем: інакше 1.4 і 0.6 стали
        // б «збігом у поданні». Колонка без формату має бути помічена гучно.
        var verdict = CutoverComparison.Compare(1.0000001m, 1.0000002m, null);

        Assert.Null(verdict.Scale);
        Assert.Equal(CutoverCategory.Blocking, verdict.Category);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    [InlineData("#,##0.00", 2)]
    [InlineData("#,##0.000", 3)]
    [InlineData("#,##0", 0)]
    [InlineData("0", 0)]
    [InlineData("0.0###", 4)]
    // Відсоток множить на сто: `0.00%` подає ЧОТИРИ знаки вихідного числа.
    [InlineData("0.00%", 4)]
    [InlineData("0.0‰", 4)]
    // Літерал у лапках — текст, а не розряди.
    [InlineData("0.0\" т\"", 1)]
    [InlineData("#,##0.00_);[Red]\\(#,##0.00\\)", 2)]
    public void Розрядність_подання_читається_з_формату(string format, int expected)
        => Assert.Equal(expected, CutoverComparison.ScaleOf(format));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("General")]
    // ⛔ Науковий запис сталої розрядності не задає: `0.00E+00` подає різну
    // точність для 1e3 і 1e-9. Порахувати тут «два» означало б вигадати межу.
    [InlineData("0.00E+00")]
    // Кома в кінці — масштабування на тисячі; ми його не рахуємо і кажемо це.
    [InlineData("#,##0,")]
    [InlineData("\"тонн\"")]
    public void Формат_який_не_задає_розрядності_дає_невідомо(string? format)
        => Assert.Null(CutoverComparison.ScaleOf(format));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    public void Порожня_звірка_дозволу_на_cutover_не_дає()
    {
        // ⛔ «Звіряти не було чого, отже все гаразд» — та сама підміна, через
        // яку публікація без тестів виглядала б як публікація з тестами
        // (`ФВ-9.12`). Cutover без жодного звіреного числа — не cutover.
        Assert.False(CutoverComparison.Summarize([]).AllowsCutover);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    public void Підсумок_рахує_три_категорії_окремо()
    {
        var verdicts = new[]
        {
            CutoverComparison.Compare(1.23m, 1.23m, "#,##0.00"),
            CutoverComparison.Compare(1.2341m, 1.2339m, "#,##0.00"),
            CutoverComparison.Compare(null, 7m, "#,##0.00"),
            CutoverComparison.Compare(1m, 2m, "#,##0.00"),
        };

        var summary = CutoverComparison.Summarize(verdicts);

        Assert.Equal(new CutoverSummary(1, 1, 1, 1), summary);
        Assert.Equal(4, summary.Total);
        Assert.False(summary.AllowsCutover);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16")]
    public void Пояснення_і_тиша_разом_cutover_не_блокують()
    {
        // ⚠ Це і є здійсненність критерію: розділ, де всі розбіжності — або
        // нижче подання, або тиша еталона, проходить. Із «побітово» він не
        // пройшов би ніколи.
        var verdicts = new[]
        {
            CutoverComparison.Compare(1.2341m, 1.2339m, "#,##0.00"),
            CutoverComparison.Compare(null, 7m, "#,##0.00"),
            CutoverComparison.Compare(5m, 5m, "#,##0.00"),
        };

        Assert.True(CutoverComparison.Summarize(verdicts).AllowsCutover);
    }
}
