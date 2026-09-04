// tests/Ecr.Application.Tests/Registries/OrphanScanTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Ознака `IsOrphaned` (ФВ-8.13, D-98). Механізм **симетричний**: те, що
/// ставить ознаку, має її й знімати — інакше виправлення довідника не
/// розблокує `Submit`.
/// </summary>
public sealed class OrphanScanTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Звуження_ValidTo_ставить_IsOrphaned()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Осиротілий_рядок_блокує_Submit_із_ECR_SUB_4221()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Осиротілий_рядок_не_блокує_читання()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Розширення_вікна_назад_знімає_ознаку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Читання_зрізу_ознаку_не_перераховує()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Нічна_перевірка_не_чіпає_закриті_періоди()
        => Assert.Fail("not implemented");
}
