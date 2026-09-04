using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// Робочий процес із гранулярністю **аркуш × період** (D-38) і правилом
/// «поданий документ не редагується» (D-67).
/// </summary>
public sealed class SubmitApproveTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Подання_аркуша_за_період_не_зачіпає_інші_аркуші()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Подання_з_незакритими_помилками_валідації_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Подання_створює_іммутабельний_зріз_із_версіями_і_режимами()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поданий_аркуш_не_редагується_навіть_у_стані_Grace()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reopen_документа_повертає_аркуш_у_Draft_із_обовязковою_причиною()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reopen_документа_при_закритому_періоді_відхиляється_ECR_PRD_4223()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_Reopen_зміни_позначаються_як_пізні()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Старий_поданий_зріз_лишається_після_повторного_подання()
        => Assert.Fail("not implemented");
}
