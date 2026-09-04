using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Units;

/// <summary>
/// Маршрут конверсії з чотирьох кроків і, головне, **відмова** при різних
/// розмірностях: це те, що не дає щільності стати «конверсією» (ФВ-16.3, 16.5).
/// </summary>
public sealed class UnitConverterTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_в_ту_саму_одиницю_повертає_значення_без_змін()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Тонни_у_кілограми_множаться_на_тисячу()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Явна_конверсія_має_пріоритет_над_маршрутом_через_базову_одиницю()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_градусів_Цельсія_у_Кельвіни_враховує_зсув()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_між_різними_розмірностями_кидає_ECR_UOM_0422()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_кубометрів_у_кілограми_неможлива_бо_це_контекстний_коефіцієнт()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_не_втрачає_точності_на_decimal()
        => Assert.Fail("not implemented");
}
