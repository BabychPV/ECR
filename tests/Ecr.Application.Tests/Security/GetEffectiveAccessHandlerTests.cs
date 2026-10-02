// tests/Ecr.Application.Tests/Security/GetEffectiveAccessHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Розріз ефективного доступу (ФВ-6.16, D-220) без бази: розбір ресурсу, 404, рівень із профілю й
/// глобальних прав довідника, атрибуція внесків і ланцюжок предків аркуша/таблиці/колонки.
/// </summary>
/// <remarks>
/// ⚠ Набір закриває мутації, яких не бачили HTTP-тести на живій базі (Stryker, лінія mutation-new-code-2):
/// там рядки й профіль приходять разом із бази, і підміна гілок обробника лишалася зеленою.
/// </remarks>
public sealed class GetEffectiveAccessHandlerTests
{
    private const int Admin = 1;
    private const int Target = 2;
    private const int Registry = 5;
    private const int Project = AccessBuilder.ProjectId;

    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string[] RegistryPermissions = ["Registry.View", "Registry.EditData"];
    private static readonly string[] AdminGroups = ["S-1-5-21-ops"];

    private readonly FakeUserStore _users = new();
    private readonly IEffectiveAccessStore _store = Substitute.For<IEffectiveAccessStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _current = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public GetEffectiveAccessHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _current.UserId.Returns(Admin);
        _current.GroupSids.Returns(AdminGroups);
        _access.BuildProfileAsync(Admin, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Admin }.Permission(GetEffectiveAccessHandler.Permission).Build());

        _users.Add(new User("admin", "Admin", AuthProvider.Local));
        _users.Add(new User("ivan", "Ivan", AuthProvider.Local));
        _store.ResourceExistsAsync(Arg.Any<ResourceKind>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _store.ListSourcesAsync(
                default, default!, default, default, default!, default, default)
            .ReturnsForAnyArgs([]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    [InlineData(null)]
    [InlineData("Registry")]
    [InlineData("Registry:")]
    [InlineData("Registry:0")]
    [InlineData("Registry:-5")]
    [InlineData("Registry:+5")]
    [InlineData("Registry:5x")]
    [InlineData("Document:5")]
    [InlineData("Nope:5")]
    public async Task Ресурс_не_у_формі_тип_ідентифікатор_дає_422_і_нічого_не_читає(string? resource)
    {
        // ⚠ МУТАЦІЙНИЙ ДОКАЗ (Parse): `id > 0` → `>= 0`, NumberStyles.None → Integer, перелік дозволених видів —
        // кожен рядок тут пройшов би далі в базу.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Target, resource, projectId: null, CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.effectiveAccessResource", error.Details!["messageKey"]);
        await _access.DidNotReceive().BuildProfileAsync(Target, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Без_Security_ManageUsers_розріз_не_віддається_і_ресурс_не_розбирається()
    {
        _access.BuildProfileAsync(Admin, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Admin }.Permission("Registry.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(Target, "nonsense", projectId: null, CancellationToken.None));
        await _store.DidNotReceiveWithAnyArgs().ResourceExistsAsync(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Немає_людини_довідника_чи_проєкту_це_404_з_власним_кодом()
    {
        var noUser = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(99, $"Registry:{Registry}", projectId: null, CancellationToken.None));
        Assert.Equal("ECR-SEC-0404", noUser.ErrorCode);
        Assert.Equal("err.ECR-SEC-0404.userNotFound", noUser.Details!["messageKey"]);

        _store.ResourceExistsAsync(Arg.Any<ResourceKind>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `kind == Registry ? NotFound(…) : ProjectNotFound` — переставлені гілки дали б
        // довіднику код проєкту й навпаки; `!ResourceExistsAsync` → без заперечення пропустив би обидва.
        var noRegistry = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(Target, $"registry : {Registry}", projectId: null, CancellationToken.None));
        Assert.Equal("ECR-REG-0404", noRegistry.ErrorCode);

        var noProject = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(Target, $"Project:{Project}", projectId: null, CancellationToken.None));
        Assert.Equal("ECR-PRJ-0404", noProject.ErrorCode);
        Assert.Equal("err.ECR-PRJ-0404.project", noProject.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Довідник_глобальне_право_піднімає_рівень_а_заборона_ні_і_рядки_про_права_питаються_лише_для_довідника()
    {
        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: прибрати піднімання рівня Registry.EditData/Registry.View, `&& !denied` → `||`,
        // поміняти гілки «права довідника : []» — червоніє відповідний рядок нижче.
        Profile(b => b.Permission("Registry.EditData"));
        var write = await Handler().HandleAsync(Target, $"Registry:{Registry}", projectId: null, CancellationToken.None);
        Assert.Equal((GrantLevel.Write, false, (string?)null), (write.Level, write.IsDenied, write.DenyReason));

        Profile(b => b.Permission("Registry.View"));
        var read = await Handler().HandleAsync(Target, $"Registry:{Registry}", projectId: null, CancellationToken.None);
        Assert.Equal(GrantLevel.Read, read.Level);

        // Грант вищий за право — лишається грант (Max, а не заміна).
        Profile(b => b.Permission("Registry.View").Grant(ResourceKind.Registry, Registry, GrantLevel.Write));
        Assert.Equal(GrantLevel.Write, (await Handler().HandleAsync(Target, $"Registry:{Registry}", null, CancellationToken.None)).Level);

        Profile(b => b.Permission("Registry.EditData").Deny(ResourceKind.Registry, Registry));
        var denied = await Handler().HandleAsync(Target, $"Registry:{Registry}", projectId: null, CancellationToken.None);
        Assert.Equal((GrantLevel.None, true, "ExplicitDeny"), (denied.Level, denied.IsDenied, denied.DenyReason));

        Profile(b => b);
        var none = await Handler().HandleAsync(Target, $"Registry:{Registry}", projectId: null, CancellationToken.None);
        Assert.Equal((GrantLevel.None, false, "NoGrant"), (none.Level, none.IsDenied, none.DenyReason));
        Assert.Null(none.Caveat);
        Assert.Null(none.ProjectId);
        Assert.Equal($"Registry:{Registry}", none.Resource);
        Assert.Equal("ivan", none.UserName);

        await _store.Received().ListSourcesAsync(
            Target, Arg.Any<IReadOnlyList<string>>(), ResourceKind.Registry, Registry,
            Arg.Is<IReadOnlyCollection<string>>(c => c.SequenceEqual(RegistryPermissions)),
            DateOnly.FromDateTime(Now), Arg.Any<CancellationToken>());

        // Проєкт: глобальні права довідника рівня проєкту не піднімають і не питаються.
        Profile(b => b.Permission("Registry.EditData"));
        var project = await Handler().HandleAsync(Target, $"Project:{Project}", projectId: null, CancellationToken.None);
        Assert.Equal(GrantLevel.None, project.Level);
        await _store.Received().ListSourcesAsync(
            Target, Arg.Any<IReadOnlyList<string>>(), ResourceKind.Project, Project,
            Arg.Is<IReadOnlyCollection<string>>(c => c.Count == 0), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Групи_сесії_враховуються_лише_для_власного_розрізу()
    {
        Profile(b => b);
        _access.BuildProfileAsync(Admin, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Admin }.Permission(GetEffectiveAccessHandler.Permission).Build());

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `userId == currentUser.UserId` → `!=` або гілки `own ? GroupSids : []` навпаки —
        // групи адміна приписалися б чужому записові (`P-02`).
        var other = await Handler().HandleAsync(Target, $"Project:{Project}", projectId: null, CancellationToken.None);
        Assert.False(other.GroupsFromTicket);
        await _store.Received().ListSourcesAsync(
            Target, Arg.Is<IReadOnlyList<string>>(g => g.Count == 0), ResourceKind.Project, Project,
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());

        var own = await Handler().HandleAsync(Admin, $"Project:{Project}", projectId: null, CancellationToken.None);
        Assert.True(own.GroupsFromTicket);
        await _store.Received().ListSourcesAsync(
            Admin, Arg.Is<IReadOnlyList<string>>(g => g.SequenceEqual(AdminGroups)), ResourceKind.Project, Project,
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Внески_проєкту_класифікуються_за_областю_чинністю_і_видом_та_впорядковані()
    {
        Profile(b => b.Grant(ResourceKind.Project, Project, GrantLevel.Read));
        Sources(ResourceKind.Project, Project,
            Row("Z-ROLE", level: GrantLevel.Read),
            Row("A-ROLE", level: GrantLevel.Write, scope: Scope([Project])),
            Row("B-ROLE", level: GrantLevel.Write, scope: Scope([Project + 1])),
            Row("C-ROLE", level: GrantLevel.Submit, scope: Scope([Project], sheets: ["F1"])),
            Row("D-ROLE", level: GrantLevel.Write, effective: false),
            Row("E-ROLE", level: GrantLevel.Read, sid: "S-B"),
            Row("E-ROLE", level: GrantLevel.Read, sid: "S-A"),
            Row("F-ROLE", level: null, permission: "Registry.View", scope: Scope([Project])),
            Row("G-ROLE", level: GrantLevel.Read, scope: "{broken"));

        var view = await Handler().HandleAsync(Target, $"Project:{Project}", projectId: null, CancellationToken.None);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ (Attribute/ClassifyScope): кожна гілка класифікації й `Counted` — окремий рядок;
        // порядок «Source → RoleCode → PrincipalSid» тримає останній Assert.
        var by = view.Contributions.ToLookup(c => c.RoleCode);
        Assert.Equal(("Unscoped", true), (by["Z-ROLE"].Single().Scope, by["Z-ROLE"].Single().Counted));
        Assert.Equal(("InScope", true), (by["A-ROLE"].Single().Scope, by["A-ROLE"].Single().Counted));
        Assert.Equal(("OutOfScope", false), (by["B-ROLE"].Single().Scope, by["B-ROLE"].Single().Counted));
        Assert.Equal(("Narrowed", false), (by["C-ROLE"].Single().Scope, by["C-ROLE"].Single().Counted));
        Assert.Equal(("Expired", false), (by["D-ROLE"].Single().Scope, by["D-ROLE"].Single().Counted));
        Assert.Equal(("OutOfScope", "Permission"), (by["F-ROLE"].Single().Scope, by["F-ROLE"].Single().Source));
        Assert.Equal(GrantLevel.Read, by["F-ROLE"].Single().Level);
        Assert.Equal("OutOfScope", by["G-ROLE"].Single().Scope);
        Assert.All(view.Contributions, c => Assert.Null(c.InheritedFrom));

        Assert.Equal(
            ["Grant/A-ROLE/", "Grant/B-ROLE/", "Grant/C-ROLE/", "Grant/D-ROLE/", "Grant/E-ROLE/S-A", "Grant/E-ROLE/S-B",
             "Grant/G-ROLE/", "Grant/Z-ROLE/", "Permission/F-ROLE/"],
            view.Contributions.Select(c => $"{c.Source}/{c.RoleCode}/{c.PrincipalSid}"));
        Assert.Equal(GrantLevel.Read, view.Level);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Право_довідника_у_внеску_дає_рівень_права_а_область_ролі_його_не_вмикає()
    {
        Profile(b => b);
        Sources(ResourceKind.Registry, Registry,
            Row("EDIT", level: null, permission: "Registry.EditData"),
            Row("VIEW", level: null, permission: "Registry.View"),
            Row("GRANT", level: GrantLevel.Approve, scope: Scope([Project])));

        var view = await Handler().HandleAsync(Target, $"Registry:{Registry}", projectId: null, CancellationToken.None);
        var by = view.Contributions.ToDictionary(c => c.RoleCode);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `PermissionCode == EditData ? Write : Read` навпаки; грант на довідник з
        // областю — OutOfScope (не InScope: область дивиться лише на проєкти).
        Assert.Equal((GrantLevel.Write, "Permission", true), (by["EDIT"].Level, by["EDIT"].Source, by["EDIT"].Counted));
        Assert.Equal((GrantLevel.Read, "Registry.View"), (by["VIEW"].Level, by["VIEW"].PermissionCode));
        Assert.Equal((GrantLevel.Approve, "OutOfScope", false), (by["GRANT"].Level, by["GRANT"].Scope, by["GRANT"].Counted));
        Assert.Null(by["GRANT"].PermissionCode);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Аркуш_таблиця_колонка_без_проєкту_дають_422(int? projectId)
    {
        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `projectId is not > 0` → `is not >= 0` пропустив би проєкт 0.
        foreach (var resource in new[] { "Sheet:20", "Table:30", "Column:40" })
        {
            var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Handler().HandleAsync(Target, resource, projectId, CancellationToken.None));
            Assert.Equal("err.ECR-REQ-0422.effectiveAccessProject", error.Details!["messageKey"]);
        }

        await _store.DidNotReceiveWithAnyArgs().ResolveChainAsync(default, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Колонка_поза_шаблоном_проєкту_неіснуюча_чи_проєкт_без_рядка_дають_різні_404()
    {
        _store.ResolveChainAsync(ResourceKind.Column, 40, Project, Arg.Any<CancellationToken>()).Returns((ResourceChain?)null);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `exists ? NotInProject : Resource` навпаки, `chain is null` → `is not null`.
        var foreign = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(Target, "Column:40", Project, CancellationToken.None));
        Assert.Equal(("ECR-TMPL-0404", "err.ECR-TMPL-0404.effectiveAccessNotInProject"), (foreign.ErrorCode, foreign.Details!["messageKey"]));
        Assert.Equal("Column:40", foreign.Details["resource"]);

        _store.ResourceExistsAsync(ResourceKind.Column, 40, Arg.Any<CancellationToken>()).Returns(false);
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(Target, "Column:40", Project, CancellationToken.None));
        Assert.Equal("err.ECR-TMPL-0404.effectiveAccessResource", missing.Details!["messageKey"]);

        _store.ResourceExistsAsync(ResourceKind.Project, Project, Arg.Any<CancellationToken>()).Returns(false);
        var noProject = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(Target, "Column:40", Project, CancellationToken.None));
        Assert.Equal(("ECR-PRJ-0404", "err.ECR-PRJ-0404.project"), (noProject.ErrorCode, noProject.Details!["messageKey"]));
        await _store.Received(2).ResolveChainAsync(ResourceKind.Column, 40, Project, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Колонка_рівень_за_ланцюжком_предків_внески_від_проєкту_до_колонки_з_InheritedFrom()
    {
        _store.ResolveChainAsync(ResourceKind.Column, 40, Project, Arg.Any<CancellationToken>())
            .Returns(new ResourceChain(20, "F1", 30, 40));
        Profile(b => b.Grant(ResourceKind.Project, Project, GrantLevel.Read).Grant(ResourceKind.Table, 30, GrantLevel.Write));
        Sources(ResourceKind.Column, 40, Row("COL", level: GrantLevel.Approve, effective: false));
        Sources(ResourceKind.Table, 30, Row("TBL", level: GrantLevel.Write));
        Sources(ResourceKind.Sheet, 20, Row("SHT-B", level: GrantLevel.Read), Row("SHT-A", level: GrantLevel.Read));
        Sources(ResourceKind.Project, Project, Row("PRJ", level: GrantLevel.Read, scope: Scope([Project])));

        var view = await Handler().HandleAsync(Target, "Column:40", Project, CancellationToken.None);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: прибрати таблицю чи колонку з ланцюжка, OrderBy(Depth) → інший порядок,
        // `from == requested ? null : from` навпаки, класифікація області предка не за ПРОЄКТОМ.
        Assert.Equal(
            ["PRJ@Project:10", "SHT-A@Sheet:20", "SHT-B@Sheet:20", "TBL@Table:30", "COL@"],
            view.Contributions.Select(c => $"{c.RoleCode}@{c.InheritedFrom}"));
        Assert.Equal(("InScope", true), (view.Contributions[0].Scope, view.Contributions[0].Counted));
        Assert.Equal(("Expired", false), (view.Contributions[^1].Scope, view.Contributions[^1].Counted));
        Assert.Equal((GrantLevel.Write, false, (string?)null), (view.Level, view.IsDenied, view.DenyReason));
        Assert.Equal(EffectiveAccessView.DocumentStateNotConsidered, view.Caveat);
        Assert.Equal(Project, view.ProjectId);
        Assert.Equal("Column:40", view.Resource);

        // Аркуш: таблиці й колонки в ланцюжку немає — питаємо лише проєкт і аркуш.
        _store.ResolveChainAsync(ResourceKind.Sheet, 20, Project, Arg.Any<CancellationToken>())
            .Returns(new ResourceChain(20, "F1", null, null));
        _store.ClearReceivedCalls();
        var sheet = await Handler().HandleAsync(Target, "Sheet:20", Project, CancellationToken.None);
        Assert.Equal(["PRJ@Project:10", "SHT-A@", "SHT-B@"], sheet.Contributions.Select(c => $"{c.RoleCode}@{c.InheritedFrom}"));
        await _store.DidNotReceive().ListSourcesAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), ResourceKind.Table, Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        Assert.Equal(GrantLevel.Read, sheet.Level);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Заборона_будь_якого_предка_у_профілі_чи_в_області_проєкту_дає_ExplicitDeny()
    {
        _store.ResolveChainAsync(ResourceKind.Column, 40, Project, Arg.Any<CancellationToken>())
            .Returns(new ResourceChain(20, "F1", 30, 40));

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ (HasDeny): прибрати перевірку `profile.Denies` чи `scoped.Denies`, `Any` → `All`.
        Profile(b => b.Grant(ResourceKind.Project, Project, GrantLevel.Write).Deny(ResourceKind.Sheet, 20));
        var sheetDeny = await Handler().HandleAsync(Target, "Column:40", Project, CancellationToken.None);
        Assert.Equal((true, "ExplicitDeny", GrantLevel.None), (sheetDeny.IsDenied, sheetDeny.DenyReason, sheetDeny.Level));

        var baseline = new AccessBuilder { UserId = Target }.Grant(ResourceKind.Project, Project, GrantLevel.Write).Build();
        var scoped = new AccessProfile
        {
            CacheKey = baseline.CacheKey,
            UserId = Target,
            SecurityStamp = baseline.SecurityStamp,
            Permissions = baseline.Permissions,
            Grants = baseline.Grants,
            Denies = baseline.Denies,
            RoleIds = baseline.RoleIds,
            Scoped = new Dictionary<int, ScopedProjectAccess>
            {
                [Project] = new(
                    new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
                    new HashSet<string>(StringComparer.Ordinal) { "Table:30" },
                    new HashSet<int>(),
                    new HashSet<string>(StringComparer.Ordinal)),
            },
        };
        _access.BuildProfileAsync(Target, Arg.Any<CancellationToken>()).Returns(scoped);

        var tableDeny = await Handler().HandleAsync(Target, "Column:40", Project, CancellationToken.None);
        Assert.Equal((true, "ExplicitDeny"), (tableDeny.IsDenied, tableDeny.DenyReason));

        // У ІНШОМУ проєкті заборона області не діє.
        _store.ResolveChainAsync(ResourceKind.Column, 40, Project + 1, Arg.Any<CancellationToken>())
            .Returns(new ResourceChain(20, "F1", 30, 40));
        var elsewhere = await Handler().HandleAsync(Target, "Column:40", Project + 1, CancellationToken.None);
        Assert.False(elsewhere.IsDenied);
        Assert.Equal("NoGrant", elsewhere.DenyReason);
    }

    // ⚠ Звужена ЛИШЕ аркушами роль на аркуші/таблиці/колонці зараз має Counted = false (ent5 P2-2 — «Аналіз»
    // це виправляє: Counted стане true, коли аркуш у переліку). Тут свідомо не тримаємо поточне значення —
    // щоб фікс не червонив цей набір; звуження з ПЕРІОДАМИ лишається false і після фіксу.
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Роль_звужена_періодами_на_аркуші_не_рахується_навіть_у_своєму_проєкті()
    {
        _store.ResolveChainAsync(ResourceKind.Sheet, 20, Project, Arg.Any<CancellationToken>())
            .Returns(new ResourceChain(20, "F1", null, null));
        Profile(b => b);
        Sources(ResourceKind.Sheet, 20,
            Row("PERIODS", level: GrantLevel.Write, scope: Scope([Project], from: new PeriodKey(202601), to: new PeriodKey(202612))));

        var view = await Handler().HandleAsync(Target, "Sheet:20", Project, CancellationToken.None);

        var row = Assert.Single(view.Contributions);
        Assert.Equal(("Narrowed", false), (row.Scope, row.Counted));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Колонка_грант_на_саму_колонку_групи_лише_свої_рівень_внеску_і_заборона_звуженого_шару_аркуша()
    {
        _store.ResolveChainAsync(ResourceKind.Column, 40, Project, Arg.Any<CancellationToken>())
            .Returns(new ResourceChain(20, "F1", 30, 40));

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `ColumnDefId ?? 0` → 0 загубив би грант на саму колонку; `row.Level ?? None` → None;
        // `ThenBy(PrincipalSid)` → Descending; `own`/`GroupSids` навпаки для аркуша/таблиці/колонки.
        Profile(b => b.Grant(ResourceKind.Project, Project, GrantLevel.Read).Grant(ResourceKind.Column, 40, GrantLevel.Approve));
        Sources(ResourceKind.Column, 40,
            Row("COL", level: GrantLevel.Approve, sid: "S-B"),
            Row("COL", level: GrantLevel.Approve, sid: "S-A"));

        var other = await Handler().HandleAsync(Target, "Column:40", Project, CancellationToken.None);
        Assert.Equal(GrantLevel.Approve, other.Level);
        Assert.False(other.GroupsFromTicket);
        Assert.Equal(["S-A", "S-B"], other.Contributions.Select(c => c.PrincipalSid));
        Assert.All(other.Contributions, c => Assert.Equal(GrantLevel.Approve, c.Level));
        await _store.Received().ListSourcesAsync(
            Target, Arg.Is<IReadOnlyList<string>>(g => g.Count == 0), ResourceKind.Column, 40,
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());

        _access.BuildProfileAsync(Admin, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Admin }.Permission(GetEffectiveAccessHandler.Permission).Build());
        var own = await Handler().HandleAsync(Admin, "Column:40", Project, CancellationToken.None);
        Assert.True(own.GroupsFromTicket);
        await _store.Received().ListSourcesAsync(
            Admin, Arg.Is<IReadOnlyList<string>>(g => g.SequenceEqual(AdminGroups)), ResourceKind.Column, 40,
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());

        // Заборона в шарі ролі, звуженої аркушем F1: діє на аркуші F1 і не діє на аркуші F2.
        // ⚠ МУТАЦІЙНИЙ ДОКАЗ (HasDeny): `Narrowed.Any` → `All`, `AppliesTo && Denies` → `||`.
        _access.BuildProfileAsync(Target, Arg.Any<CancellationToken>()).Returns(NarrowedDeny("F1", "Table:30"));
        var deniedOnF1 = await Handler().HandleAsync(Target, "Column:40", Project, CancellationToken.None);
        Assert.Equal((true, "ExplicitDeny"), (deniedOnF1.IsDenied, deniedOnF1.DenyReason));

        _store.ResolveChainAsync(ResourceKind.Column, 41, Project, Arg.Any<CancellationToken>())
            .Returns(new ResourceChain(21, "F2", 30, 41));
        var freeOnF2 = await Handler().HandleAsync(Target, "Column:41", Project, CancellationToken.None);
        Assert.False(freeOnF2.IsDenied);
    }

    private static AccessProfile NarrowedDeny(string sheetCode, string deniedKey)
    {
        var baseline = new AccessBuilder { UserId = Target }.Grant(ResourceKind.Project, Project, GrantLevel.Write).Build();
        var none = new HashSet<string>(StringComparer.Ordinal);

        return new AccessProfile
        {
            CacheKey = baseline.CacheKey,
            UserId = Target,
            SecurityStamp = baseline.SecurityStamp,
            Permissions = baseline.Permissions,
            Grants = baseline.Grants,
            Denies = baseline.Denies,
            RoleIds = baseline.RoleIds,
            Scoped = new Dictionary<int, ScopedProjectAccess>
            {
                [Project] = new(new Dictionary<string, GrantLevel>(StringComparer.Ordinal), none, new HashSet<int>(), none)
                {
                    Narrowed =
                    [
                        new NarrowedAccess(
                            new HashSet<string>(StringComparer.Ordinal) { sheetCode }, null, null,
                            new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
                            new HashSet<string>(StringComparer.Ordinal) { deniedKey },
                            new HashSet<int> { 6 },
                            none),
                        new NarrowedAccess(
                            new HashSet<string>(StringComparer.Ordinal) { "OTHER" }, null, null,
                            new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
                            none,
                            new HashSet<int> { 8 },
                            none),
                    ],
                },
            },
        };
    }

    private GetEffectiveAccessHandler Handler() => new(_users, _store, _access, _current, _clock);

    private void Profile(Func<AccessBuilder, AccessBuilder> build)
        => _access.BuildProfileAsync(Target, Arg.Any<CancellationToken>())
            .Returns(build(new AccessBuilder { UserId = Target }).Build());

    private void Sources(ResourceKind kind, int id, params AccessSourceRow[] rows)
        => _store.ListSourcesAsync(
                Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), kind, id,
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(rows);

    private static AccessSourceRow Row(
        string role, GrantLevel? level, string? scope = null, bool effective = true, string? sid = null,
        string? permission = null, bool deny = false)
        => new(1, role, sid, null, null, effective, scope, permission, level, deny);

    private static string Scope(int[] projects, string[]? sheets = null, PeriodKey? from = null, PeriodKey? to = null)
        => RoleAssignmentScope.Create(projects, sheets, from, to).ToJson();
}
