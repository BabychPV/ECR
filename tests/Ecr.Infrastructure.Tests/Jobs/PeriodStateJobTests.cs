// tests/Ecr.Infrastructure.Tests/Jobs/PeriodStateJobTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Переходи станів періоду. Рахуються в **поясі майданчика**, не за
/// UTC-опівніччю (D-68): період, що закривається «31 числа о 23:59», має
/// закритися о 23:59 там, де сидять люди.
/// </summary>
public sealed class PeriodStateJobTests
{
    private static readonly TimeZoneInfo Site = ProjectBuilder.Zone(offsetHours: 5);

    private readonly PeriodStateCalculator _calculator = new();

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Переходи_рахуються_в_поясі_майданчика()
    {
        var onSite = Periods(Site)[0];
        var onUtc = Periods(TimeZoneInfo.Utc)[0];

        // 31 січня 23:00 у поясі майданчика (UTC+5) — це 18:00 UTC. Період ще
        // відкритий: останній день належить йому цілком.
        var stillOpen = new DateTime(2026, 1, 31, 18, 0, 0, DateTimeKind.Utc);
        Assert.Equal(PeriodState.Open, _calculator.Calculate(onSite, stillOpen, Site));

        // Місцева північ 1 лютого — це 19:00 UTC 31 січня. Саме тут
        // період має перейти в Grace.
        var localMidnight = new DateTime(2026, 1, 31, 19, 0, 0, DateTimeKind.Utc);
        Assert.Equal(PeriodState.Grace, _calculator.Calculate(onSite, localMidnight, Site));

        // ⚠ Той самий період із межами, порахованими в UTC, о тій самій миті
        // ще відкритий — і залишатиметься відкритим до 05:00 місцевого часу
        // 1 лютого. Межа періоду перестає збігатися з тим, що люди називають
        // «кінець місяця», а на майданчику із від’ємним зсувом — закривається раніше,
        // ніж закінчився останній робочий день (D-68).
        Assert.Equal(PeriodState.Open, _calculator.Calculate(onUtc, localMidnight, TimeZoneInfo.Utc));
        Assert.NotEqual(onSite.ComputedGraceAt, onUtc.ComputedGraceAt);
        Assert.Equal(TimeSpan.FromHours(5), onUtc.ComputedGraceAt - onSite.ComputedGraceAt);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Pinned_поточний_період_не_перезаписується_задачею()
    {
        var project = ProjectBuilder.Project();
        var periods = Periods();
        ProjectBuilder.Attach(project, periods);

        var now = new DateTime(2026, 2, 10, 6, 0, 0, DateTimeKind.Utc);
        var pinned = periods[0].Id;
        project.PinCurrentPeriod(pinned, "звірка річного звіту", userId: 7, now);

        // Задача пропонує лютий; проєкт лишається на січні.
        Apply(periods, now);
        var chosen = _calculator.SelectCurrentPeriod(periods);
        project.SetCurrentPeriodAutomatically(chosen?.Id, now);

        // ⚠ «Пін» — рішення людини з причиною. Нічна задача, що його скасовує,
        // виглядала б як зникнення налаштування без сліду (D-77).
        Assert.Equal(periods[1].Id, chosen?.Id);
        Assert.Equal(pinned, project.CurrentPeriodId);
        Assert.Equal(CurrentPeriodMode.Pinned, project.CurrentPeriodMode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Стан_є_збереженим_значенням_а_не_функцією_від_now()
    {
        var periods = Periods();
        var beforeRun = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        // Час пішов далеко вперед, а задача не працювала — стан лишився тим,
        // яким його записали останнього разу.
        Assert.All(periods, p => Assert.Equal(PeriodState.Scheduled, p.State));

        // ⚠ Якби стан рахувався в запиті, два одночасні запити на межі доби
        // дали б різні відповіді, а перевірка доступу — різні рішення для тієї
        // самої комірки (ФВ-1.12).
        Assert.Equal(PeriodState.Grace, _calculator.Calculate(periods[0], beforeRun, Site));
        Assert.Equal(PeriodState.Scheduled, periods[0].State);

        Apply(periods, beforeRun);
        Assert.Equal(PeriodState.Grace, periods[0].State);
        Assert.Equal(beforeRun, periods[0].StateChangedAt);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Повторний_запуск_не_змінює_вже_переведені_періоди()
    {
        var periods = Periods();
        var first = new DateTime(2026, 2, 5, 6, 0, 0, DateTimeKind.Utc);

        Apply(periods, first);
        var stamps = periods.Select(p => p.StateChangedAt).ToList();
        var states = periods.Select(p => p.State).ToList();

        var second = first.AddHours(1);
        var planned = PeriodStateJob.Plan(periods, second, Site, _calculator);

        // Задача ідемпотентна: другий прогін за ту саму годину не пропонує
        // жодного переходу. Інакше StateChangedAt оновлювався б щогодини, і
        // журнал перестав би відповідати, коли період справді змінився.
        Assert.Empty(planned);

        Apply(periods, second);
        Assert.Equal(states, periods.Select(p => p.State));
        Assert.Equal(stamps, periods.Select(p => p.StateChangedAt));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Одночасний_Reopen_серіалізується_а_не_губиться()
    {
        var periods = Periods();

        // Січень уже закритий за розкладом.
        var afterClose = new DateTime(2026, 4, 1, 6, 0, 0, DateTimeKind.Utc);
        Apply(periods, afterClose);
        Assert.Equal(PeriodState.Closed, periods[0].State);

        // Адміністратор відкриває його до кінця тижня.
        var reopenedUntil = afterClose.AddDays(7);
        periods[0].Reopen(reopenedUntil, "уточнення за скаргою", afterClose);
        Assert.Equal(PeriodState.Grace, periods[0].State);

        // ⚠ Наступний прогін задачі НЕ повертає період у Closed: тимчасове
        // відкриття перевіряється першим і перекриває розрахунок. Інакше нічна
        // задача мовчки скасувала б рішення людини (ФВ-1.10a).
        var nextRun = afterClose.AddDays(1);
        Assert.Empty(PeriodStateJob.Plan(periods, nextRun, Site, _calculator));
        Assert.Equal(PeriodState.Grace, periods[0].State);

        // Коли вікно вийшло — період повертається в Closed, і не потребує
        // окремої дії адміністратора.
        Assert.Equal(
            PeriodState.Closed, _calculator.Calculate(periods[0], reopenedUntil.AddMinutes(1), Site));
    }

    /// <summary>Періоди проєкту з межами, порахованими в указаному поясі.</summary>
    private static List<Period> Periods(TimeZoneInfo? zone = null)
    {
        var project = ProjectBuilder.Project();
        var periods = PeriodCalendar.Build(project, ProjectBuilder.Policy(), zone ?? Site, existing: []);

        var next = 100;
        foreach (var period in periods)
        {
            typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(period, next++);
        }

        return [.. periods];
    }

    /// <summary>Прогін задачі: план і його застосування — тими самими викликами, що й у задачі.</summary>
    private void Apply(IReadOnlyList<Period> periods, DateTime utcNow)
    {
        foreach (var (period, target) in PeriodStateJob.Plan(periods, utcNow, Site, _calculator))
        {
            period.AdvanceTo(target, utcNow);
        }
    }
}
