// tests/Ecr.Application.Tests/Permits/PermitSheetBindingGuardQueryTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>
/// Ціна охоронця видалення аркуша й таблиці (PM-09b2): аркуш без таблиць не питає прив'язок узагалі, решта - ОДИН
/// звернення <see cref="IPermitBindingStore.FindHoldAsync"/> по таблицях, і ніколи не перелік усіх прив'язок версії
/// (<see cref="IPermitBindingStore.ListAsync"/> тягне колонки й довідники).
/// </summary>
/// <remarks>
/// Мутаційний доказ (запущений): замінити в <c>RequireSheetFreeAsync</c> <c>FindHoldAsync</c> на <c>ListAsync</c> - червоніють
/// три з чотирьох («Аркуш_без_прив_язок…», «Аркуш_без_таблиць…», «Аркуш_з_прив_язаною…»). Нуль запитів для порожнього
/// переліку забезпечує сховище (<c>FindHoldAsync</c>), його доводить <c>PermitBindingHoldTests</c>.
/// </remarks>
public sealed class PermitSheetBindingGuardQueryTests
{
    [Fact]
    public async Task Аркуш_без_прив_язок_робить_один_запит_по_таблицях_а_не_перелік_прив_язок_версії()
    {
        var bindings = Substitute.For<IPermitBindingStore>();
        bindings.FindHoldAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns((PermitBindingHold?)null);
        var sheet = SheetWithTables(11, 12, 13);

        await PermitSheetBindingGuard.RequireSheetFreeAsync(bindings, sheet, default);

        await bindings.Received(1).FindHoldAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 3 && ids.Contains(11) && ids.Contains(12) && ids.Contains(13)), Arg.Any<CancellationToken>());
        await bindings.DidNotReceiveWithAnyArgs().ListAsync(default, default);
    }

    [Fact]
    public async Task Аркуш_без_таблиць_не_питає_прив_язок_узагалі()
    {
        var bindings = Substitute.For<IPermitBindingStore>();
        bindings.FindHoldAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns((PermitBindingHold?)null);

        await PermitSheetBindingGuard.RequireSheetFreeAsync(bindings, SheetWithTables(), default);

        await bindings.DidNotReceiveWithAnyArgs().ListAsync(default, default);
        await bindings.Received(1).FindHoldAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Аркуш_з_прив_язаною_таблицею_відмовляє_з_кодами_аркуша_таблиці_й_прив_язки()
    {
        var bindings = Substitute.For<IPermitBindingStore>();
        bindings.FindHoldAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(new PermitBindingHold(12, "B1"));
        var sheet = SheetWithTables(11, 12);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => PermitSheetBindingGuard.RequireSheetFreeAsync(bindings, sheet, default));

        Assert.Equal(("ECR-PRM-4102", "err.ECR-PRM-4102.sheet"), (error.ErrorCode, error.Details!["messageKey"]));
        Assert.Equal(("SH", "T12", "B1"), (error.Details["sheetCode"], error.Details["tableCode"], error.Details["bindingCode"]));
    }

    [Fact]
    public async Task Таблиця_питає_прив_язки_одним_запитом_по_собі_а_без_прив_язки_проходить()
    {
        var bindings = Substitute.For<IPermitBindingStore>();
        bindings.FindHoldAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns((PermitBindingHold?)null);
        var table = SheetWithTables(21).Tables.Single();

        await PermitSheetBindingGuard.RequireTableFreeAsync(bindings, table, default);

        await bindings.Received(1).FindHoldAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Single() == 21), Arg.Any<CancellationToken>());
        await bindings.DidNotReceiveWithAnyArgs().ListAsync(default, default);
    }

    private static SheetDef SheetWithTables(params int[] tableIds)
    {
        var sheet = new SheetDef(1, EcrCode.Create("SH"), Text("Sheet"), 1);
        foreach (var id in tableIds)
        {
            var table = new TableDef(0, EcrCode.Create($"T{id}"), Text($"T{id}"), id, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
            typeof(Entity<int>).GetProperty("Id")!.SetValue(table, id);
            sheet.AddTable(table);
        }

        return sheet;
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
