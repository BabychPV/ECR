// tests/Ecr.Application.Tests/Periods/ReopenRaceTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// Гонка `Reopen` і `PeriodStateJob` (ФВ-1.10a). Обидві операції беруть рядок
/// періоду з `UPDLOCK`; програвший бачить актуальний стан, а не тихо
/// застосовується до вже закритого періоду.
/// </summary>
public sealed class ReopenRaceTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Одночасні_Reopen_і_закриття_серіалізуються()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Програвший_бачить_актуальний_стан_і_відмовляє_з_причиною()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Два_одночасні_Reopen_дають_один_результат()
        => Assert.Fail("not implemented");
}
