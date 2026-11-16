// tests/Ecr.Application.Tests/Security/DependentMonthsRuleTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Recalculation;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// ✎ B79 (рішення людини 13.11): чисте правило «залежні затверджені місяці» й індекс «стовпець → є залежні формули».
/// </summary>
/// <remarks>
/// ⛔ Безпека запису. МУТАЦІЇ (локально): (1) <c>DependentMonthsRule.Blocks</c> → <c>false</c> — червоніють
/// «Затверджений_місяць…», «Під_блокуванням…»; (2) <c>Blocks</c> додати <c>Submitted</c> — червоніє «Поданий_чернетка…»;
/// (3) в <c>Apply</c> прибрати <c>!decision.IsAllowed</c> — червоніє «Наявна_відмова_перемагає…».
/// </remarks>
public sealed class DependentMonthsRuleTests
{
    private const int Jan = 209901;
    private const int Feb = 209902;
    private const int Mar = 209903;

    private static AccessProfile Writer()
        => new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write).Build();

    private static IReadOnlyList<int> Blocking(params (int Period, DocumentStatus Status)[] months)
    {
        var statuses = months.ToDictionary(m => m.Period, m => m.Status);
        return DependentMonthsRule.BlockingMonths(statuses.Keys, p => statuses[p]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Затверджений_місяць_блокує_і_названий_у_відмові()
    {
        var blocking = Blocking((Mar, DocumentStatus.Approved), (Jan, DocumentStatus.Approved), (Feb, DocumentStatus.Draft));

        var decision = DependentMonthsRule.Apply(EditRules.CanEdit(Writer(), AccessBuilder.Cell()), blocking);

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.DependentMonthsLocked, decision.Reason);
        Assert.Equal(new[] { Jan, Mar }, decision.BlockingMonths);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.Rejected)]
    public void Поданий_чернетка_й_відхилений_місяць_не_блокують(DocumentStatus status)
    {
        var blocking = Blocking((Jan, status), (Feb, DocumentStatus.Draft));

        var decision = DependentMonthsRule.Apply(EditRules.CanEdit(Writer(), AccessBuilder.Cell()), blocking);

        Assert.Empty(blocking);
        Assert.True(decision.IsAllowed);
        Assert.Null(decision.BlockingMonths);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Наявна_відмова_перемагає_причина_і_подробиця_не_підміняються()
    {
        var blocking = Blocking((Jan, DocumentStatus.Approved));
        var reader = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read).Build();
        var stranger = new AccessBuilder { UserId = 8 }.Build();

        var rowLocked = EditRules.CanEdit(Writer(), AccessBuilder.Cell() with { RowLockedByOther = true });
        var noGrant = EditRules.CanEdit(stranger, AccessBuilder.Cell());
        var lowLevel = EditRules.CanEdit(reader, AccessBuilder.Cell());
        var closed = EditRules.CanEdit(Writer(), AccessBuilder.Cell(period: PeriodState.Closed));

        Assert.Equal(EditDenyReason.RowLocked, DependentMonthsRule.Apply(rowLocked, blocking).Reason);
        Assert.Equal(EditDenyReason.NoGrant, DependentMonthsRule.Apply(noGrant, blocking).Reason);
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, DependentMonthsRule.Apply(lowLevel, blocking).Reason);
        Assert.Equal(EditDenyReason.PeriodClosed, DependentMonthsRule.Apply(closed, blocking).Reason);
        Assert.Null(DependentMonthsRule.Apply(noGrant, blocking).BlockingMonths);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Дозвіл_з_підтвердженням_чи_позначкою_теж_стає_відмовою()
    {
        var blocking = Blocking((Jan, DocumentStatus.Approved));

        Assert.Equal(
            EditDenyReason.DependentMonthsLocked,
            DependentMonthsRule.Apply(EditDecision.AllowWithConfirmation("вікно"), blocking).Reason);
        Assert.False(DependentMonthsRule.Apply(EditDecision.AllowMarked("вікно"), blocking).IsAllowed);
        Assert.False(DependentMonthsRule.Apply(EditDecision.AllowWithConfirmation("вікно"), blocking).RequiresConfirmation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Під_блокуванням_затверджений_місяць_відхиляє_з_переліком_а_поданий_ні()
    {
        var locked = new Dictionary<int, DocumentStatus>
        {
            [Jan] = DocumentStatus.Approved,
            [Feb] = DocumentStatus.Submitted,
            [Mar] = DocumentStatus.Approved,
        };

        var denied = Assert.Throws<AccessDeniedException>(
            () => DependentMonthsRule.ThrowIfLocked([Jan, Feb, Mar], locked, deniedCount: 2));

        Assert.Equal("ECR-ACCS-0403", denied.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.deniedCells", denied.Details?["messageKey"]);
        Assert.Equal(nameof(EditDenyReason.DependentMonthsLocked), denied.Details?["reason"]);
        Assert.Equal("2", denied.Details?["deniedCount"]);
        Assert.Equal(new[] { Jan, Mar }, Assert.IsType<int[]>(denied.Details?[DependentMonthsRule.MonthsDetail]));
        Assert.Equal("209901, 209903", denied.Details?[DependentMonthsRule.MonthsTextDetail]);

        locked[Jan] = DocumentStatus.Submitted;
        locked[Mar] = DocumentStatus.Rejected;
        DependentMonthsRule.ThrowIfLocked([Jan, Feb, Mar], locked, deniedCount: 2);
    }

    // ── індекс «стовпець → є залежні формули» ──

    private const int Table = 7;
    private const int OtherTable = 8;
    private const int Fuel = 71;
    private const int Free = 72;

    private static FormulaDependency Edge(
        byte kind, int? table, int? column, short? offset = null, string? rowKey = "R1", string? filter = null)
        => FormulaDependency.ForFormula(100, kind, table, rowKey, column, filter, offset, sortOrder: 0);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Стовпець_який_читає_формула_коміркою_має_залежні_а_сусідній_ні()
    {
        var index = GeneralInputDependents.From([Edge(kind: 0, Table, Fuel), Edge(kind: 0, Table, Fuel, offset: 0)]);

        Assert.True(index.Reads(Table, Fuel));
        Assert.False(index.Reads(Table, Free));
        Assert.False(index.Reads(OtherTable, Fuel));
        Assert.False(index.IsEmpty);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Міжперіодне_ребро_й_предикат_теж_роблять_стовпець_залежним()
    {
        var crossPeriod = GeneralInputDependents.From([Edge(kind: 3, Table, Fuel, offset: -1)]);
        var absolute = GeneralInputDependents.From([Edge(kind: 5, Table, Fuel, offset: 4)]);
        var predicate = GeneralInputDependents.From(
            [Edge(kind: 3, Table, Free, offset: -1, rowKey: null, filter: "{\"x\":1}")]);
        var wholeTable = GeneralInputDependents.From([Edge(kind: 0, Table, column: null)]);

        Assert.True(crossPeriod.Reads(Table, Fuel));
        Assert.False(crossPeriod.Reads(Table, Free));
        Assert.True(absolute.Reads(Table, Fuel));
        Assert.True(predicate.Reads(Table, Fuel));
        Assert.True(wholeTable.Reads(Table, Free));
        Assert.False(wholeTable.Reads(OtherTable, Free));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Шапка_довідник_інший_проєкт_і_прив_язка_методології_залежними_не_вважаються()
    {
        var index = GeneralInputDependents.From(
        [
            Edge(kind: 1, Table, Fuel),
            Edge(kind: 2, Table, Fuel),
            Edge(kind: 4, Table, Fuel),
            Edge(kind: 0, table: null, Fuel),
            Edge(kind: 3, Table, Fuel, offset: 0),
            FormulaDependency.ForBinding(5, 0, Table, "R1", Fuel, 0),
        ]);

        Assert.False(index.Reads(Table, Fuel));
        Assert.True(index.IsEmpty);
        Assert.Same(GeneralInputDependents.None, index);
    }

    private const int SheetA = 501;
    private const int SheetB = 502;
    private const int SheetC = 503;

    /// <summary>
    /// ✎ Квіз 16.11 (людина: «і користувач має побачити чому»): індекс знає АРКУШ формули-читача — стовпця (точно) і
    /// всієї таблиці (предикат, посилання без стовпця); аркуші об'єднуються. Невідомий аркуш (старий вхід без аркуша) —
    /// <see cref="GeneralInputDependents.UnknownReaderSheet"/>.
    /// </summary>
    /// <remarks>⛔ МУТАЦІЇ (локально): у <c>From</c> не додавати аркуш читача (<c>readers.Add</c>) — червоніє; у
    /// <c>ReaderSheets</c> не поєднувати читачів таблиці зі стовпцевими — червоніє рядок <c>SheetC</c>.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Індекс_знає_аркуш_формули_читача_стовпця_і_таблиці()
    {
        var index = GeneralInputDependents.From(new[]
        {
            (Edge(kind: 0, Table, Fuel), SheetB),
            (Edge(kind: 3, Table, Fuel, offset: -1), SheetA),
            (Edge(kind: 0, Table, column: null), SheetC),
        });

        Assert.Equal(new[] { SheetA, SheetB, SheetC }, index.ReaderSheets(Table, Fuel).Order());
        Assert.Equal(new[] { SheetC }, index.ReaderSheets(Table, Free).Order());
        Assert.Empty(index.ReaderSheets(OtherTable, Fuel));
        Assert.Equal(new[] { SheetA, SheetB, SheetC }, index.AllReaderSheets().Order());

        var legacy = GeneralInputDependents.From([Edge(kind: 0, Table, Fuel)]);
        Assert.Equal(new[] { GeneralInputDependents.UnknownReaderSheet }, legacy.ReaderSheets(Table, Fuel));
        Assert.Empty(legacy.AllReaderSheets());
    }

    /// <summary>
    /// ✎ Квіз 16.11: відмова за аркушами-читачами — місяці об'єднано за зростанням, аркуш без затверджених місяців у
    /// відмову не йде; жодного затвердженого місяця — дозвіл лишається.
    /// </summary>
    /// <remarks>⛔ МУТАЦІЇ (локально): у <c>Apply</c> за аркушами не відкидати аркуші без місяців — червоніє
    /// <c>Assert.Single</c>; не передавати аркуші в рішення (<c>BlockingSheets = null</c>) — червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Відмова_називає_аркуші_читачі_з_їхніми_затвердженими_місяцями()
    {
        var allowed = EditRules.CanEdit(Writer(), AccessBuilder.Cell());

        var denied = DependentMonthsRule.Apply(
            allowed,
            [
                new DependentMonthsBlock(SheetB, "B", [Mar]),
                new DependentMonthsBlock(SheetC, "C", []),
                new DependentMonthsBlock(SheetA, "A", [Jan, Mar]),
            ]);

        Assert.Equal(EditDenyReason.DependentMonthsLocked, denied.Reason);
        Assert.Equal(new[] { Jan, Mar }, denied.BlockingMonths);
        Assert.Equal(["B", "A"], denied.BlockingSheets!.Select(b => b.SheetCode).ToArray());

        var stillAllowed = DependentMonthsRule.Apply(allowed, [new DependentMonthsBlock(SheetC, "C", [])]);
        Assert.True(stillAllowed.IsAllowed);
        Assert.Null(stillAllowed.BlockingSheets);

        var single = DependentMonthsRule.Apply(allowed, [new DependentMonthsBlock(SheetB, "B", [Feb])]);
        Assert.Single(single.BlockingSheets!);
    }

    /// <summary>✎ Квіз 16.11: відмова 403 несе <c>sheets</c> (аркуш і його місяці), коли аркуші відомі; без них — ні.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Відмова_403_несе_аркуші_коли_вони_відомі()
    {
        var withSheets = DependentMonthsRule.Denied(2, [Jan, Mar], [new DependentMonthsBlock(SheetB, "B", [Mar])]);
        var withoutSheets = DependentMonthsRule.Denied(1, [Jan]);

        var sheets = Assert.IsType<Dictionary<string, object?>[]>(withSheets.Details![DependentMonthsRule.SheetsDetail]);
        Assert.Equal("B", Assert.Single(sheets)["sheetCode"]);
        Assert.Equal(new[] { Mar }, sheets[0][DependentMonthsRule.MonthsDetail]);
        Assert.False(withoutSheets.Details!.ContainsKey(DependentMonthsRule.SheetsDetail));
    }
}
