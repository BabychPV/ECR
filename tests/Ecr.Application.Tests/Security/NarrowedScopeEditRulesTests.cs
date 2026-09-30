// tests/Ecr.Application.Tests/Security/NarrowedScopeEditRulesTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// D-214: роль, чия область звужена аркушами чи періодами, діє лише на своїх
/// аркушах і у своєму проміжку періодів — і більше ніде.
/// </summary>
public sealed class NarrowedScopeEditRulesTests
{
    private const int NarrowedRole = 6;
    private const int OtherSheet = 21;
    private const string SheetX = "F1";
    private const string SheetY = "F2";
    private static readonly PeriodKey Inside = new(202603);
    private static readonly PeriodKey Before = new(202512);
    private static readonly PeriodKey After = new(202607);

    private static string ProjectKey => $"{ResourceKind.Project}:{AccessBuilder.ProjectId}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Роль_з_аркушем_дає_свій_рівень_лише_на_ньому()
    {
        var profile = Narrowed(sheets: [SheetX], level: GrantLevel.Submit);
        var onX = Cell(SheetX, Inside, DocumentStatus.Draft);
        var onY = Cell(SheetY, Inside, DocumentStatus.Draft) with { SheetDefId = OtherSheet };

        Assert.Equal(GrantLevel.Submit, EditRules.Effective(profile, onX));
        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, onY));
        Assert.True(EditRules.CanSubmit(profile, onX, hasBlockingErrors: false).IsAllowed);
        Assert.Equal(EditDenyReason.NoGrant, EditRules.CanSubmit(profile, onY, hasBlockingErrors: false).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Невідомий_аркуш_при_звуженні_за_аркушами_закритий()
    {
        var profile = Narrowed(sheets: [SheetX], level: GrantLevel.Write);

        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, Cell(sheetCode: null, Inside)));
    }

    [Theory]
    [InlineData(202603, GrantLevel.Write)]
    [InlineData(202601, GrantLevel.Write)]
    [InlineData(202606, GrantLevel.Write)]
    [InlineData(202512, GrantLevel.None)]
    [InlineData(202607, GrantLevel.None)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Роль_з_періодами_діє_у_включних_межах(int period, GrantLevel expected)
    {
        var profile = Narrowed(from: new PeriodKey(202601), to: new PeriodKey(202606), level: GrantLevel.Write);

        Assert.Equal(expected, EditRules.Effective(profile, Cell(SheetX, new PeriodKey(period))));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Відкрита_межа_періоду_не_обмежує_свій_бік()
    {
        var fromOnly = Narrowed(from: Inside, level: GrantLevel.Read);
        var toOnly = Narrowed(to: Inside, level: GrantLevel.Read);

        Assert.Equal(GrantLevel.Read, EditRules.Effective(fromOnly, Cell(SheetX, After)));
        Assert.Equal(GrantLevel.None, EditRules.Effective(fromOnly, Cell(SheetX, Before)));
        Assert.Equal(GrantLevel.Read, EditRules.Effective(toOnly, Cell(SheetX, Before)));
        Assert.Equal(GrantLevel.None, EditRules.Effective(toOnly, Cell(SheetX, After)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Невідомий_період_при_звуженні_за_періодами_закритий()
    {
        var profile = Narrowed(from: Before, to: After, level: GrantLevel.Read);

        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, Cell(SheetX, period: null)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Аркуш_і_період_перетинаються()
    {
        var profile = Narrowed(sheets: [SheetX], from: Inside, to: Inside, level: GrantLevel.Write);

        Assert.Equal(GrantLevel.Write, EditRules.Effective(profile, Cell(SheetX, Inside)));
        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, Cell(SheetY, Inside)));
        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, Cell(SheetX, After)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Заборона_звуженої_ролі_діє_лише_в_її_області()
    {
        var profile = Narrowed(
            sheets: [SheetX],
            level: GrantLevel.Write,
            unscoped: new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write),
            denyColumn: true);

        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, Cell(SheetX, Inside)));
        Assert.Equal(GrantLevel.Write, EditRules.Effective(profile, Cell(SheetY, Inside)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Звужена_роль_відкриває_документ_але_не_піднімає_рівень_проєкту()
    {
        var profile = Narrowed(sheets: [SheetX], level: GrantLevel.Manage);

        Assert.True(profile.SeesDocumentsOf(AccessBuilder.ProjectId));
        Assert.False(profile.SeesDocumentsOf(AccessBuilder.ProjectId + 1));
        Assert.Equal(GrantLevel.None, profile.LevelFor(ResourceKind.Project, AccessBuilder.ProjectId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Роль_кроку_маршруту_лише_на_своєму_аркуші()
    {
        var profile = Narrowed(
            sheets: [SheetX],
            level: GrantLevel.Approve,
            unscoped: new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Approve));

        Assert.True(EditRules.CanApprove(profile, Cell(SheetX, Inside, DocumentStatus.Submitted), NarrowedRole).IsAllowed);
        Assert.False(EditRules.CanApprove(profile, Cell(SheetY, Inside, DocumentStatus.Submitted), NarrowedRole).IsAllowed);
        Assert.DoesNotContain(NarrowedRole, profile.RoleIdsIn(AccessBuilder.ProjectId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Права_звуженої_ролі_діють_на_рівні_документа()
    {
        var profile = Narrowed(sheets: [SheetX], level: GrantLevel.Read, permissions: ["Document.View"]);

        Assert.True(profile.Has("Document.View", AccessBuilder.ProjectId));
        Assert.False(profile.Has("Document.View", AccessBuilder.ProjectId + 1));
        Assert.False(profile.Has("Document.View"));
        Assert.True(profile.HasInAnyProject("Document.View"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Межі_читання_ховають_чужий_аркуш_і_чужий_період()
    {
        var snapshot = Snapshot();
        var profile = Narrowed(sheets: [SheetX], from: Inside, to: Inside, level: GrantLevel.Read);

        var anyPeriod = DocumentReadScope.For(profile, AccessBuilder.ProjectId, snapshot);
        var inside = anyPeriod.InPeriod(Inside);
        var outside = anyPeriod.InPeriod(After);

        Assert.Equal([100, 200], anyPeriod.HiddenTableIds());
        Assert.Equal([200], inside.HiddenTableIds());
        Assert.Equal([100, 200], outside.HiddenTableIds());
    }

    private static CellAccessContext Cell(
        string? sheetCode, PeriodKey? period, DocumentStatus sheet = DocumentStatus.Draft)
        => AccessBuilder.Cell(sheet: sheet) with { SheetCode = sheetCode, Period = period };

    /// <summary>Профіль: ролі <paramref name="unscoped"/> плюс роль <see cref="NarrowedRole"/> зі звуженою областю в проєкті.</summary>
    private static AccessProfile Narrowed(
        GrantLevel level,
        string[]? sheets = null,
        PeriodKey? from = null,
        PeriodKey? to = null,
        AccessBuilder? unscoped = null,
        bool denyColumn = false,
        string[]? permissions = null)
    {
        var baseline = (unscoped ?? new AccessBuilder()).Build();
        var denies = new HashSet<string>(StringComparer.Ordinal);
        if (denyColumn)
        {
            denies.Add($"{ResourceKind.Column}:{AccessBuilder.ColumnId}");
        }

        var layer = new NarrowedAccess(
            sheets?.ToHashSet(StringComparer.Ordinal),
            from,
            to,
            new Dictionary<string, GrantLevel>(StringComparer.Ordinal) { [ProjectKey] = level },
            denies,
            new HashSet<int> { NarrowedRole },
            (permissions ?? []).ToHashSet(StringComparer.Ordinal));

        return new AccessProfile
        {
            CacheKey = baseline.CacheKey,
            UserId = baseline.UserId,
            SecurityStamp = baseline.SecurityStamp,
            Permissions = baseline.Permissions,
            Grants = baseline.Grants,
            Denies = baseline.Denies,
            RoleIds = new HashSet<int>(baseline.RoleIds) { NarrowedRole },
            UnscopedRoleIds = baseline.RoleIds,
            Scoped = new Dictionary<int, ScopedProjectAccess>
            {
                [AccessBuilder.ProjectId] = new ScopedProjectAccess(
                    new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
                    new HashSet<string>(StringComparer.Ordinal),
                    baseline.RoleIds,
                    new HashSet<string>(StringComparer.Ordinal))
                {
                    Narrowed = [layer],
                },
            },
        };
    }

    /// <summary>Два аркуші — <c>F1</c> (таблиця 100) і <c>F2</c> (таблиця 200), по колонці.</summary>
    private static TemplateVersionSnapshot Snapshot()
    {
        var columns = new Dictionary<int, ColumnDef>();
        var x = Sheet(AccessBuilder.SheetId, SheetX);
        var y = Sheet(OtherSheet, SheetY);
        x.AddTable(Table(100, AccessBuilder.SheetId, 1000, columns));
        y.AddTable(Table(200, OtherSheet, 2000, columns));

        return new TemplateVersionSnapshot(1, 0, [x, y], columns, new Dictionary<(int, string), RowDef>());
    }

    private static SheetDef Sheet(int id, string code)
    {
        var sheet = new SheetDef(1, EcrCode.Create(code), Text(code), id);
        SetId(sheet, id);
        return sheet;
    }

    private static TableDef Table(int id, int sheetId, int columnId, Dictionary<int, ColumnDef> index)
    {
        var table = new TableDef(
            sheetId, EcrCode.Create("T" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Text("T"), id, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, id);

        var column = new ColumnDef(
            id, EcrCode.Create("C" + columnId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Text("C"), columnId, CellDataType.Decimal);
        SetId(column, columnId);
        table.AddColumn(column);
        index[columnId] = column;

        return table;
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T e, int id) where T : Ecr.Domain.Abstractions.Entity<int>
        => typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(e, id);
}
