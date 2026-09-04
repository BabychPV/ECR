// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotBuilderTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Побудова зрізів для звітності. У `rpt.*` потрапляють **усі** зрізи,
/// включно з `Draft` — щоб числа можна було перевірити **до** затвердження
/// (D-65). Фільтр для регулятора стоїть у вʼюсі, не в RDL (ФВ-10.11).
/// </summary>
public sealed class ReportSnapshotBuilderTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Статус_зрізу_успадковується_від_стану_даних()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Чернеткові_зрізи_теж_потрапляють_у_rpt()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Вʼюха_для_регулятора_віддає_лише_Approved_і_Submitted()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void IsCurrent_перемикається_однією_транзакцією()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Поданий_зріз_не_перебудовується_ніколи()
        => Assert.Fail("not implemented");
}
