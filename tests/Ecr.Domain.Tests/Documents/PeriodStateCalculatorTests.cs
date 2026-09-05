using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Переходи станів періоду і, окремо, **пояс майданчика**: межі рахуються не
/// в UTC, інакше «останній день періоду» настає для користувача в інший час
/// (D-68).
/// </summary>
public sealed class PeriodStateCalculatorTests
{
    private static readonly PeriodStateCalculator Calculator = new();

    /// <summary>Актау: UTC+5 цілий рік, без переходу на літній час.</summary>
    private static readonly TimeZoneInfo Site = TimeZoneInfo.CreateCustomTimeZone(
        "SITE+5", TimeSpan.FromHours(5), "Майданчик UTC+5", "Майданчик UTC+5");

    /// <summary>Січень 2026: відкривається одразу, жорстко закривається 20-го лютого.</summary>
    private static Period January()
    {
        var period = new Period(
            projectId: 1, new PeriodKey(202601), sequence: 1,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        period.RecomputeBoundaries(
            new PeriodPolicy(EcrCode.Create("STD"), openOffsetDays: 0, graceOffsetDays: 5,
                             hardCloseOffsetDays: 20, yearGraceOffsetDays: 45),
            Site);

        return period;
    }

    private static DateTime SiteMidnight(int year, int month, int day)
        => TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified), Site);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public void До_дати_відкриття_період_у_стані_Scheduled()
    {
        var state = Calculator.Calculate(January(), SiteMidnight(2025, 12, 20), Site);

        Assert.Equal(PeriodState.Scheduled, state);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Усередині_періоду_стан_Open()
    {
        var state = Calculator.Calculate(January(), SiteMidnight(2026, 1, 15), Site);

        Assert.Equal(PeriodState.Open, state);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.7")]
    public void Після_завершення_періоду_і_до_HardClose_стан_Grace()
    {
        // 31 січня минуло, 20 лютого ще ні.
        var state = Calculator.Calculate(January(), SiteMidnight(2026, 2, 10), Site);

        Assert.Equal(PeriodState.Grace, state);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.8")]
    public void Після_HardClose_стан_Closed()
    {
        var state = Calculator.Calculate(January(), SiteMidnight(2026, 3, 1), Site);

        Assert.Equal(PeriodState.Closed, state);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public void Reopen_повертає_період_у_Grace_до_вказаного_моменту()
    {
        var period = January();
        var afterClose = SiteMidnight(2026, 3, 1);

        // Назад ходити не можна: період проходить увесь шлях, як його вела б
        // задача станів.
        period.TransitionTo(PeriodState.Open, SiteMidnight(2026, 1, 1));
        period.TransitionTo(PeriodState.Grace, SiteMidnight(2026, 2, 1));
        period.TransitionTo(PeriodState.Closed, afterClose);

        var until = SiteMidnight(2026, 3, 10);
        period.Reopen(until, "уточнення за листом №17", afterClose);

        // ⚠ Тимчасове відкриття ПЕРЕКРИВАЄ розрахунок за offsets: адміністратор
        // відкрив період свідомо і з причиною, і повертати його в Closed за
        // розкладом означало б скасувати рішення людини мовчки.
        Assert.Equal(PeriodState.Grace, Calculator.Calculate(period, SiteMidnight(2026, 3, 5), Site));

        // А після вказаного моменту розрахунок знову бере гору.
        Assert.Equal(PeriodState.Closed, Calculator.Calculate(period, SiteMidnight(2026, 3, 11), Site));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-0.6")]
    public void Межі_рахуються_у_поясі_майданчика_а_не_в_UTC()
    {
        var period = January();

        // Опівніч 1 січня в поясі UTC+5 — це 31 грудня 19:00 UTC.
        Assert.Equal(new DateTime(2025, 12, 31, 19, 0, 0, DateTimeKind.Utc), period.ComputedOpenAt);
        Assert.Equal(new DateTime(2026, 1, 31, 19, 0, 0, DateTimeKind.Utc), period.ComputedGraceAt);
        Assert.Equal(new DateTime(2026, 2, 19, 19, 0, 0, DateTimeKind.Utc), period.ComputedCloseAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Опівночі_за_поясом_майданчика_період_ще_відкритий_хоча_в_UTC_уже_наступна_доба()
    {
        var period = January();

        // 31 січня, 22:00 за майданчиком = 17:00 UTC того ж дня.
        // Але 31 січня 21:00 UTC — це вже 1 лютого 02:00 на майданчику.
        var lateOnLastDay = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 1, 31, 22, 0, 0, DateTimeKind.Unspecified), Site);

        // ⚠ Ось заради чого весь пояс: якби межі рахувалися в UTC, період
        // закрився б для користувача о 19:00 його часу — за п'ять годин до
        // кінця останнього дня, рівно тоді, коли всі дозаповнюють форми.
        Assert.Equal(PeriodState.Open, Calculator.Calculate(period, lateOnLastDay, Site));
        Assert.True(lateOnLastDay.Day == 31);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поточний_період_у_режимі_Auto_це_найраніший_Open()
    {
        var periods = new[]
        {
            Make(202601, PeriodState.Open),
            Make(202602, PeriodState.Open),
            Make(202512, PeriodState.Grace),
        };

        // Найраніший, а не найпізніший: якщо відкриті два періоди, робота йде
        // в тому, що мав закритися раніше — саме він горить.
        Assert.Equal(202601, Calculator.SelectCurrentPeriod(periods)!.PeriodKeyValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Якщо_відкритих_немає_поточним_стає_найпізніший_Grace()
    {
        var periods = new[]
        {
            Make(202511, PeriodState.Grace),
            Make(202512, PeriodState.Grace),
            Make(202510, PeriodState.Closed),
        };

        Assert.Equal(202512, Calculator.SelectCurrentPeriod(periods)!.PeriodKeyValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Якщо_немає_ні_Open_ні_Grace_поточного_періоду_немає()
    {
        var periods = new[] { Make(202510, PeriodState.Closed), Make(202603, PeriodState.Scheduled) };

        // null, а не «останній закритий»: підставити щось «розумне» означало б
        // показувати період, у якому працювати не можна, як поточний.
        Assert.Null(Calculator.SelectCurrentPeriod(periods));
    }

    private static Period Make(int key, PeriodState state)
    {
        var period = new Period(
            projectId: 1, new PeriodKey(key), sequence: (byte)(key % 100),
            new DateOnly(key / 100, key % 100, 1), new DateOnly(key / 100, key % 100, 28));

        var now = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        foreach (var step in Path(state))
        {
            period.TransitionTo(step, now);
        }

        return period;
    }

    /// <summary>Шлях переходів до потрібного стану: назад ходити не можна.</summary>
    private static IEnumerable<PeriodState> Path(PeriodState target)
        => target switch
        {
            PeriodState.Scheduled => [],
            PeriodState.Open => [PeriodState.Open],
            PeriodState.Grace => [PeriodState.Open, PeriodState.Grace],
            _ => [PeriodState.Open, PeriodState.Grace, PeriodState.Closed],
        };
}
