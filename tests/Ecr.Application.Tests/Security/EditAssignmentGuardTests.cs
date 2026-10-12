// tests/Ecr.Application.Tests/Security/EditAssignmentGuardTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// EA-B: таблиця рішень <see cref="EditAssignmentGuard"/> — виконавець /
/// не виконавець / власник / системний шлях × стани × ексклюзивність ×
/// селектори × перетин передач.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЇ (локально, не в кінчику lane): (1) «підйом при Unknown» — замінити
/// <c>match == Match.Yes</c> на <c>match != Match.No</c> — червоніє «невідомо → закрито»;
/// (2) AND → OR у перетині заборон (<c>locked != None ? … : lift</c> → пріоритет підйому) —
/// червоніє «заборона виграє»; (3) прибрати перевірку <c>DelegatorActive</c> — червоніє
/// «делегатор вимкнений»; (4) прибрати перевірку <c>ExpiresAt</c>; (5) прибрати пропуск
/// <c>isIntegrationWriter</c> — червоніє «системний шлях».
/// </remarks>
public sealed class EditAssignmentGuardTests
{
    private const int Owner = 1;
    private const int Executor = 7;
    private const int Other = 9;
    private const int Delegator = 2;
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly PeriodKey Year = new(202600);

    private static EditAssignmentFact Fact(
        byte? month = 1,
        string? sheet = null,
        string? table = null,
        PeriodKey? period = null,
        bool exclusive = true,
        bool validated = false,
        bool delegatorActive = true,
        DateTime? expires = null,
        AssigneeProgress? mine = null,
        int delegator = Delegator)
        => new(sheet, table, month, period, exclusive, validated, delegatorActive, delegator, expires, mine);

    private static EditSliceFacts Cell(
        byte? month = 1, bool isMonth = true, string? sheet = "F1", string? table = "T1",
        PeriodKey? period = null, bool row = false)
        => new(sheet, table, period ?? Year, isMonth, month, row);

