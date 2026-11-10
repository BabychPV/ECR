using System.Globalization;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// x9 (10.11): чи може задача станів (<c>PeriodStateJob</c>) перевести період НАЗАД. Задача застосовує рівно два
/// списки плану <see cref="PeriodStateCalculator.PlanTransitions"/>: <c>Transitions</c> — через
/// <see cref="Period.AdvanceTo"/>, <c>YearReopens</c> — через <see cref="Period.Reopen"/>. Тест перебирає збережений
/// стан (зокрема перевідкритий вручну), зміну політики ПІСЛЯ того, як стан уже встановлено, вікно року (немає/0/45/400
/// днів) і момент прогону в будь-якому порядку (годинник уперед і назад), застосовує план так само, як задача, і
/// перевіряє: стан ніколи не йде назад, окрім <c>Closed → Grace</c> системного Reopen D-204 — лише для періоду року
/// проєкту і лише поки триває вікно.
/// </summary>
/// <remarks>
/// Висновок x9: дефекту немає. Зворотний перехід, якого вимагав би розрахунок (подовжили пільговий строк, відсунули
/// відкриття, годинник назад, минув дедлайн ручного Reopen), іде в <c>Skipped</c> і лишає стан; назад періоди переводить
/// лише людина — Reopen або <c>PeriodStateCatchUp</c> (<see cref="Period.RederiveTo"/>, зміна політики, F13 e1), не задача.
/// Мутаційний доказ (локально): у <c>PlanTransitions</c> прибрати умову <c>year.HoldsInGrace(period, utcNow)</c> гілки
/// D-204 — закритий період зі зсунутим після зміни політики закриттям «перевідкривається» поза вікном, червоний.
/// </remarks>
public sealed class PeriodStateNoBackwardTests
{
    private static readonly PeriodStateCalculator Calculator = new();

    private static readonly TimeZoneInfo Site = TimeZoneInfo.CreateCustomTimeZone(
        "SITE+5", TimeSpan.FromHours(5), "Майданчик UTC+5", "Майданчик UTC+5");

    private static readonly DateOnly ProjectEnd = new(2026, 12, 31);

    private static readonly DateTime SetAt = Utc(2026, 1, 1);

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Як потрапили у збережений стан.</summary>
    public enum Stored
    {
        Scheduled,
        Open,
        Grace,
        Closed,
        ReopenedUntilJanuary,
        ReopenedUntilSummer,
    }

    private static Period Make(int month, Stored stored)
    {
        var period = new Period(
            projectId: 1, new PeriodKey((2026 * 100) + month), sequence: (byte)month,
            new DateOnly(2026, month, 1), new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)));

        // Межі на момент встановлення стану — не ті, з якими буде прогін (зміна політики нижче).
        period.RecomputeBoundaries(new PeriodPolicy(EcrCode.Create("SET"), 0, 15, 30, 45), Site);

        switch (stored)
        {
            case Stored.Scheduled:
                break;
            case Stored.Open:
                period.AdvanceTo(PeriodState.Open, SetAt);
                break;
            case Stored.Grace:
                period.AdvanceTo(PeriodState.Grace, SetAt);
                break;
            case Stored.Closed:
                period.AdvanceTo(PeriodState.Closed, SetAt);
                break;
            case Stored.ReopenedUntilJanuary:
                period.AdvanceTo(PeriodState.Closed, SetAt);
                period.Reopen(Utc(2027, 1, 15), "ручне відкриття", SetAt);
                break;
            case Stored.ReopenedUntilSummer:
                period.AdvanceTo(PeriodState.Closed, SetAt);
                period.Reopen(Utc(2027, 6, 1), "ручне відкриття", SetAt);
                break;
        }

        return period;
    }

    private static readonly (int Open, int Grace, int Close)[] Policies =
    [
        (0, 5, 10), (0, 5, 30), (0, 5, 90), (0, 15, 30), (0, 15, 90), (0, 60, 90),
        (20, 5, 10), (20, 5, 30), (20, 5, 90), (20, 15, 30), (20, 15, 90), (20, 60, 90),
    ];

    private static readonly int?[] Windows = [null, 0, 45, 400];

    private static readonly DateTime[] Moments =
    [
        Utc(2025, 12, 1), Utc(2026, 1, 10), Utc(2026, 3, 1), Utc(2026, 6, 20), Utc(2026, 9, 1), Utc(2026, 12, 20),
        Utc(2027, 1, 5), Utc(2027, 1, 20), Utc(2027, 2, 14), Utc(2027, 3, 1), Utc(2027, 7, 1), Utc(2028, 3, 1),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.12")]
    [Trait("Finding", "x9")]
    public void Задача_станів_не_переводить_період_назад_окрім_Reopen_вікна_року()
    {
        var checkedCases = 0;
        var yearReopens = 0;
        var skipped = 0;

        foreach (var stored in Enum.GetValues<Stored>())
        {
            foreach (var month in new[] { 1, 6, 12 })
            {
                foreach (var (open, grace, close) in Policies)
                {
                    foreach (var days in Windows)
                    {
                        foreach (var now in Moments)
                        {
                            var period = Make(month, stored);

                            // Зміна політики ПІСЛЯ встановлення стану (межі відсунуто вперед чи назад).
                            period.RecomputeBoundaries(
                                new PeriodPolicy(EcrCode.Create("RUN"), open, grace, close, days ?? 0), Site);
                            YearGraceWindow? window = days is { } d ? YearGraceWindow.For(ProjectEnd, d, Site) : null;

                            var before = period.State;
                            var plan = Calculator.PlanTransitions([period], now, Site, window);
                            var label = string.Create(
                                CultureInfo.InvariantCulture,
                                $"{stored}, місяць {month}, політика {open}/{grace}/{close}, вікно {days}, {now:yyyy-MM-dd}");

                            foreach (var s in plan.Skipped)
                            {
                                Assert.True(s.Computed < s.Current, $"пропуск не зворотний: {label}");
                                skipped++;
                            }

                            // Так само, як PeriodStateJob.ProcessProjectAsync.
                            foreach (var (p, target) in plan.Transitions)
                            {
                                Assert.True(target > p.State, $"план містить незворотно-вперед перехід {p.State} → {target}: {label}");
                                p.AdvanceTo(target, now);
                            }

                            foreach (var reopen in plan.YearReopens)
                            {
                                Assert.Equal(PeriodState.Closed, reopen.Period.State);
                                Assert.True(
                                    window is { } w && w.HoldsInGrace(reopen.Period, now),
                                    $"Reopen поза вікном року: {label}");
                                reopen.Period.Reopen(reopen.Until, reopen.Reason, now);
                                yearReopens++;
                            }

                            var after = period.State;
                            var allowedBackward = before == PeriodState.Closed
                                                  && after == PeriodState.Grace
                                                  && window is { } yw && yw.HoldsInGrace(period, now);
                            Assert.True(after >= before || allowedBackward, $"стан назад {before} → {after}: {label}");
                            checkedCases++;
                        }
                    }
                }
            }
        }

        // Перебір справді зачепив обидві гілки, а не пройшов «порожнім».
        Assert.Equal(6 * 3 * 12 * 4 * 12, checkedCases);
        Assert.True(yearReopens > 0, "жодного Reopen вікна року — сітка не покриває D-204");
        Assert.True(skipped > 0, "жодного пропуску — сітка не покриває зміну політики");
    }
}
