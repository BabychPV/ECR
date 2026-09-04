using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Аудит: пакетність, незмінність, партиціонування по <c>ChangedAt</c>.</summary>
[Collection("SqlServer")]
public sealed class AuditTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Аудит_ста_комірок_пишеться_одним_запитом_а_не_ста()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Автором_зміни_є_UserId_а_не_SID()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_у_стані_Grace_позначається_як_пізня()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Аудит_партиціонується_за_моментом_зміни_а_не_за_звітним_періодом()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_за_січень_у_березні_потрапляє_в_березневу_партицію_аудиту()
        => Assert.Fail("not implemented");
}
