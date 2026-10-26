// tests/Ecr.Application.Tests/Permits/LookupTargetMatcherDenyTests.cs
using Ecr.Application.Permits.Groups;
using Ecr.Application.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>
/// P2 (рішення людини 23.10: «Deny блокує і вибір запису»): <see cref="LookupTargetMatcher.Belongs"/> з профілем
/// відхиляє запис реєстру під Deny і на ПРЯМОМУ збігу цілі; без профілю (системні шляхи) - як і раніше.
/// Мутація: повернути «збіг реєстру - true» без перевірки Deny - тести з Deny червоніють.
/// </summary>
public sealed class LookupTargetMatcherDenyTests
{
    private const int Target = 55;
    private const int Other = 59;

    // Рішення 25.10: звичайний профіль - з Registry.View (вибір запису потребує права читати реєстр); NoView() - без нього.
    private static AccessProfile Profile(params int[] denied) => new()
    {
        CacheKey = "k", UserId = 1, SecurityStamp = "s",
        Permissions = new HashSet<string> { "Registry.View" }, Grants = new Dictionary<string, Ecr.Domain.Enums.GrantLevel>(),
        Denies = new HashSet<string>(denied.Select(id => "Registry:" + id)), RoleIds = new HashSet<int>(),
    };

    [Fact]
    public void Пряма_ціль_під_Deny_не_належить()
        => Assert.False(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), Profile(Target), Target, Target));

    [Fact]
    public void Пряма_ціль_під_Deny_не_належить_і_без_резолвера()
        => Assert.False(LookupTargetMatcher.Belongs(null, Profile(Target), Target, Target));

    [Fact]
    public void Пряма_ціль_без_Deny_належить_і_не_кличе_резолвер()
    {
        var groups = new FakePermitGroupResolver { ThrowOnUse = true };

        Assert.True(LookupTargetMatcher.Belongs(groups, Profile(), Target, Target));
        Assert.Equal(0, groups.Calls);
    }

    [Fact]
    public void Пряма_ціль_Deny_на_інший_довідник_належить()
        => Assert.True(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), Profile(Other), Target, Target));

    [Fact]
    public void Системний_шлях_без_профілю_Deny_не_застосовує()
        => Assert.True(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), null, Target, Target));

    // ── Решення людини 25.10 («Так, вимагати»): вибір запису потребує Registry.View (глобально) АБО гранта Read на
    // реєстр запису; Deny перемагає; системні шляхи (profile = null) і збір (IsIntegrationWriter) - як було.
    // Мутація: прибрати перевірку View в Belongs - червоніють тести «без View».

    private static AccessProfile NoView() => new()
    {
        CacheKey = "n", UserId = 1, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, Ecr.Domain.Enums.GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private static AccessProfile WithView(params string[] denied) => new()
    {
        CacheKey = "v", UserId = 1, SecurityStamp = "s",
        Permissions = new HashSet<string> { "Registry.View" }, Grants = new Dictionary<string, Ecr.Domain.Enums.GrantLevel>(),
        Denies = new HashSet<string>(denied), RoleIds = new HashSet<int>(),
    };

    [Fact]
    public void Пряма_ціль_без_Registry_View_не_належить()
        => Assert.False(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), NoView(), Target, Target));

    [Fact]
    public void Пряма_ціль_з_Registry_View_належить()
        => Assert.True(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), WithView(), Target, Target));

    [Fact]
    public void Пряма_ціль_View_і_Deny_не_належить()
        => Assert.False(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), WithView("Registry:" + Target), Target, Target));

    [Fact]
    public void Пряма_ціль_грант_Read_на_реєстр_без_View_належить()
    {
        var profile = new AccessProfile
        {
            CacheKey = "g", UserId = 1, SecurityStamp = "s",
            Permissions = new HashSet<string>(),
            Grants = new Dictionary<string, Ecr.Domain.Enums.GrantLevel> { ["Registry:" + Target] = Ecr.Domain.Enums.GrantLevel.Read },
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        };

        Assert.True(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), profile, Target, Target));
    }

    [Fact]
    public void Збір_IsIntegrationWriter_без_View_належить()
    {
        var profile = new AccessProfile
        {
            CacheKey = "i", UserId = 1, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, Ecr.Domain.Enums.GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(), IsIntegrationWriter = true,
        };

        Assert.True(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), profile, Target, Target));
    }

    [Fact]
    public void Група_реєстр_члена_без_View_не_належить_а_з_View_належить()
    {
        var groups = new FakePermitGroupResolver();
        groups.WithGroup(Target, [Other]);

        Assert.False(LookupTargetMatcher.Belongs(groups, NoView(), Target, Other));
        Assert.True(LookupTargetMatcher.Belongs(groups, WithView(), Target, Other));
    }

    [Fact]
    public void Група_реєстр_члена_View_і_Deny_не_належить()
    {
        var groups = new FakePermitGroupResolver();
        groups.WithGroup(Target, [Other]);

        Assert.False(LookupTargetMatcher.Belongs(groups, WithView("Registry:" + Other), Target, Other));
    }

    [Fact]
    public void Група_реєстр_члена_під_Deny_не_належить()
    {
        var groups = new FakePermitGroupResolver();
        groups.WithGroup(Target, [Other]);

        Assert.True(LookupTargetMatcher.Belongs(groups, Profile(), Target, Other));
        Assert.False(LookupTargetMatcher.Belongs(groups, Profile(Other), Target, Other));
    }

    // ── P50: Deny/Read-грант, видані ролі з областю, діють у проєкті документа ────────────────────────────────────────

    private const int ProjectId = 10;

    private static AccessProfile Scoped(
        bool globalView, bool scopedView, bool deny, bool readGrant, int scopedProject = ProjectId) => new()
    {
        CacheKey = "sc", UserId = 1, SecurityStamp = "s",
        Permissions = new HashSet<string>(globalView ? ["Registry.View"] : []),
        Grants = new Dictionary<string, Ecr.Domain.Enums.GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        Scoped = new Dictionary<int, ScopedProjectAccess>
        {
            [scopedProject] = new(
                new Dictionary<string, Ecr.Domain.Enums.GrantLevel>(), new HashSet<string>(), new HashSet<int>(), new HashSet<string>())
            {
                RegistryView = scopedView,
                RegistryDenies = deny ? new HashSet<int> { Target } : new HashSet<int>(),
                RegistryGrants = readGrant
                    ? new Dictionary<int, Ecr.Domain.Enums.GrantLevel> { [Target] = Ecr.Domain.Enums.GrantLevel.Read }
                    : new Dictionary<int, Ecr.Domain.Enums.GrantLevel>(),
            },
        },
    };

    [Fact]
    public void Scoped_View_і_власний_Deny_у_проєкті_не_належить_а_в_іншому_проєкті_належить()
    {
        var profile = Scoped(globalView: false, scopedView: true, deny: true, readGrant: false);

        Assert.False(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), profile, Target, Target, ProjectId));
        Assert.True(LookupTargetMatcher.ViewProjectNeeded(profile));
        Assert.False(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), profile, Target, Target, ProjectId + 1));
    }

    [Fact]
    public void Глобальний_View_і_scoped_Deny_потребує_проєкту_і_відмовляє_лише_в_ньому()
    {
        var profile = Scoped(globalView: true, scopedView: false, deny: true, readGrant: false);

        Assert.True(LookupTargetMatcher.ViewProjectNeeded(profile));
        Assert.False(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), profile, Target, Target, ProjectId));
        Assert.True(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), profile, Target, Target, ProjectId + 1));
        Assert.False(LookupTargetMatcher.MayChoose(profile, Target, ProjectId));
    }

    [Fact]
    public void Scoped_Read_грант_без_View_належить_лише_у_проєкті_призначення()
    {
        var profile = Scoped(globalView: false, scopedView: false, deny: false, readGrant: true);

        Assert.True(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), profile, Target, Target, ProjectId));
        Assert.False(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), profile, Target, Target, ProjectId + 1));
    }

    [Fact]
    public void Група_реєстр_члена_під_scoped_Deny_не_належить()
    {
        var groups = new FakePermitGroupResolver();
        groups.WithGroup(Target, [Other]);
        var profile = Scoped(globalView: true, scopedView: false, deny: false, readGrant: false);
        profile = new AccessProfile
        {
            CacheKey = "sc2", UserId = 1, SecurityStamp = "s",
            Permissions = profile.Permissions, Grants = profile.Grants, Denies = profile.Denies, RoleIds = profile.RoleIds,
            Scoped = new Dictionary<int, ScopedProjectAccess>
            {
                [ProjectId] = new(
                    new Dictionary<string, Ecr.Domain.Enums.GrantLevel>(), new HashSet<string>(), new HashSet<int>(), new HashSet<string>())
                {
                    RegistryDenies = new HashSet<int> { Other },
                },
            },
        };

        Assert.False(LookupTargetMatcher.Belongs(groups, profile, Target, Other, ProjectId));
        Assert.True(LookupTargetMatcher.Belongs(groups, profile, Target, Other, ProjectId + 1));
    }

    // ── P2-C (Тестувальник, прохід A): Deny на саму ціль-ГРУПУ перевіряється першим - запис члена групи не обирається ──

    [Fact]
    public void Deny_на_ціль_групи_блокує_запис_члена_а_Deny_лише_на_члена_як_раніше()
    {
        var groups = new FakePermitGroupResolver();
        groups.WithGroup(Target, [Other]);

        Assert.True(LookupTargetMatcher.Belongs(groups, WithView(), Target, Other));
        Assert.False(LookupTargetMatcher.Belongs(groups, WithView("Registry:" + Target), Target, Other));
        Assert.False(LookupTargetMatcher.Belongs(groups, WithView("Registry:" + Other), Target, Other));
    }

    [Fact]
    public void Scoped_Deny_на_ціль_групи_блокує_члена_лише_у_проєкті_призначення()
    {
        var groups = new FakePermitGroupResolver();
        groups.WithGroup(Target, [Other]);
        var profile = Scoped(globalView: true, scopedView: false, deny: true, readGrant: false);

        Assert.False(LookupTargetMatcher.Belongs(groups, profile, Target, Other, ProjectId));
        Assert.True(LookupTargetMatcher.Belongs(groups, profile, Target, Other, ProjectId + 1));
    }}
