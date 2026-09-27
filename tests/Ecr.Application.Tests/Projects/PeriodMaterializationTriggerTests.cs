using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Projects;

/// <summary>
/// Які переходи періоду ставлять задачу матеріалізації PI — повна таблиця
/// всіх пар станів, одна на всі місця переходу (<c>PeriodStateJob</c>,
/// активація проєкту, перевідкриття).
/// </summary>
public sealed class PeriodMaterializationTriggerTests
{
    public static TheoryData<PeriodState, PeriodState, bool> Transitions()
    {
        // Так — рівно три групи: відкриття, закриття за один прогін, перевідкриття.
        var yes = new HashSet<(PeriodState, PeriodState)>
        {
            (PeriodState.Scheduled, PeriodState.Open),
            (PeriodState.Scheduled, PeriodState.Grace),
            (PeriodState.Scheduled, PeriodState.Closed),
            (PeriodState.Closed, PeriodState.Grace),
            (PeriodState.Closed, PeriodState.Open),
        };

        var data = new TheoryData<PeriodState, PeriodState, bool>();
        foreach (var before in Enum.GetValues<PeriodState>())
        {
            foreach (var after in Enum.GetValues<PeriodState>())
            {
                data.Add(before, after, yes.Contains((before, after)));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "D16-03")]
    public void Перехід_потребує_матеріалізації_рівно_за_таблицею(PeriodState before, PeriodState after, bool expected)
    {
        // ⛔ `Scheduled → Closed`: без задачі точки лишались сирими мовчки —
        // задача лишає `SkippedPeriodClosed` у журналі покриття.
        // ⛔ `Closed → Grace`: без задачі пропущені точки не підхоплювались і
        // після перевідкриття.
        // ⚠ `Open → Grace`, `Grace → Closed` — ні: період побував відкритим.
        Assert.Equal(expected, PeriodMaterializationTrigger.Requires(before, after));
    }
}
