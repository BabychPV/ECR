using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>Id</c> береться з <c>SEQUENCE</c>, а не <c>IDENTITY</c>: значення
/// потрібні **до** вставки, щоб завантажити рядки і комірки одним проходом
/// <c>SqlBulkCopy</c> (B02 §2.3).
/// </summary>
[Collection("SqlServer")]
public sealed class SequenceTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Резервування_діапазону_повертає_безперервні_ідентифікатори()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Паралельні_резервування_не_перетинаються()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Один_виклик_на_батч_а_не_на_рядок()
        => Assert.Fail("not implemented");
}
