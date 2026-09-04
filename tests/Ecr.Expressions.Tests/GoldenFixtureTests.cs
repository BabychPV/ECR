using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Обчислення формул фікстури і звірка з **очікуваними числами з
/// `water-demo.json`**.
/// </summary>
/// <remarks>
/// Очікування беруться з файлу, а не з коду тесту. Якщо код дає інше — правий
/// файл (`08-workflow.md` §4). Підганяти очікування під результат коду
/// заборонено: саме так «зелені» тести перестають щось означати.
/// </remarks>
public sealed class GoldenFixtureTests
{
    [Theory]
    [InlineData("7001001", "3650.750")]
    [InlineData("7001002", "1750.750")]   // Feb — комірки НЕМАЄ
    [InlineData("7001003", "4100.500")]   // Mar — ЯВНА порожнеча
    [InlineData("7001101", "1380.500")]
    [InlineData("7001102", "930.000")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F1_сума_місяців_у_рядку(string rowKey, string expected)
        => Assert.Fail("not implemented");

    [Theory]
    [InlineData("Jan", "4750.625")]
    [InlineData("Feb", "4020.500")]
    [InlineData("Mar", "3041.375")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F2_баланс_підсумовує_два_діапазони(string column, string expected)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F3_крос_аркушний_rollup_поверхневих_вод()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рядок_C009_збігається_з_рядком_7009000_бо_це_два_шляхи_до_того_самого_числа()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void F6_конверсія_кубометрів_у_тонни_через_щільність()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void F7_одиниця_на_рядок_приводить_тонни_і_кілограми_до_спільної_одиниці()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F9_крос_період_у_першому_періоді_дає_null_а_не_помилку()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F10_предикатний_діапазон_підсумовує_лише_рядки_за_умовою()
        => Assert.Fail("not implemented");
}
