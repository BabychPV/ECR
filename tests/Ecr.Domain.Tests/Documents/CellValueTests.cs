// tests/Ecr.Domain.Tests/Documents/CellValueTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Комірка. Три стани розрізняються завжди: **є значення** / **явна
/// порожнеча** (`IsEmpty`) / **комірки немає** (ФВ-3.8, R-B4).
/// </summary>
/// <remarks>
/// Формула трактує другий і третій однаково, але аудит і експорт — ні. Злиття
/// цих станів здається спрощенням рівно доти, доки не треба довести, що
/// користувач свідомо лишив клітинку порожньою.
/// </remarks>
public sealed class CellValueTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Apply_замінює_значення_і_тип()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void ToData_віддає_рівно_те_що_записано()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_і_відсутня_комірка_це_різні_стани()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void ValueUnitId_приймається_лише_для_DataType_Unit()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнення_двох_типізованих_колонок_одночасно_відхиляється()
        => Assert.Fail("not implemented");
}
