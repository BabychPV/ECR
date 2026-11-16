// tests/Ecr.Application.Tests/Documents/RowLockF13Tests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents.RowLocks;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ B75 (F13): мутації, що вижили, — перевірка під замком пропускає інтеграцію (Q2, M12); режим None відхиляється для
/// версії з маршрутом погодження з кроками (M14); таблиця ідентифікується разом з аркушем (D2: код таблиці унікальний
/// лише в межах аркуша).
/// </summary>
/// <remarks>
/// Мутації: (1) у <c>RowLockRecheck.LockedAsync</c> прибрати <c>IsIntegrationWriter</c> — червоніє «Інтеграція…»;
/// (2) прибрати перевірку <c>HasApprovalRouteStepsAsync</c> — червоніє «None_при_маршруті…»; (3) у <c>FindTable</c> чи
/// <c>AnyLocked</c> не порівнювати код аркуша — червоніють «…два_аркуші…».
/// </remarks>
public sealed class RowLockF13Tests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75-Q2")]
    public async Task Інтеграція_під_замком_не_перевіряє_блокувань_рядків()
    {
        var gate = Substitute.For<ISheetEditGate>();
        gate.RowLocksSeen(Arg.Any<long>(), Arg.Any<int>()).Returns(true);
        var store = Substitute.For<IRowLockStore>();
        store.LockedByOthersAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<RowLockKey>>([new RowLockKey(null, "S", "T", "R")]));

        var own = new AccessBuilder().Build();
        var integration = new AccessProfile
        {
            CacheKey = own.CacheKey + "|integration-job",
            UserId = own.UserId,
            SecurityStamp = own.SecurityStamp,
            Permissions = own.Permissions,
            Grants = own.Grants,
            Denies = own.Denies,
            RoleIds = own.RoleIds,
            IsIntegrationWriter = true,
        };

        var human = await RowLockRecheck.LockedAsync(gate, store, own, 1, 202611, DateTime.UtcNow, CancellationToken.None);
        var system = await RowLockRecheck.LockedAsync(gate, store, integration, 1, 202611, DateTime.UtcNow, CancellationToken.None);

        Assert.Single(human);
        Assert.Empty(system);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75-D2")]
    public void Таблиця_з_тим_самим_кодом_на_двох_аркушах_розрізняється_аркушем()
    {
        var first = Sheet(1, "A");
        var second = Sheet(2, "B");
        var snapshot = new TemplateVersionSnapshot(
            7, 0, [first.Sheet, second.Sheet], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>());

        Assert.Equal(first.Table.Id, RowLockRules.FindTable(snapshot, "A", "T1")?.Table.Id);
        Assert.Equal(second.Table.Id, RowLockRules.FindTable(snapshot, "B", "T1")?.Table.Id);
        Assert.Null(RowLockRules.FindTable(snapshot, "C", "T1"));

        var locked = new HashSet<RowLockKey> { new(null, "A", "T1", "R1") };
        Assert.True(RowLockRecheck.AnyLocked(locked, null, "A", "T1", ["R1"]));
        Assert.False(RowLockRecheck.AnyLocked(locked, null, "B", "T1", ["R1"]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75-Q4")]
    public async Task None_при_маршруті_погодження_з_кроками_422_і_нічого_не_змінено()
    {
        var now = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
        var version = new TemplateVersion(1, "1.0", 5, now);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(version, 42);

        var store = Substitute.For<ITemplateVersionStore>();
        store.GetWithStructureAsync(42, Arg.Any<CancellationToken>()).Returns(version);
        store.HasDocumentsAsync(42, Arg.Any<CancellationToken>()).Returns(false);
        store.HasApprovalRouteStepsAsync(42, Arg.Any<CancellationToken>()).Returns(true);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(5, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 5 }.Permission(SetTemplateWorkflowModeHandler.Permission).Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(5);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now);
        var uow = Substitute.For<IUnitOfWork>();

        var handler = new SetTemplateWorkflowModeHandler(
            store, Substitute.For<IMetadataCache>(), Substitute.For<IAuditWriter>(), uow, clock, access, user);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(42, WorkflowMode.None, CancellationToken.None));

        Assert.Equal("err.ECR-TMPL-0422.workflowModeHasRoute", ex.Details?["messageKey"]?.ToString());
        Assert.Equal(WorkflowMode.SubmitApprove, version.WorkflowMode);
        await uow.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    private static (SheetDef Sheet, TableDef Table) Sheet(int id, string code)
    {
        var sheet = new SheetDef(7, EcrCode.Create(code), Text(code), id);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, id);
        var table = new TableDef(id, EcrCode.Create("T1"), Text("T1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, 100 + id);
        sheet.AddTable(table);
        return (sheet, table);
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
