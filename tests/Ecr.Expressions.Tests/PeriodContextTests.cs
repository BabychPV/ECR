using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Календарна конвенція (D-78).
/// </summary>
/// <remarks>
/// Різниця між режимами на тих самих даних — **3.3 %** (фікстура 02c §7), і
/// виглядає вона як помилка формули, а не як різниця конвенції. Тому тест на
/// обидва режими обов'язковий.
/// </remarks>
public sealed class PeriodContextTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Actual_дає_фактичну_кількість_днів_у_січні()
    {
        Assert.Equal(31, Month(2026, 1, CalendarMode.Actual).Days);

        // І 28 у лютому невисокосного, і 29 — у високосному. Саме це «і»
        // відрізняє Actual від решти: він не має жодного числа, взятого
        // наперед.
        Assert.Equal(28, Month(2026, 2, CalendarMode.Actual).Days);
        Assert.Equal(29, Month(2028, 2, CalendarMode.Actual).Days);
        Assert.Equal(30, Month(2026, 4, CalendarMode.Actual).Days);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.11")]
    public void Fixed360_дає_тридцять_днів_у_будь_якому_місяці()
    {
        // ⚠ Однакове число для всіх місяців — і є сенс режиму: місячні звіти
        // стають порівнюваними між собою. У Actual січень «більший» за
        // лютий на 10 %, і питомі показники стрибають самі собою.
        Assert.Equal(30, Month(2026, 1, CalendarMode.Fixed360).Days);
        Assert.Equal(30, Month(2026, 2, CalendarMode.Fixed360).Days);
        Assert.Equal(30, Month(2028, 2, CalendarMode.Fixed360).Days);

        // Квартал — рівно три однакові місяці, а не «90 днів за домовленістю».
        Assert.Equal(90, Period(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), CalendarMode.Fixed360).Days);

        // Рік = 360, а не 365 і не 366.
        Assert.Equal(360, Year(2028, CalendarMode.Fixed360).Days);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Fixed365_дає_рік_у_365_днів_навіть_у_високосному()
    {
        // 2028 — високосний: фактично 366 днів.
        Assert.Equal(366, Year(2028, CalendarMode.Actual).Days);
        Assert.Equal(365, Year(2028, CalendarMode.Fixed365).Days);
        Assert.Equal(365, Year(2026, CalendarMode.Fixed365).Days);

        // ⚠ Fixed365 фіксує лише РІК. Місяць у ньому лишається фактичним —
        // інакше сума дванадцяти місяців не дорівнювала б року, і зведений
        // звіт не сходився б із помісячними при жодному округленні.
        Assert.Equal(31, Month(2026, 1, CalendarMode.Fixed365).Days);
        Assert.Equal(29, Month(2028, 2, CalendarMode.Fixed365).Days);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.11a")]
    public void Секунди_періоду_це_дні_помножені_на_86400()
    {
        var january = Month(2026, 1, CalendarMode.Actual);

        Assert.Equal(31 * 86400L, january.Seconds);
        Assert.Equal(2_678_400L, january.Seconds);
        Assert.Equal(31 * 24, january.Hours);

        // ⚠ Секунд рівно 86400 на добу: переходи на літній час не враховуються.
        // Це не спрощення — це вимога звіряння: місячний звіт, у якому один
        // місяць має 86400×31 секунд, а інший 86400×31−3600, неможливо
        // порівняти з тим самим місяцем минулого року.
        Assert.Equal(360 * 86400L, Year(2028, CalendarMode.Fixed360).Seconds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Перерахунок_у_грами_за_секунду_відрізняється_між_Actual_і_Fixed360()
    {
        // Фікстура 02c §7, рядок 7001001, SUB-COD: 912.6875 kg за січень 2026.
        const decimal massGrams = 912_687.5m;

        var actual = massGrams / Month(2026, 1, CalendarMode.Actual).Seconds;
        var fixed360 = massGrams / Month(2026, 1, CalendarMode.Fixed360).Seconds;

        // ⚠ 0.340758, а НЕ 0.340774, як написано в 02c §7. Число фікстури
        // суперечить її ж вхідним даним: 912687.5 / 2678400 = 0.3407584…
        // Fixed360 у тому самому місці порахований правильно, і саме тому
        // помилку видно — обидва не могли б збігтися з одним дільником.
        // Записано як P-06; змінюється одним числом, якщо рішення буде інше.
        Assert.Equal(0.340758m, decimal.Round(actual, 6, MidpointRounding.AwayFromZero));
        Assert.Equal(0.352117m, decimal.Round(fixed360, 6, MidpointRounding.AwayFromZero));

        // ⛔ 3.3 % на тих самих вхідних даних. Ось заради чого CalendarMode
        // існує як явне поле версії методології (D-78): без нього розбіжність
        // виглядає як помилка формули, і її шукають у формулі — там, де її
        // немає.
        // Відношення точне: 31/30. Саме тому «3.3 %» у документі правильні
        // навіть із хибним gsec_Actual — помилка сховалася за округленням.
        var difference = (fixed360 - actual) / actual;
        Assert.Equal(0.033m, decimal.Round(difference, 3, MidpointRounding.AwayFromZero));
        Assert.Equal(
            decimal.Round(31m / 30m, 20),
            decimal.Round(fixed360 / actual, 20));
    }

    private static PeriodContext Month(int year, int month, CalendarMode mode)
        => new(
            new DateOnly(year, month, 1),
            new DateOnly(year, month, DateTime.DaysInMonth(year, month)),
            mode,
            year,
            (byte)month);

    private static PeriodContext Year(int year, CalendarMode mode)
        => Period(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), mode);

    private static PeriodContext Period(DateOnly start, DateOnly end, CalendarMode mode)
        => new(start, end, mode, start.Year, 1);
}
