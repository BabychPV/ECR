// tests/Ecr.Domain.Tests/Workflow/ApprovalRouteResolveTests.cs
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Domain.Tests.Workflow;

/// <summary>
/// <c>ApprovalRoute.Resolve</c> — єдине правило вибору маршруту для затвердження й черги «До погодження»
/// (ФВ-12.5, ФВ-5.17); ✎ B155 — з рівнями шаблону.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЇ (B155): поміняти місцями рівні в <c>ApprovalRoute.SpecificityOf</c> (шаблон ↔ проєкт, (P,T) ↔ (–,T))
/// — червоніє «кожен рівень виграє в наступного»; прибрати умову шаблону з <c>Applicable</c> — червоніє «маршрут
/// чужого шаблону не діє»; прибрати перевірку конкретнішого маршруту з <c>ScopeGoverns</c> — червоніє «виняток
/// проєкту вилучає аркуші з області шаблону».
/// </remarks>
public sealed class ApprovalRouteResolveTests
{
    private const int Project = 3;
    private const int Template = 9;
    private const int Version = 7;

    private static ApprovalRoute Route(string code, int? projectId = null, int? versionId = null, int? templateId = null)
        => new(
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            projectId,
            versionId,
            templateId);

    [Fact]
    [Trait("Decision", "B155")]
    public void Кожен_рівень_виграє_в_наступного_версія_проєкт_шаблон_шаблон_проєкт_типовий()
    {
        var typical = Route("TYPICAL");
        var byProject = Route("BYPROJECT", projectId: Project);
        var byTemplate = Route("BYTEMPLATE", templateId: Template);
        var byProjectTemplate = Route("BYPROJECTTEMPLATE", projectId: Project, templateId: Template);
        var byVersion = Route("BYVERSION", versionId: Version);
        var both = Route("BOTH", projectId: Project, versionId: Version);

        List<ApprovalRoute> routes = [typical, byProject, byTemplate, byProjectTemplate, byVersion, both];
        ApprovalRoute[] expected = [both, byVersion, byProjectTemplate, byTemplate, byProject, typical];

        // Прибираємо переможця — виграє наступний рівень, і так до типового.
        foreach (var winner in expected)
        {
            Assert.Same(winner, ApprovalRoute.Resolve(routes, Project, Template, Version));
            // ⚠ За посиланням: рівність сутності — за Id, а в усіх тут Id = 0.
            routes.RemoveAll(r => ReferenceEquals(r, winner));
        }

        Assert.Null(ApprovalRoute.Resolve(routes, Project, Template, Version));
    }

    [Fact]
    [Trait("Decision", "B155")]
    public void Маршрут_чужого_або_невідомого_шаблону_не_діє()
    {
        var byProject = Route("BYPROJECT", projectId: Project);
        var otherTemplate = Route("OTHERTEMPLATE", templateId: Template + 1);
        var otherOverride = Route("OTHEROVERRIDE", projectId: Project + 1, templateId: Template);

        Assert.Same(byProject, ApprovalRoute.Resolve([byProject, otherTemplate, otherOverride], Project, Template, Version));

        // Шаблон невідомий — маршрути шаблонів не діють узагалі.
        var byTemplate = Route("BYTEMPLATE", templateId: Template);
        Assert.Same(byProject, ApprovalRoute.Resolve([byProject, byTemplate], Project, null, Version));
    }

    [Fact]
    public void Чужий_проєкт_чи_версія_не_підходять_і_маршруту_може_не_бути()
    {
        var other = Route("OTHER", projectId: 4, versionId: 9);

        Assert.Null(ApprovalRoute.Resolve([other], Project, Template, Version));
        Assert.Null(ApprovalRoute.Resolve([], Project, Template, Version));
    }

    [Fact]
    public void Рівна_конкретність_вирішується_кодом_а_не_порядком_переліку()
    {
        var b = Route("B_ROUTE", projectId: Project);
        var a = Route("A_ROUTE", projectId: Project);

        Assert.Same(a, ApprovalRoute.Resolve([b, a], Project, Template, Version));
        Assert.Same(a, ApprovalRoute.Resolve([a, b], Project, Template, Version));
    }

    [Fact]
    [Trait("Decision", "B155")]
    public void Загальний_Approver_лише_на_маршруті_шаблону()
    {
        Assert.True(Route("T", templateId: Template).AllowsGeneralApprover);
        Assert.True(Route("PT", projectId: Project, templateId: Template).AllowsGeneralApprover);
        Assert.False(Route("P", projectId: Project).AllowsGeneralApprover);
        Assert.False(Route("D").AllowsGeneralApprover);
        Assert.False(Route("PV", projectId: Project, versionId: Version).AllowsGeneralApprover);
    }

    [Fact]
    [Trait("Decision", "B155")]
    public void Виняток_проєкту_вилучає_аркуші_з_області_шаблону_а_інші_проєкти_лишаються()
    {
        var byTemplate = Route("BYTEMPLATE", templateId: Template);
        var overrideIn3 = Route("OVERRIDE3", projectId: Project, templateId: Template);
        List<ApprovalRoute> routes = [byTemplate, overrideIn3];

        // Область «шаблон, усі проєкти»: проєкт 3 має виняток — не її; проєкт 4 — її; чужий шаблон — ні.
        Assert.False(ApprovalRoute.ScopeGoverns(routes, null, Template, Project, Template, Version));
        Assert.True(ApprovalRoute.ScopeGoverns(routes, null, Template, Project + 1, Template, Version));
        Assert.False(ApprovalRoute.ScopeGoverns(routes, null, Template, Project + 1, Template + 1, Version));

        // Область винятку — лише свій проєкт.
        Assert.True(ApprovalRoute.ScopeGoverns(routes, Project, Template, Project, Template, Version));
        Assert.False(ApprovalRoute.ScopeGoverns(routes, Project, Template, Project + 1, Template, Version));

        // Область вирішує й тоді, коли власного маршруту ще немає (його додають), — і не вирішує під маршрутом версії.
        Assert.True(ApprovalRoute.ScopeGoverns([], null, Template, Project, Template, Version));
        Assert.False(ApprovalRoute.ScopeGoverns([Route("V", versionId: Version)], null, Template, Project, Template, Version));

        // Типовий майданчика — лише там, де нічого конкретнішого немає.
        Assert.False(ApprovalRoute.ScopeGoverns([Route("P", projectId: Project)], null, null, Project, Template, Version));
        Assert.True(ApprovalRoute.ScopeGoverns([Route("P", projectId: Project)], null, null, Project + 1, Template, Version));
    }

    [Fact]
    [Trait("Decision", "B155")]
    public void Область_успадковує_від_шаблону_потім_проєкту_потім_типового()
    {
        var typical = Route("TYPICAL");
        var byProject = Route("BYPROJECT", projectId: Project);
        var byTemplate = Route("BYTEMPLATE", templateId: Template);

        Assert.Same(byTemplate, ApprovalRoute.ResolveForScope([typical, byProject, byTemplate], Project, Template));
        Assert.Same(byProject, ApprovalRoute.ResolveForScope([typical, byProject], Project, Template));
        Assert.Same(typical, ApprovalRoute.ResolveForScope([typical, byProject], null, Template));
        Assert.Null(ApprovalRoute.ResolveForScope([byProject], null, Template));
    }
}
