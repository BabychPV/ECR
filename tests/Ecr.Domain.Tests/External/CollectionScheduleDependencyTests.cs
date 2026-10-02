// tests/Ecr.Domain.Tests/External/CollectionScheduleDependencyTests.cs
using Ecr.Domain.Entities.External;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.External;

/// <summary>Залежність розкладу збору від іншого розкладу (ФВ-13.15) — без бази.</summary>
public sealed class CollectionScheduleDependencyTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-13.15")]
    public void Без_залежності_збір_запускається_завжди()
    {
        var schedule = Ran(Now.AddHours(-3));

        Assert.True(schedule.IsDependencyMet(dependency: null, Now));
        Assert.True(schedule.IsDependencyMet(Ran(Now.AddHours(-1)), Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-13.15")]
    public void Запуск_чекає_прогону_залежного_не_раннього_за_власний_останній()
    {
        var schedule = Ran(Now.AddHours(-3));
        schedule.SetDependency(2);

        // Залежний бігав ДО нашого останнього прогону — спершу має відбігти він.
        Assert.False(schedule.IsDependencyMet(Ran(Now.AddHours(-5)), Now));

        // Після нашого останнього (і в той самий момент) — можна.
        Assert.True(schedule.IsDependencyMet(Ran(Now.AddHours(-2)), Now));
        Assert.True(schedule.IsDependencyMet(Ran(Now.AddHours(-3)), Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-13.15")]
    public void Залежність_не_блокує_вічно_вимкнена_ненаявна_ще_не_бігала_або_застаріла()
    {
        var schedule = Ran(Now.AddHours(-30));
        schedule.SetDependency(2);

        var disabled = Ran(Now.AddHours(-40));
        disabled.Disable();

        // ⛔ Без цих виходів збір стояв би, доки залежність не оживе, — а вона може не ожити ніколи.
        Assert.True(schedule.IsDependencyMet(dependency: null, Now), "залежного видалено");
        Assert.True(schedule.IsDependencyMet(disabled, Now), "залежний вимкнений");
        Assert.True(schedule.IsDependencyMet(new CollectionSchedule(1, "0 5 * * * ?"), Now), "залежний ще не бігав");

        // ⚠ Межа 48 годин літералами: рівно 48 — ще блокує, 49 — вже ні.
        Assert.False(schedule.IsDependencyMet(Ran(Now.AddHours(-48)), Now));
        Assert.True(schedule.IsDependencyMet(Ran(Now.AddHours(-49)), Now));

        // Свого прогону ще не було — нема від чого відраховувати «після».
        var first = new CollectionSchedule(1, "0 5 * * * ?");
        first.SetDependency(2);
        Assert.True(first.IsDependencyMet(Ran(Now.AddHours(-1)), Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-13.15")]
    public void Розклад_не_залежить_сам_від_себе()
    {
        var schedule = new CollectionSchedule(1, "0 5 * * * ?");
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(schedule, 5);

        Assert.Throws<ArgumentException>(() => schedule.SetDependency(5));

        schedule.SetDependency(6);
        Assert.Equal(6, schedule.DependsOnScheduleId);

        schedule.SetDependency(null);
        Assert.Null(schedule.DependsOnScheduleId);
    }

    private static CollectionSchedule Ran(DateTime at)
    {
        var schedule = new CollectionSchedule(1, "0 5 * * * ?");
        schedule.MarkRun(at, watermark: null);

        return schedule;
    }
}
