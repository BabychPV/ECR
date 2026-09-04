// tests/Ecr.Infrastructure.Tests/Jobs/PeriodStateJobTests.cs
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
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Переходи_рахуються_в_поясі_майданчика()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Pinned_поточний_період_не_перезаписується_задачею()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Стан_є_збереженим_значенням_а_не_функцією_від_now()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Повторний_запуск_не_змінює_вже_переведені_періоди()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Одночасний_Reopen_серіалізується_а_не_губиться()
        => Assert.Fail("not implemented");
}
