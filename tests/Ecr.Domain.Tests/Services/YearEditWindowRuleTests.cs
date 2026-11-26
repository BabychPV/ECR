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
/// МУТАЦІЯ (локально, 26.11): рік з кінця проєкту замість року екземпляра — червоний «Вікно_рахується_від_року…»; кінець вікна без
/// зміщення (<c>yearGraceOffsetDays</c> → 0) — червоні «Після_31_12…» і «Останній_день…».
/// </remarks>
public sealed class YearEditWindowRuleTests
{
    private static readonly DateOnly End = new(2026, 12, 31);

    // Asia/Atyrau = UTC+05:00 без переходів: опівніч 01.01.2026 майданчика = 31.12.2025 19:00 UTC.
    private static readonly TimeZoneInfo Site = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");

    [Fact]
    [Trait("Finding", "b14-document-move-P2")]
    public void До_початку_року_правка_вже_відкрита_людина_26_11()
    {
        Assert.Equal(PeriodState.Open, State(new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc)));   // «просто таблиця»
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

    [Fact]
    [Trait("Finding", "b14-document-move-L5")]
    public void Вікно_рахується_від_року_екземпляра_а_не_від_проєкту()
    {
        // Екземпляр 2025 у червні 2026: рік + 45 днів минули — закрито (рахунок від кінця проєкту 2026 дав би Open).
        Assert.Equal(PeriodState.Closed, YearEditWindowRule.State(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), 2025, 45, Site));
        Assert.Equal(PeriodState.Open, YearEditWindowRule.State(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), 2026, 45, Site));
    }

    private static PeriodState State(DateTime nowUtc) => YearEditWindowRule.State(nowUtc, 2026, 45, Site);
}
