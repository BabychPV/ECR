using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Інкремент ревізії — **одним statement із `OUTPUT`** (R-B7).
/// Read-modify-write у застосунку заборонений, бо інстансів ≥2 і дві
/// презентаційні правки одночасно дали б однакову ревізію.
/// </summary>
[Collection("SqlServer")]
public sealed class PresentationRevisionTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Інкремент_повертає_нове_значення_одним_запитом()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Паралельні_інкременти_дають_різні_значення()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Ключ_кешу_змінюється_після_презентаційної_правки()
        => Assert.Fail("not implemented");
}
