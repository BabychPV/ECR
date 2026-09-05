using Ecr.Adapters.PiAf;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests;

/// <summary>
/// Пошук прогалин у журналі покриття.
/// </summary>
/// <remarks>
/// ⚠ Перевіряється саме чиста <see cref="CatchUpPlanner.FindGaps"/>, а не
/// прогін збору: це єдине місце, де вирішується, що таке «дірка». Помилка тут
/// не падає — вона мовчки не збирає даних за проміжок, і побачать це на
/// звірці через місяць.
/// </remarks>
public sealed class CatchUpPlannerTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-11.3")]
    public void Порожнє_покриття_дає_одну_прогалину_на_весь_період()
    {
        var gaps = CatchUpPlanner.FindGaps([], From, From.AddDays(10));

        Assert.Single(gaps);
        Assert.Equal(From, gaps[0].From);
        Assert.Equal(From.AddDays(10), gaps[0].To);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Суцільне_покриття_не_дає_прогалин()
    {
        var covered = new[] { new TimeInterval(From, From.AddDays(10)) };

        Assert.Empty(CatchUpPlanner.FindGaps(covered, From, From.AddDays(10)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Суміжні_інтервали_зливаються_і_прогалини_між_ними_не_зʼявляється()
    {
        var covered = new[]
        {
            new TimeInterval(From, From.AddDays(3)),
            new TimeInterval(From.AddDays(3), From.AddDays(7)),
        };

        Assert.Empty(CatchUpPlanner.FindGaps(covered, From, From.AddDays(7)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Перекриті_інтервали_не_створюють_фальшивої_прогалини()
    {
        // ⚠ Саме цей випадок дає найтихішу помилку: два прогони, що читали
        // діапазони з нахлестом, лишили б «дірку» там, де даних насправді
        // вдосталь — і збирач ганяв би джерело по вже зібраному.
        var covered = new[]
        {
            new TimeInterval(From, From.AddDays(5)),
            new TimeInterval(From.AddDays(2), From.AddDays(9)),
        };

        Assert.Empty(CatchUpPlanner.FindGaps(covered, From, From.AddDays(9)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Прогалини_повертаються_від_найстарішої()
    {
        var covered = new[]
        {
            new TimeInterval(From.AddDays(2), From.AddDays(3)),
            new TimeInterval(From.AddDays(6), From.AddDays(7)),
        };

        var gaps = CatchUpPlanner.FindGaps(covered, From, From.AddDays(10));

        Assert.Equal(3, gaps.Count);
        Assert.Equal(From, gaps[0].From);
        Assert.Equal(From.AddDays(3), gaps[1].From);
        Assert.Equal(From.AddDays(7), gaps[2].From);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Покриття_поза_вікном_огляду_обрізається_а_не_ігнорується()
    {
        // Інтервал, що починається до вікна, покриває свою частину всередині:
        // відкинути його цілком означало б дозбирувати вже зібране.
        var covered = new[] { new TimeInterval(From.AddDays(-5), From.AddDays(4)) };

        var gaps = CatchUpPlanner.FindGaps(covered, From, From.AddDays(10));

        Assert.Single(gaps);
        Assert.Equal(From.AddDays(4), gaps[0].From);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Вікно_нульової_довжини_прогалин_не_дає()
    {
        // Верхня межа «зараз»: поки час не рушив, дірки не існує — інакше
        // наздоганяння просило б у джерела дані, яких ще немає.
        Assert.Empty(CatchUpPlanner.FindGaps([], From, From));
    }
}