    private static EditAccessFacts Eval(
        int user, IReadOnlyList<EditAssignmentFact> facts, EditSliceFacts slice, bool integration = false)
        => EditAssignmentGuard.Evaluate(user, Owner, integration, facts, slice, Now);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Виконавець_Assigned_піднімається_лише_у_своєму_місяці_і_лише_місячна_колонка()
    {
        var a = new[] { Fact(month: 1, mine: AssigneeProgress.Assigned) };

        Assert.True(Eval(Executor, a, Cell(month: 1)).DelegatedWrite);
        Assert.False(Eval(Executor, a, Cell(month: 2)).DelegatedWrite);
        Assert.False(Eval(Executor, a, Cell(month: null, isMonth: false)).DelegatedWrite);
        Assert.False(Eval(Executor, a, Cell(month: 1, isMonth: false)).DelegatedWrite);
        Assert.Equal(EditDenyReason.None, Eval(Executor, a, Cell(month: 2)).Lock);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Returned_знову_піднімає_а_Filled_і_Validated_замикають()
    {
        var returned = new[] { Fact(mine: AssigneeProgress.Returned) };
        var filled = new[] { Fact(mine: AssigneeProgress.Filled) };
        var validated = new[] { Fact(mine: AssigneeProgress.Filled, validated: true) };
        var validatedAssigned = new[] { Fact(mine: AssigneeProgress.Assigned, validated: true) };

        Assert.True(Eval(Executor, returned, Cell()).DelegatedWrite);

        var f = Eval(Executor, filled, Cell());
        Assert.False(f.DelegatedWrite);
        Assert.Equal(EditDenyReason.AssignmentFilled, f.Lock);

        Assert.Equal(EditDenyReason.AssignmentValidated, Eval(Executor, validated, Cell()).Lock);
        Assert.Equal(EditDenyReason.AssignmentValidated, Eval(Executor, validatedAssigned, Cell()).Lock);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Не_виконавець_замкнений_лише_ексклюзивною_передачею_того_самого_зрізу()
    {
        var exclusive = new[] { Fact(month: 1, exclusive: true, mine: null) };
        var shared = new[] { Fact(month: 1, exclusive: false, mine: null) };

        Assert.Equal(EditDenyReason.LockedByOwner, Eval(Other, exclusive, Cell(month: 1)).Lock);
        Assert.Equal(EditDenyReason.None, Eval(Other, exclusive, Cell(month: 2)).Lock);
        Assert.Equal(EditDenyReason.None, Eval(Other, shared, Cell(month: 1)).Lock);
        Assert.False(Eval(Other, shared, Cell(month: 1)).DelegatedWrite);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Власник_делегатор_і_системний_шлях_не_блокуються_й_не_піднімаються()
    {
        var a = new[] { Fact(month: 1, exclusive: true, mine: null, delegator: Delegator) };

        Assert.Equal(EditAccessFacts.None, Eval(Owner, a, Cell(month: 1)));
        Assert.Equal(EditAccessFacts.None, Eval(Delegator, a, Cell(month: 1)));
        Assert.Equal(EditAccessFacts.None, Eval(Other, a, Cell(month: 1), integration: true));

        var mine = new[] { Fact(month: 1, mine: AssigneeProgress.Filled) };
        Assert.Equal(EditAccessFacts.None, Eval(Executor, mine, Cell(month: 1), integration: true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Делегатор_вимкнений_або_строк_минув_передача_мертва()
    {
        var inactive = new[] { Fact(mine: AssigneeProgress.Assigned, delegatorActive: false) };
        var expired = new[] { Fact(mine: AssigneeProgress.Assigned, expires: Now) };
        var alive = new[] { Fact(mine: AssigneeProgress.Assigned, expires: Now.AddSeconds(1)) };
        var inactiveExclusive = new[] { Fact(mine: null, delegatorActive: false) };
        var expiredExclusive = new[] { Fact(mine: null, expires: Now) };

        Assert.False(Eval(Executor, inactive, Cell()).DelegatedWrite);
        Assert.False(Eval(Executor, expired, Cell()).DelegatedWrite);
        Assert.Equal(EditDenyReason.AssignmentExpired, Eval(Executor, expired, Cell()).Lock);
        Assert.True(Eval(Executor, alive, Cell()).DelegatedWrite);
        Assert.Equal(EditDenyReason.None, Eval(Other, inactiveExclusive, Cell()).Lock);

        // Прострочена ексклюзивність чужих не тримає; чужий місяць виконавця не зачіпає.
        Assert.Equal(EditDenyReason.None, Eval(Other, expiredExclusive, Cell()).Lock);
        Assert.Equal(EditDenyReason.None, Eval(Executor, expired, Cell(month: 5)).Lock);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Невідомо_закрито_для_підйому_і_консервативно_для_заборони()
    {
        var sheet = new[] { Fact(sheet: "F1", mine: AssigneeProgress.Assigned) };
        var unknown = Cell(sheet: null);

        Assert.False(Eval(Executor, sheet, unknown).DelegatedWrite);

        var filled = new[] { Fact(sheet: "F1", mine: AssigneeProgress.Filled) };
        Assert.Equal(EditDenyReason.AssignmentFilled, Eval(Executor, filled, unknown).Lock);

        var exclusive = new[] { Fact(sheet: "F1", mine: null) };
        Assert.Equal(EditDenyReason.LockedByOwner, Eval(Other, exclusive, unknown).Lock);

        // Відомий інший аркуш — не стосується.
        Assert.Equal(EditDenyReason.None, Eval(Other, exclusive, Cell(sheet: "F2")).Lock);
        Assert.False(Eval(Executor, sheet, Cell(sheet: "F2")).DelegatedWrite);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Селектори_аркуша_таблиці_і_періоду_діють_разом_AND()
    {
        var a = new[]
        {
            Fact(month: 3, sheet: "F1", table: "T1", period: new PeriodKey(202600), mine: AssigneeProgress.Assigned),
        };

        Assert.True(Eval(Executor, a, Cell(month: 3, sheet: "f1")).DelegatedWrite);
        Assert.False(Eval(Executor, a, Cell(month: 3, table: "T2")).DelegatedWrite);
        Assert.False(Eval(Executor, a, Cell(month: 3, period: new PeriodKey(202700))).DelegatedWrite);
        Assert.False(Eval(Executor, a, Cell(month: 4)).DelegatedWrite);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Селектор_періоду_без_місяця_піднімає_будь_яку_колонку_зрізу()
    {
        var a = new[] { Fact(month: null, period: new PeriodKey(202601), mine: AssigneeProgress.Assigned) };

        Assert.True(Eval(Executor, a, Cell(month: null, isMonth: false, period: new PeriodKey(202601))).DelegatedWrite);
        Assert.False(Eval(Executor, a, Cell(month: null, isMonth: false, period: new PeriodKey(202602))).DelegatedWrite);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Перетин_передач_заборона_виграє_а_підйом_дає_будь_яка_активна()
    {
        // X: виконавець Assigned на місяць 1; Y: той самий місяць, він там не виконавець, ексклюзивна.
        var lift = new[]
        {
            Fact(month: 1, mine: AssigneeProgress.Assigned, sheet: "F1"),
            Fact(month: 1, mine: AssigneeProgress.Assigned, sheet: "F2"),
        };
        Assert.True(Eval(Executor, lift, Cell(month: 1, sheet: "F2")).DelegatedWrite);

        var both = new[]
        {
            Fact(month: 1, mine: AssigneeProgress.Assigned),
            Fact(month: 1, mine: null, exclusive: true),
        };
        var r = Eval(Executor, both, Cell(month: 1));
        Assert.False(r.DelegatedWrite);
        Assert.Equal(EditDenyReason.LockedByOwner, r.Lock);

        var strongest = new[]
        {
            Fact(month: 1, mine: AssigneeProgress.Filled),
            Fact(month: 1, mine: AssigneeProgress.Assigned, validated: true),
            Fact(month: 1, mine: null, exclusive: true),
        };
        Assert.Equal(EditDenyReason.AssignmentValidated, Eval(Executor, strongest, Cell(month: 1)).Lock);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Рівень_рядка_без_підйому_і_без_селектора_місяця()
    {
        var monthly = new[] { Fact(month: 1, mine: AssigneeProgress.Assigned, exclusive: true) };
        var wholeSheet = new[] { Fact(month: null, sheet: "F1", mine: AssigneeProgress.Assigned) };
        var exclusiveSheet = new[] { Fact(month: null, sheet: "F1", mine: null, exclusive: true) };

        Assert.Equal(EditAccessFacts.None, Eval(Executor, monthly, Cell(row: true)));
        Assert.False(Eval(Executor, wholeSheet, Cell(row: true)).DelegatedWrite);
        Assert.Equal(EditDenyReason.LockedByOwner, Eval(Other, exclusiveSheet, Cell(row: true)).Lock);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Narrow_відсіює_лише_точно_нерелевантні_передачі()
    {
        var all = new[]
        {
            Fact(sheet: "F1"),
            Fact(sheet: "F2"),
            Fact(sheet: null),
            Fact(table: "T9"),
            Fact(period: new PeriodKey(202500)),
        };

        var narrowed = EditAssignmentGuard.Narrow(all, Cell(sheet: "F1", table: "T1"));

        Assert.Equal(2, narrowed.Count);
        Assert.Contains(all[0], narrowed);
        Assert.Contains(all[2], narrowed);

        var unknownSheet = EditAssignmentGuard.Narrow(all, Cell(sheet: null, table: "T1"));
        Assert.Equal(3, unknownSheet.Count);
    }
}
