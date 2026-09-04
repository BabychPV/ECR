// tests/Ecr.Infrastructure.Tests/Jobs/ConsistencyCheckJobTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Нічна перевірка інваріантів (ФВ-7.7). **Знахідка — баг, а не шум**: якщо
/// перевірка регулярно щось знаходить і це вважають нормою, вона перестає
/// працювати як сигнал.
/// </summary>
public sealed class ConsistencyCheckJobTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Виявляє_осиротілі_комірки()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Виявляє_порушені_FK_у_гібридному_режимі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Звіряє_архів_із_джерелом_за_контрольними_сумами()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Ставить_і_знімає_IsOrphaned_в_обидва_боки()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Не_чіпає_закриті_періоди()
        => Assert.Fail("not implemented");
}
