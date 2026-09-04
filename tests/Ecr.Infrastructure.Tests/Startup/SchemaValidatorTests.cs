using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Startup;

/// <summary>
/// Перевірки при старті. Мета — **впасти зрозуміло**, а не працювати на
/// несумісному середовищі й з'ясувати це на першому записі (ФВ-7.9).
/// </summary>
[Collection("SqlServer")]
public sealed class SchemaValidatorTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Незастосована_міграція_у_режимі_Validate_зупиняє_старт()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Міграція_у_БД_якої_немає_у_збірці_зупиняє_старт()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Відсутність_схеми_партиціонування_зупиняє_старт_із_інструкцією()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Вимкнений_RCSI_дає_критичний_стан_здоровя_але_не_зупиняє_старт()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Standard_старший_за_2016_SP1_приймається()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Standard_до_2016_SP1_зупиняє_старт_бо_модель_архівації_не_працює()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Developer_Edition_розпізнається_як_Enterprise()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явний_режим_Standard_перекриває_автовизначення()
        => Assert.Fail("not implemented");
}
