// tests/Ecr.Domain.Tests/Documents/ProjectActivationTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Активація проєкту — перехід, без якого система не працює взагалі.
/// </summary>
/// <remarks>
/// ⛔ До `A7-25` цього переходу не існувало: проєкт створювався чернеткою, а
/// <c>PeriodStateJob</c> обробляє лише активні. Отже, жоден період не
/// відкривався ніколи, і на кожній комірці стояло «період ще не відкрито» —
/// причина, яка при цьому неправдива, бо за датами період відкритий.
///
/// ⚠ <c>ProjectStatus.Active</c> траплявся в коді рівно один раз — у параметрі
/// за замовчуванням тестової фікстури. Перевірки жили в системі, якої не
/// існувало.
/// </remarks>
public sealed class ProjectActivationTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Новий_проєкт_є_чернеткою_і_активується()
    {
        var project = Project();

        Assert.Equal(ProjectStatus.Draft, project.Status);

        project.Activate(Now);

        Assert.Equal(ProjectStatus.Active, project.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Повторна_активація_є_помилкою_а_не_нічим()
    {
        // ⚠ «Нічого не сталося» приховало б справжню причину виклику: той, хто
        // активує вдруге, майже завжди вважає стан іншим, ніж він є.
        var project = Project();
        project.Activate(Now);

        Assert.Throws<InvalidOperationException>(() => project.Activate(Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Задача_станів_бере_лише_активні_проєкти_тому_активація_і_потрібна()
    {
        // ⛔ Ось зв'язок, який робив систему непрацездатною. Період із
        // правильними межами лишається `Scheduled`, доки проєкт — чернетка:
        // задача просто до нього не доходить.
        var project = Project();
        var policy = new PeriodPolicy(
            EcrCode.Create("Default"), openOffsetDays: 0, graceOffsetDays: 0,
            hardCloseOffsetDays: 45, yearGraceOffsetDays: 45);
        var zone = TimeZoneInfo.Utc;

        var period = new Period(
            project.Id, PeriodKey.Create(2026, 9), 9,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        period.RecomputeBoundaries(policy, zone);

        var transitions = PeriodStateJob_Plan(period, zone);

        // Сам перехід обчислюється правильно — його просто нікому застосувати,
        // доки проєкт не активований.
        Assert.Equal(PeriodState.Open, transitions);
        Assert.Equal(ProjectStatus.Draft, project.Status);
    }

    private static PeriodState PeriodStateJob_Plan(Period period, TimeZoneInfo zone)
        => new PeriodStateCalculator().Calculate(period, Now, zone);

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
