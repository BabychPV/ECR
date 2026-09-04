// tests/Ecr.Application.Tests/Documents/GetTableSliceTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Читання зрізу — бюджет **p95 400 мс** на ~5 000 комірок (tz/08 §8.2).
/// Саме тому права перевіряються одним викликом, а не покомірково.
/// </summary>
public sealed class GetTableSliceTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Порожні_комірки_не_повертаються()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Права_перевіряються_одним_викликом_на_зріз()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Немає_запиту_на_кожен_рядок()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_повертається_окремою_ознакою()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ознака_IsOrphaned_читається_а_не_перераховується()
        => Assert.Fail("not implemented");
}
