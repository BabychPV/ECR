using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// ФВ-1.8: річне пільгове вікно <c>31.12 + YearGraceOffsetDays</c> тримає
/// періоди року у <c>Grace</c> (запис із позначкою <c>IsLateEdit</c>, ФВ-1.9)
/// до кінця вікна — з D-204 усі, і закриті до 31.12; після вікна — <c>Closed</c>.
/// Системний Reopen закритих — <see cref="YearGraceSystemReopenTests"/>.
/// </summary>
/// <remarks>
/// Політика навмисно коротка (<c>grace 15</c>, <c>hard-close 30</c>): грудень
/// за власними межами закривається 30.01, тобто ВСЕРЕДИНІ 45-денного вікна.
/// Без вікна (контроль у кожному тесті) він у цю мить <c>Closed</c> — інакше
/// тест не відрізнив би вікно від звичайного пільгового строку.
/// </remarks>
public sealed class YearGracePeriodStateTests
{
    private static readonly PeriodStateCalculator Calculator = new();

    /// <summary>UTC+5 без переходу на літній час.</summary>
    private static readonly TimeZoneInfo Site = TimeZoneInfo.CreateCustomTimeZone(
        "SITE+5", TimeSpan.FromHours(5), "Майданчик UTC+5", "Майданчик UTC+5");

    private static readonly DateOnly ProjectEnd = new(2026, 12, 31);

