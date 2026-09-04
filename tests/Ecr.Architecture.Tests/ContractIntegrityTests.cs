using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Цілісність контрактів: те, що описано в <c>02-contracts.md</c>, має
/// існувати в коді і не розходитися з ним.
/// </summary>
/// <remarks>
/// Рев'ю Етапу N перевіряє, що <c>git diff</c> по файлах контрактів порожній.
/// Ці тести перевіряють інше: що контракт **реалізований повністю** — усі
/// enum-значення на місці, усі порти мають реалізацію (крім свідомо
/// відсутніх), усі коди помилок унікальні.
/// </remarks>
public sealed class ContractIntegrityTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_порт_має_рівно_одну_реалізацію_окрім_явно_множинних()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Коди_помилок_унікальні_і_відповідають_формату()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_значення_EditDenyReason_повертається_хоча_б_одним_шляхом()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_ендпоінт_із_таблиці_бюджету_має_метрику()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Публічні_типи_мають_XML_документацію()
        => Assert.Fail("not implemented");
}
