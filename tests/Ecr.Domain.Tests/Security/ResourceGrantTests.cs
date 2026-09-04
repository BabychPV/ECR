// tests/Ecr.Domain.Tests/Security/ResourceGrantTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Security;

/// <summary>
/// `IsDeny` **виграє завжди**, на будь-якому рівні успадкування (ФВ-6.6).
/// </summary>
/// <remarks>
/// Це свідома жорсткість. Альтернатива «конкретніший рівень перемагає» дає
/// ситуації, де людина має доступ і ніхто не може пояснити чому — а пояснити
/// доступ важливіше, ніж зробити його гнучким.
/// </remarks>
public sealed class ResourceGrantTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Deny_на_проєкті_перекриває_Manage_на_аркуші()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Deny_на_колонці_перекриває_Write_на_таблиці()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Рівні_упорядковані_від_None_до_Manage()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Грант_успадковується_від_проєкту_до_колонки()
        => Assert.Fail("not implemented");
}