    private static DateTime SiteTime(int year, int month, int day, int hour = 0, int minute = 0)
        => TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), Site);

    private static Period Month(int month, int hardCloseOffsetDays = 30)
    {
        var period = new Period(
            projectId: 1, new PeriodKey((2026 * 100) + month), sequence: (byte)month,
            new DateOnly(2026, month, 1), new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)));

        period.RecomputeBoundaries(
            new PeriodPolicy(EcrCode.Create("SHORT"), openOffsetDays: 0, graceOffsetDays: 15,
                             hardCloseOffsetDays: hardCloseOffsetDays, yearGraceOffsetDays: 45),
            Site);

        return period;
    }

    private static YearGraceWindow Window(int days) => YearGraceWindow.For(ProjectEnd, days, Site);

    /// <remarks>
    /// Мутаційні докази (у <c>PeriodStateCalculator.Calculate</c> /
    /// <c>YearGraceWindow</c>): (1) не питати <c>year.HoldsInGrace(...)</c>
    /// (до D-204 — <c>ExtendClose</c>) → 01.02 дає <c>Closed</c>, червоний;
    /// (2) кінець вікна без останньої доби (<c>endsAt</c> від <c>projectEnd +
    /// N - 1</c>) → 14.02 23:59 дає <c>Closed</c>, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.8")]
    public void Грудень_у_вікні_Grace_після_вікна_Closed()
    {
        var december = Month(12); // власне закриття — 30.01.2027
        var window = Window(45);

        // Контроль: без вікна 01.02 грудень уже закритий.
        Assert.Equal(PeriodState.Closed, Calculator.Calculate(december, SiteTime(2027, 2, 1), Site));

        Assert.Equal(PeriodState.Grace, Calculator.Calculate(december, SiteTime(2027, 2, 1), Site, window));
        Assert.Equal(PeriodState.Grace, Calculator.Calculate(december, SiteTime(2027, 2, 14, 23, 59), Site, window));
        Assert.Equal(PeriodState.Closed, Calculator.Calculate(december, SiteTime(2027, 2, 15), Site, window));

        // До власного кінця пільгового строку вікно нічого не посилює: Open лишається Open.
        Assert.Equal(PeriodState.Open, Calculator.Calculate(december, SiteTime(2027, 1, 10), Site, window));
    }

    /// <remarks>
    /// ⚠ D-204 (варіант «б», рішення людини 2026-09-28): вікно тримає в
    /// <c>Grace</c> УСІ періоди року, і ті, що закрилися до 31.12. Мутація:
    /// повернути старе правило (лише незакриті на 31.12) → січень 01.02 дає
    /// <c>Closed</c>, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.8")]
    public void Вікно_тримає_в_Grace_усі_періоди_року_і_закриті_до_31_12()
    {
        var window = Window(45);
        var now = SiteTime(2027, 2, 1);

        // Листопад із довгим жорстким закриттям (30.11 + 45 = 14.01.2027): на
        // 31.12 ще не закрився → вікно тримає його в Grace.
        var november = Month(11, hardCloseOffsetDays: 45);
        Assert.Equal(PeriodState.Closed, Calculator.Calculate(november, now, Site));
        Assert.Equal(PeriodState.Grace, Calculator.Calculate(november, now, Site, window));

        // Січень закрився ще в березні 2026 за власними межами — у вікні року
        // він теж Grace (контроль без вікна — Closed), а після вікна — Closed.
        Assert.Equal(PeriodState.Closed, Calculator.Calculate(Month(1), now, Site));
        Assert.Equal(PeriodState.Grace, Calculator.Calculate(Month(1), now, Site, window));
        Assert.Equal(PeriodState.Closed, Calculator.Calculate(Month(1), SiteTime(2027, 2, 15), Site, window));

        // До кінця року вікно нічого не відкриває: 20.12 січень закритий.
        Assert.Equal(PeriodState.Closed, Calculator.Calculate(Month(1), SiteTime(2026, 12, 20), Site, window));
    }

    /// <remarks>
    /// Мутація: <c>YearGraceWindow.For</c> ігнорує <c>yearGraceOffsetDays</c>
    /// (літерал 45) → 20.02 дає <c>Closed</c>, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.8")]
    public void Проєкт_з_offset_90_має_інше_вікно()
    {
        var december = Month(12);
        var window = Window(90); // 31.12 + 90 → останній день 31.03.2027

        Assert.Equal(new DateOnly(2027, 3, 31), window.LastDay);

        // 20.02: для +45 вже закрито, для +90 — ще Grace.
        Assert.Equal(PeriodState.Closed, Calculator.Calculate(december, SiteTime(2027, 2, 20), Site, Window(45)));
        Assert.Equal(PeriodState.Grace, Calculator.Calculate(december, SiteTime(2027, 2, 20), Site, window));
        Assert.Equal(PeriodState.Grace, Calculator.Calculate(december, SiteTime(2027, 3, 31, 23, 59), Site, window));
        Assert.Equal(PeriodState.Closed, Calculator.Calculate(december, SiteTime(2027, 4, 1), Site, window));
    }

    /// <remarks>
    /// Рішення про запис (<see cref="PeriodStateCalculator.Effective"/>) і план
    /// задачі станів беруть те саме вікно. Мутація: <c>Effective</c> не передає
    /// <c>yearGrace</c> у <c>Calculate</c> → рішення 01.02 дає <c>Closed</c>
    /// для збереженого <c>Grace</c>, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.8")]
    public void Задача_і_рішення_про_запис_бачать_одне_вікно_і_ту_саму_позначку_Grace()
    {
        var window = Window(45);
        var december = Month(12);
        december.AdvanceTo(PeriodState.Grace, SiteTime(2027, 1, 15));

        // План на 01.02 — нічого не закривати; після вікна — закрити.
        Assert.Empty(Calculator.PlanTransitions([december], SiteTime(2027, 2, 1), Site, window).Transitions);
        var close = Assert.Single(Calculator.Plan([december], SiteTime(2027, 2, 15), Site, window));
        Assert.Equal(PeriodState.Closed, close.Target);

        // Рішення про запис: у вікні — Grace, після — Closed; без вікна — Closed.
        Assert.Equal(PeriodState.Grace, Calculator.Effective(december, SiteTime(2027, 2, 1), window));
        Assert.Equal(PeriodState.Closed, Calculator.Effective(december, SiteTime(2027, 2, 1)));
        Assert.Equal(PeriodState.Closed, Calculator.Effective(december, SiteTime(2027, 2, 15), window));

        // ФВ-1.9: той самий Grace — запис дозволено, з позначкою пізньої правки.
        Assert.True(december.AllowsEditing);
        Assert.True(december.IsLateEditWindow);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.8")]
    public void Нульовий_offset_вікна_не_дає()
    {
        var window = Window(0);

        Assert.Equal(window.YearEndUtc, window.EndsAtUtc);
        Assert.False(window.Contains(SiteTime(2027, 1, 1)));
        Assert.Equal(PeriodState.Closed, Calculator.Calculate(Month(12), SiteTime(2027, 2, 1), Site, window));
    }
}
