using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Три операції над коміркою і те, що порожні не матеріалізуються.</summary>
[Collection("SqlServer")]
public sealed class CellStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Запис_значення_створює_рядок_комірки()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Стирання_видаляє_рядок_комірки_а_не_обнуляє_його()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Явна_порожнеча_лишає_рядок_із_прапорцем_IsEmpty()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Незаповнені_комірки_не_створюються_і_не_повертаються()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Читання_зрізу_виконує_один_запит_а_не_запит_на_рядок()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Масове_завантаження_вантажить_рядки_і_комірки_одним_проходом()
        => Assert.Fail("not implemented");
}
