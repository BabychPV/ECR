// tests/Ecr.Application.Tests/Calculations/SaveCalculationBindingIssuesCleanupTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// AN-160: вимкнення прив'язки викликає прибирання проблем розрахунку таблиці в тій самій транзакції; створення чи
/// повторне вмикання - ні. Мутація: прибрати виклик <c>DeleteForUnboundTablesAsync</c> з
/// <see cref="SaveCalculationBindingHandler"/> - червоніє перший тест.
/// </summary>
public sealed class SaveCalculationBindingIssuesCleanupTests
{
    private const int MethodologyId = 7;
    private const int TableDefId = 3;
    private const int ColumnDefId = 42;

    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ICalculationIssueStore _issues = Substitute.For<ICalculationIssueStore>();

    public SaveCalculationBindingIssuesCleanupTests()
    {
        _user.UserId.Returns(9);
        _clock.UtcNow.Returns(new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc));
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(SaveCalculationBindingHandler.Permission).Build());
        _drafts.FindAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns(new Methodology(
                EcrCode.Create("M1"), new LocalizedText(new Dictionary<string, string> { ["en"] = "M1" })));
        _bindings.FindColumnAsync(ColumnDefId, Arg.Any<CancellationToken>())
            .Returns(new BoundColumnRef(TableDefId, "Calc", CellDataType.Calculated));

        var version = new MethodologyVersion(
            MethodologyId, "1.0", CalculationLevel.Configuration, 9, new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, 70);
        _drafts.GetAllVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyVersion>)[version]);
        _methodologies.GetOutputsAsync(70, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyOutput>)[new MethodologyOutput(70, EcrCode.Create("OUT1"), 5)]);
    }

    private SaveCalculationBindingHandler Handler() => new(
        _bindings, _drafts, _methodologies, _uow, _access, _user, Substitute.For<IAuditWriter>(), _clock,
        Substitute.For<ITemplateVersionStore>(),
        Substitute.For<IRepository<TableDef, int>>(),
        Substitute.For<IRepository<SheetDef, int>>(),
        _issues);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Вимкнення_прив_язки_прибирає_проблеми_її_таблиці()
    {
        var existing = new CalculationBinding(TableDefId, ColumnDefId, MethodologyId, "OUT1", "{}");
        _bindings.FindAsync(ColumnDefId, MethodologyId, "OUT1", Arg.Any<CancellationToken>()).Returns(existing);

        await Handler().HandleAsync(MethodologyId, ColumnDefId, "OUT1", "{}", isActive: false, CancellationToken.None);

        await _issues.Received(1).DeleteForUnboundTablesAsync(
            null, null, Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { TableDefId })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Створення_активної_прив_язки_проблем_не_чіпає()
    {
        _bindings.FindAsync(ColumnDefId, MethodologyId, "OUT1", Arg.Any<CancellationToken>())
            .Returns((CalculationBinding?)null);

        await Handler().HandleAsync(MethodologyId, ColumnDefId, "OUT1", "{}", isActive: true, CancellationToken.None);

        await _issues.DidNotReceiveWithAnyArgs().DeleteForUnboundTablesAsync(default, default, default, default);
    }
}
