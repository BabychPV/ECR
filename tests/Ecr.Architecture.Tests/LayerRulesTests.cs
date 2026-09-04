using Ecr.TestKit;
using NetArchTest.Rules;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Вісім правил залежностей із <c>tz/03</c> §3.3.
/// </summary>
/// <remarks>
/// Ці тести — не формальність. Кожне правило захищає конкретну властивість
/// системи: тестованість без БД, замінність адаптерів, універсальність ядра.
/// Порушення жодного з них не проявиться як помилка — воно проявиться через
/// рік як «чому це неможливо змінити».
/// </remarks>
public sealed class LayerRulesTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_1_домен_не_залежить_ні_від_чого()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_2_застосунок_не_знає_про_інфраструктуру_і_EF_Core()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_3_ядро_не_знає_про_AF_Excel_і_екологію()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_4_DbContext_не_зустрічається_у_контролерах()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_5_немає_блокувальних_викликів_Result_і_Wait()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_6_у_застосунку_немає_ToList_без_Take()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_7_перевірка_ролей_робиться_лише_через_IAccessDecisionService()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_8_у_результатних_типах_немає_float_і_double()
        => Assert.Fail("not implemented");
}
