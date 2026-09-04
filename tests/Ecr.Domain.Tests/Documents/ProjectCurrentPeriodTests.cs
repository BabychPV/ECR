using System.Reflection;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// <c>Project.CurrentPeriod</c> — **наша конфігурація** (D-77), а не значення
/// з зовнішньої системи.
/// </summary>
public sealed class ProjectCurrentPeriodTests
{
    private const int User = 7;
    private static readonly DateTime Now = new(2026, 2, 5, 10, 0, 0, DateTimeKind.Utc);

    private static Project Make(out Period period)
    {
        var project = new Project(
            EcrCode.Create("PLANT_A"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Plant A" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: 2, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");

        period = new Period(project.Id, new PeriodKey(202601), 1,
                            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        typeof(Entity<int>).GetProperty("Id")!.SetValue(period, 55);

        var field = typeof(Project).GetField("_periods", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((List<Period>)field.GetValue(project)!).Add(period);

        return project;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Новий_проєкт_має_режим_Auto()
    {
        var project = Make(out _);

        // Auto за замовчуванням: пін — свідома дія з причиною, а не типовий стан.
        Assert.Equal(CurrentPeriodMode.Auto, project.CurrentPeriodMode);
        Assert.Null(project.CurrentPeriodId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Фіксація_періоду_без_причини_відхиляється()
    {
        var project = Make(out var period);

        var error = Assert.Throws<DomainException>(
            () => project.PinCurrentPeriod(period.Id, "   ", User, Now));

        // Стан неочевидний для того, хто відкриє проєкт наступним, і має бути
        // видимим в UI разом із поясненням.
        Assert.Equal("ECR-PRD-0422", error.ErrorCode);
        Assert.Equal(CurrentPeriodMode.Auto, project.CurrentPeriodMode);

        // Чужий період теж не приймається.
        Assert.Throws<DomainException>(() => project.PinCurrentPeriod(999, "причина", User, Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void У_режимі_Pinned_автоматичне_оновлення_не_змінює_поточний_період()
    {
        var project = Make(out var period);
        project.PinCurrentPeriod(period.Id, "звірка за минулий рік", User, Now);

        project.SetCurrentPeriodAutomatically(periodId: 999, Now.AddDays(1));

        // ⚠ Ручний пін має пріоритет над нічною задачею: людина зафіксувала
        // період свідомо і з причиною, і задача не має права це скасувати.
        Assert.Equal(period.Id, project.CurrentPeriodId);
        Assert.Equal(CurrentPeriodMode.Pinned, project.CurrentPeriodMode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зняття_фіксації_повертає_режим_Auto()
    {
        var project = Make(out var period);
        project.PinCurrentPeriod(period.Id, "звірка", User, Now);

        project.UnpinCurrentPeriod(User, Now.AddHours(1));

        Assert.Equal(CurrentPeriodMode.Auto, project.CurrentPeriodMode);
        Assert.Null(project.CurrentPeriodPinnedReason);

        // CurrentPeriodId лишається до наступного прогону задачі: показувати
        // останнє відоме значення краще, ніж порожнечу.
        Assert.Equal(period.Id, project.CurrentPeriodId);

        project.SetCurrentPeriodAutomatically(periodId: 999, Now.AddHours(2));
        Assert.Equal(999, project.CurrentPeriodId);
    }
}
