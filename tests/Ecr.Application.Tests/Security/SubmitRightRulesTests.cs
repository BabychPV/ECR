// tests/Ecr.Application.Tests/Security/SubmitRightRulesTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// D-285 (варіант B′): подання аркуша дозволене, якщо ефективний рівень гранта
/// ≥ <c>Submit</c> АБО (≥ <c>Write</c> І профіль має проєктне право
/// <c>Document.Submit</c> у проєкті документа). Сам по собі <c>Write</c>
/// подання НЕ дає.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази (по одному на гілку):
/// (а) прибрати гілку права з <c>EditRules.CanSubmit</c> — червоніє
/// «Write + право подає»; (б) зробити <c>Write</c> достатнім без права —
/// червоніє «Write без права не подає». Кожен рядок нижче — рішення, яке
/// ДО D-285 було іншим тільки там, де це позначено «ДО».
/// </remarks>
public sealed class SubmitRightRulesTests
{
    private const string Right = "Document.Submit";

    private static AccessProfile Writer(bool withRight)
    {
        var builder = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write);
        return (withRight ? builder.Permission(Right) : builder).Build();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-285")]
    public void Write_без_права_Submit_не_подає_як_і_раніше()
    {
        var decision = EditRules.CanSubmit(Writer(withRight: false), AccessBuilder.Cell(), hasBlockingErrors: false);

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-285")]
    public void Write_з_правом_Submit_подає_ДО_було_InsufficientGrantLevel()
    {
        var decision = EditRules.CanSubmit(Writer(withRight: true), AccessBuilder.Cell(), hasBlockingErrors: false);

        Assert.True(decision.IsAllowed);
        Assert.Equal(EditDenyReason.None, decision.Reason);
    }

    [Theory]
    [InlineData(GrantLevel.Submit)]
    [InlineData(GrantLevel.Approve)]
    [InlineData(GrantLevel.Manage)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-285")]
    public void Рівень_Submit_і_вище_подає_з_правом_і_без_нього(GrantLevel level)
    {
        foreach (var withRight in new[] { false, true })
        {
            var builder = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, level);
            var profile = (withRight ? builder.Permission(Right) : builder).Build();

            Assert.True(
                EditRules.CanSubmit(profile, AccessBuilder.Cell(), hasBlockingErrors: false).IsAllowed,
                $"{level}, право={withRight}");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-285")]
    public void Read_з_правом_Submit_не_подає_а_без_гранта_NoGrant()
    {
        var reader = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Permission(Right).Build();
        var stranger = new AccessBuilder { UserId = 99 }.Permission(Right).Build();

        var readerDecision = EditRules.CanSubmit(reader, AccessBuilder.Cell(), hasBlockingErrors: false);
        var strangerDecision = EditRules.CanSubmit(stranger, AccessBuilder.Cell(), hasBlockingErrors: false);

        // ⚠ Право НЕ підіймає рівень: Read лишається Read, None лишається None.
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, readerDecision.Reason);
        Assert.Equal(EditDenyReason.NoGrant, strangerDecision.Reason);
    }

    [Theory]
    [InlineData(ResourceKind.Sheet, AccessBuilder.SheetId)]
    [InlineData(ResourceKind.Table, AccessBuilder.TableId)]
    [InlineData(ResourceKind.Column, AccessBuilder.ColumnId)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-285")]
    public void Заборона_на_ресурс_перемагає_право_Submit(ResourceKind kind, int id)
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write)
            .Permission(Right)
            .Deny(kind, id)
            .Build();

        var decision = EditRules.CanSubmit(profile, AccessBuilder.Cell(), hasBlockingErrors: false);

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.NoGrant, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-285")]
    public void Блокувальні_помилки_валідації_блокують_і_Write_з_правом()
    {
        var decision = EditRules.CanSubmit(Writer(withRight: true), AccessBuilder.Cell(), hasBlockingErrors: true);

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.BusinessRule, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-285")]
    public void Закритий_період_архів_архівація_і_стан_блокують_Write_з_правом_без_змін()
    {
        var profile = Writer(withRight: true);

        Assert.Equal(
            EditDenyReason.PeriodClosed,
            EditRules.CanSubmit(profile, AccessBuilder.Cell(period: PeriodState.Closed), false).Reason);
        Assert.Equal(
            EditDenyReason.ProjectArchived,
            EditRules.CanSubmit(profile, AccessBuilder.Cell(project: ProjectStatus.Archived), false).Reason);
        Assert.Equal(
            EditDenyReason.ArchivingInProgress,
            EditRules.CanSubmit(profile, AccessBuilder.Cell(archiving: true), false).Reason);
        Assert.Equal(
            EditDenyReason.DocumentSubmitted,
            EditRules.CanSubmit(profile, AccessBuilder.Cell(sheet: DocumentStatus.Submitted), false).Reason);
        Assert.Equal(
            EditDenyReason.DocumentApproved,
            EditRules.CanSubmit(profile, AccessBuilder.Cell(sheet: DocumentStatus.Approved), false).Reason);
    }

    /// <summary>
    /// Право від ролі з областю «проєкт X»: подає в X, не подає в Y (Write у обох).
    /// ⛔ Мутація: у <c>MeetsSubmit</c> глобальна перевірка (<c>profile.Has(code)</c>) або
    /// ігнор <c>projectId</c> в <c>IsGrantedIn</c> — перший рядок чи другий червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-285")]
    public void Право_від_ролі_з_областю_діє_лише_в_її_проєкті()
    {
        const int other = AccessBuilder.ProjectId + 1;
        var profile = new AccessProfile
        {
            CacheKey = "u7:s1",
            UserId = 7,
            SecurityStamp = "s1",
            Permissions = new HashSet<string>(),
            Grants = new Dictionary<string, GrantLevel>
            {
                [$"{ResourceKind.Project}:{AccessBuilder.ProjectId}"] = GrantLevel.Write,
                [$"{ResourceKind.Project}:{other}"] = GrantLevel.Write,
            },
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
            Scoped = new Dictionary<int, ScopedProjectAccess>
            {
                [AccessBuilder.ProjectId] = new(
                    new Dictionary<string, GrantLevel>(),
                    new HashSet<string>(),
                    new HashSet<int>(),
                    new HashSet<string> { Right }),
            },
        };

        Assert.True(EditRules.CanSubmit(profile, AccessBuilder.Cell(), hasBlockingErrors: false).IsAllowed);

        var inOther = AccessBuilder.Cell() with { ProjectId = other };
        Assert.Equal(
            EditDenyReason.InsufficientGrantLevel,
            EditRules.CanSubmit(profile, inOther, hasBlockingErrors: false).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-285")]
    public void Симуляція_блокує_Write_з_правом()
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write)
            .Permission(Right)
            .Build(simulation: true, simulatedFor: 5);

        Assert.Equal(
            EditDenyReason.SimulationReadOnly,
            EditRules.CanSubmit(profile, AccessBuilder.Cell(), false).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-285")]
    public void Право_Submit_лише_в_іншому_проєкті_не_дає_подання_тут()
    {
        // ⛔ Проєктне право: роль з областю «проєкт 999» не дає подання в проєкті 10.
        var scoped = new Dictionary<int, ScopedProjectAccess>
        {
            [999] = new(
                new Dictionary<string, GrantLevel>(),
                new HashSet<string>(),
                new HashSet<int>(),
                new HashSet<string> { Right }),
        };
        var profile = new AccessProfile
        {
            CacheKey = "u7:s1",
            UserId = 7,
            SecurityStamp = "s1",
            Permissions = new HashSet<string>(),
            Grants = new Dictionary<string, GrantLevel>
            {
                [$"{ResourceKind.Project}:{AccessBuilder.ProjectId}"] = GrantLevel.Write,
            },
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
            Scoped = scoped,
        };

        var decision = EditRules.CanSubmit(profile, AccessBuilder.Cell(), hasBlockingErrors: false);

        Assert.Equal(EditDenyReason.InsufficientGrantLevel, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-285")]
    public void Право_Submit_не_розширює_редагування_і_Approve()
    {
        // Право дає ЛИШЕ подання: ні запис нижче Write, ні погодження не змінюються.
        var reader = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Permission(Right).Build();

        Assert.False(EditRules.CanEdit(reader, AccessBuilder.Cell()).IsAllowed);
        Assert.False(EditRules.CanApprove(
            Writer(withRight: true), AccessBuilder.Cell(sheet: DocumentStatus.Submitted)).IsAllowed);
    }
}
