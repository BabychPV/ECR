using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Дванадцять сценаріїв доступу з фікстури <c>02c §6</c> плюс три додаткові
/// з <c>tz/07</c> §7.6.
/// </summary>
/// <remarks>
/// Найважливіший тут — <c>A7</c>: **закритий період блокує запис усім,
/// включно з найвищим грантом**. Якщо він проходить — модель доступу зламана,
/// і жоден інший тест цього не покаже.
/// </remarks>
public sealed class AccessDecisionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A01_оператор_із_грантом_Write_редагує_відкритий_період()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A02_локальний_користувач_має_ті_самі_права_що_й_доменний()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A03_обчислена_колонка_недоступна_на_запис_із_причиною_CalculatedCell()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A04_переглядач_без_гранта_Write_отримує_причину_NoGrant()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A05_у_стані_Grace_запис_дозволений_і_позначається_як_пізній()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A06_закритий_період_блокує_запис_оператору()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A07_закритий_період_блокує_запис_НАВІТЬ_власнику_Manage()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A08_аркуш_поза_вікном_доступу_дає_причину_OutOfAccessWindow()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A09_поданий_документ_блокує_запис_навіть_у_відкритому_періоді()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A10_заборона_на_одну_колонку_не_блокує_решту()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A11_подання_потребує_рівня_Submit_а_не_Write()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A12_затвердження_потребує_рівня_Approve_і_стану_Submitted()
        => Assert.Fail("not implemented");

    // ——— Додаткові з tz/07 §7.6 ———

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Заборона_виграє_над_дозволом_на_будь_якому_рівні_успадкування()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Грант_на_колонку_перекриває_грант_на_таблицю()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поточний_період_проєкту_НЕ_впливає_на_рішення_про_доступ()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Профіль_будується_раз_а_не_на_кожну_комірку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Пакетна_перевірка_зрізу_дає_ті_самі_рішення_що_й_поштучна()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відмова_повертає_ПРИЧИНУ_а_не_просто_заборону()
        => Assert.Fail("not implemented");
}
