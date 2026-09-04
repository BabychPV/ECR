// tests/Ecr.Api.Tests/DocumentsControllerTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Контролер не містить логіки — вона в обробнику.</summary>
public sealed class DocumentsControllerTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Контролер_лише_делегує_обробнику()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Права_перевіряються_в_обробнику_а_не_атрибутом_контролера()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Помилка_повертається_як_ProblemDetails_із_кодом()
        => Assert.Fail("not implemented");
}
