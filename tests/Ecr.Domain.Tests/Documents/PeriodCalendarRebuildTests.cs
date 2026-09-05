// tests/Ecr.Domain.Tests/Documents/PeriodCalendarRebuildTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Повторна побудова календаря перераховує межі наявних періодів.
/// </summary>
/// <remarks>
/// ⛔ До `A7-26` календар лише ДОПОВНЮВАВ набір: наявні періоди пропускалися
/// цілком. Наслідків було два, і обидва мовчазні:
/// <list type="number">
/// <item>зміна політики періодів або поясу майданчика не діяла на вже створені
/// періоди — ніколи;</item>
/// <item>період, створений в обхід календаря, лишався з межами
/// <c>0001-01-01</c>, а калькулятор станів читає їх як «усе вже минуло» і
/// оголошує період ЗАКРИТИМ. Система виглядала налаштованою і не приймала
/// жодного значення.</item>
/// </list>
/// </remarks>
public sealed class PeriodCalendarRebuildTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Повторна_побудова_перераховує_межі_наявного_періоду()
    {
        var project = Project();
        var zone = TimeZoneInfo.Utc;

        // Період без обчислених меж — саме те, що дає створення в обхід
        // календаря: у базі стоїть `0001-01-01`.
        var orphan = new Period(
            project.Id, PeriodKey.Create(2026, 1), 1,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.Equal(default, orphan.ComputedOpenAt);

        PeriodCalendar.Build(project, Policy(closeOffsetDays: 45), zone, [orphan]);

        Assert.NotEqual(default, orphan.ComputedOpenAt);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), orphan.ComputedOpenAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зміна_політики_діє_на_вже_створені_періоди()
    {
        var project = Project();
        var zone = TimeZoneInfo.Utc;

        var period = new Period(
            project.Id, PeriodKey.Create(2026, 1), 1,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        period.RecomputeBoundaries(Policy(closeOffsetDays: 45), zone);
        var before = period.ComputedCloseAt;

        // ⚠ Політику змінили: строк здачі скоротили вдвічі. Якщо календар не
        // перераховує наявні періоди, зміна лишиться на папері.
        PeriodCalendar.Build(project, Policy(closeOffsetDays: 20), zone, [period]);

        Assert.NotEqual(before, period.ComputedCloseAt);
        Assert.True(period.ComputedCloseAt < before);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Повторна_побудова_не_створює_дублікатів()
    {
        // Ідемпотентність лишається: перерахунок меж її не скасовує.
        var project = Project();
        var existing = new Period(
            project.Id, PeriodKey.Create(2026, 1), 1,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var created = PeriodCalendar.Build(project, Policy(45), TimeZoneInfo.Utc, [existing]);

        Assert.DoesNotContain(created, p => p.PeriodKeyValue == existing.PeriodKeyValue);
        Assert.Equal(11, created.Count);
    }

    private static PeriodPolicy Policy(int closeOffsetDays)
        => new(EcrCode.Create("Default"), 0, 0, closeOffsetDays, 45);

    private static Project Project()
        => new(
            EcrCode.Create("P2026"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Probe" }),
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 12, 31),
            templateVersionId: 1,
            PeriodKind.Monthly,
            periodPolicyId: 1,
            "UTC");
}
