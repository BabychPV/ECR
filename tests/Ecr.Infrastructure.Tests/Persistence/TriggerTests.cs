using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Незмінність опублікованої версії забезпечується **на рівні БД**, а не лише
/// кодом: тригер має спрацювати навіть на прямому <c>UPDATE</c> повз застосунок.
/// </summary>
/// <remarks>
/// Окремо перевіряється сумісність із EF Core: без
/// <c>.ToTable(t =&gt; t.HasTrigger(...))</c> <c>SaveChanges</c> падає в
/// рантаймі через <c>OUTPUT</c>-клаузу (ТЗ §13.5 п.1) — помилку легко
/// пропустити до першого запису в проді.
/// </remarks>
[Collection("SqlServer")]
public sealed class TriggerTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_типу_колонки_опублікованої_версії_відхиляється_тригером()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_підпису_колонки_опублікованої_версії_дозволена()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_Ordinal_опублікованої_версії_дозволена()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_RowKey_опублікованої_версії_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Видалення_формули_опублікованої_версії_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void SaveChanges_на_таблиці_з_тригером_не_падає_бо_оголошено_HasTrigger()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміни_у_чернетці_тригер_не_блокує()
        => Assert.Fail("not implemented");
}
