// tests/Ecr.Domain.Tests/Documents/TableRowTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Рядок таблиці. Головне тут — <c>ModifiedAt</c>: без його підняття
/// <c>RowVersion</c> не змінюється, і оптимістичне блокування **тихо не
/// працює** (B04 §2.4).
/// </summary>
public sealed class TableRowTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Touch_піднімає_ModifiedAt()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_комірки_рядка_піднімає_ModifiedAt_рядка()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Soft_delete_лишає_рядок_у_сховищі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void RowKey_динамічного_рядка_є_GUID_у_форматі_N()
        => Assert.Fail("not implemented");
}
