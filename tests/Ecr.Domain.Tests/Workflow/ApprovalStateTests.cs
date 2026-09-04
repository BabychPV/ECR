// tests/Ecr.Domain.Tests/Workflow/ApprovalStateTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Workflow;

/// <summary>
/// Стан робочого процесу на аркуш × період — **єдине джерело істини** про
/// статус (D-38, D-93).
/// </summary>
public sealed class ApprovalStateTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reopen_без_причини_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Перехід_Approved_у_Draft_можливий_лише_через_Reopen()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reopen_із_Draft_відхиляється_бо_нема_чого_відкривати()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Стан_одного_аркуша_не_зачіпає_інші_аркуші_періоду()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reject_вимагає_коментаря()
        => Assert.Fail("not implemented");
}
