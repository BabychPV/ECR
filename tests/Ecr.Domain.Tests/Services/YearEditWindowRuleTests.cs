// tests/Ecr.Domain.Tests/Services/YearEditWindowRuleTests.cs
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Xunit;

namespace Ecr.Domain.Tests.Services;

/// <summary>
/// b14 document-move (D-577): «вікно редагування = рік проєкту + зміщення» (людина 25.11) — межі в поясі майданчика,
/// ті самі, що річне пільгове вікно (<see cref="YearGraceWindow"/>) і ворота архівації.
/// </summary>
/// <remarks>
/// МУТАЦІЯ (локально, 26.11): «до року — Open» замість Scheduled — червоний «До_початку_року…»; кінець вікна без
/// зміщення (<c>yearGraceOffsetDays</c> → 0) — червоні «Після_31_12…» і «Останній_день…».
/// </remarks>
public sealed class YearEditWindowRuleTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly End = new(2026, 12, 31);

    // Asia/Atyrau = UTC+05:00 без переходів: опівніч 01.01.2026 майданчика = 31.12.2025 19:00 UTC.
    private static readonly TimeZoneInfo Site = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");

    [Fact]
    [Trait("Finding", "b14-document-move-P2")]
    public void До_початку_року_правка_ще_не_відкрита()
    {
        Assert.Equal(PeriodState.Scheduled, State(new DateTime(2025, 12, 31, 18, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(PeriodState.Open, State(new DateTime(2025, 12, 31, 19, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    [Trait("Finding", "b14-document-move-P2")]
    public void Увесь_рік_відкрито_незалежно_від_місяців()
    {
        Assert.Equal(PeriodState.Open, State(new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(PeriodState.Open, State(new DateTime(2026, 12, 31, 18, 59, 59, DateTimeKind.Utc)));
    }

    [Fact]
    [Trait("Finding", "b14-document-move-P2")]
    public void Після_31_12_до_кінця_зміщення_пізня_правка()
    {
        Assert.Equal(PeriodState.Grace, State(new DateTime(2026, 12, 31, 19, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(PeriodState.Grace, State(new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    [Trait("Finding", "b14-document-move-P2")]
    public void Останній_день_зміщення_включно_далі_закрито()
    {
        // 31.12.2026 + 45 → останній день 14.02.2027 включно, вікно закривається опівночі 15.02 за майданчиком.
        Assert.Equal(PeriodState.Grace, State(new DateTime(2027, 2, 14, 18, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(PeriodState.Closed, State(new DateTime(2027, 2, 14, 19, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    [Trait("Finding", "b14-document-move-P2")]
    public void Вікно_збігається_з_воротами_архівації()
    {
        var window = YearGraceWindow.For(End, 45, Site);

        Assert.Equal(PeriodState.Grace, State(window.EndsAtUtc.AddTicks(-1)));
        Assert.Equal(PeriodState.Closed, State(window.EndsAtUtc));
    }

    private static PeriodState State(DateTime nowUtc) => YearEditWindowRule.State(nowUtc, Start, End, 45, Site);
}
