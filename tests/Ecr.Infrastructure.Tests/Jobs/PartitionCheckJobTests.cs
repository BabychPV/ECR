// tests/Ecr.Infrastructure.Tests/Jobs/PartitionCheckJobTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Перевірка запасу партицій. Задача **алертить**, а `SPLIT` робить SQL Agent
/// (D-66): обліковий запис застосунку не має DDL-прав у PROD.
/// </summary>
public sealed class PartitionCheckJobTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Нестача_запасу_партицій_дає_попередження()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Задача_не_виконує_DDL()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Достатній_запас_не_породжує_шуму()
        => Assert.Fail("not implemented");
}
