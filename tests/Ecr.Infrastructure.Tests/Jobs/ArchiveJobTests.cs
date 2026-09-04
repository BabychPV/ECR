using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Архівація року.
/// </summary>
/// <remarks>
/// Головне правило процедури: **дані з джерела не видаляються, поки контрольні
/// суми не збіглися**. Тест на обрив посередині перевіряє саме це — і саме він
/// відрізняє відновлювану операцію від такої, що при збої лишає систему в
/// напівстані.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchiveJobTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Успішна_архівація_переносить_усі_рядки_і_звільняє_партиції()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Розбіжність_контрольних_сум_зупиняє_процес()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void При_розбіжності_дані_джерела_лишаються_на_місці()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Обрив_посередині_дозволяє_продовжити_з_наступної_партиції()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Під_час_архівації_читання_бере_джерело_за_станом_а_не_за_датою()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Розархівація_повертає_рядки_без_втрат()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Читання_архівного_року_прозоре_для_викликача()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Діапазон_партицій_для_квартального_проєкту_охоплює_чотири_а_не_дванадцять()
        => Assert.Fail("not implemented");
}
