// tests/Ecr.Infrastructure.Tests/Persistence/UnitOfWorkTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Транзакційна межа. Довгі транзакції заборонені (D-29): під RCSI вони
/// роздувають version store, і пік «останнього дня періоду» стає збоєм.
/// </summary>
public sealed class UnitOfWorkTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Вкладені_транзакції_заборонені()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Масовий_імпорт_іде_батчами_з_окремим_commit()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Відкат_повертає_стан_повністю()
        => Assert.Fail("not implemented");
}
