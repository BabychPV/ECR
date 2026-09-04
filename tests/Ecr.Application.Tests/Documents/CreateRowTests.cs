// tests/Ecr.Application.Tests/Documents/CreateRowTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>Створення рядка динамічної таблиці через той самий batch-PATCH (R-B2).</summary>
public sealed class CreateRowTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void BaseVersion_null_трактується_як_створення()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Перевищення_MaxDynamicRows_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Створення_рядка_у_Fixed_таблиці_заборонене()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Дублікат_RowKey_дає_ECR_ROW_0409()
        => Assert.Fail("not implemented");
}
