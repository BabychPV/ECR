// tests/Ecr.Domain.Tests/Workflow/ApprovalRouteResolveTests.cs
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Domain.Tests.Workflow;

/// <summary>
/// <c>ApprovalRoute.Resolve</c> — єдине правило вибору маршруту для затвердження й черги «До погодження»
/// (ФВ-12.5, ФВ-5.17).
/// </summary>
public sealed class ApprovalRouteResolveTests
{
    private static ApprovalRoute Route(string code, int? projectId = null, int? versionId = null)
        => new(
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            projectId,
            versionId);

    [Fact]
    public void Найконкретніший_виграє_проєкт_і_версія_над_проєктом_над_версією_над_типовим()
    {
        var typical = Route("TYPICAL");
        var byVersion = Route("BYVERSION", versionId: 7);
        var byProject = Route("BYPROJECT", projectId: 3);
        var both = Route("BOTH", projectId: 3, versionId: 7);

        Assert.Same(both, ApprovalRoute.Resolve([typical, byVersion, byProject, both], 3, 7));
        Assert.Same(byProject, ApprovalRoute.Resolve([typical, byVersion, byProject], 3, 7));
        Assert.Same(byVersion, ApprovalRoute.Resolve([typical, byVersion], 3, 7));
        Assert.Same(typical, ApprovalRoute.Resolve([typical, byVersion], 3, 8));
    }

    [Fact]
    public void Чужий_проєкт_чи_версія_не_підходять_і_маршруту_може_не_бути()
    {
        var other = Route("OTHER", projectId: 4, versionId: 9);

        Assert.Null(ApprovalRoute.Resolve([other], 3, 7));
        Assert.Null(ApprovalRoute.Resolve([], 3, 7));
    }

    [Fact]
    public void Рівна_конкретність_вирішується_кодом_а_не_порядком_переліку()
    {
        var b = Route("B_ROUTE", projectId: 3);
        var a = Route("A_ROUTE", projectId: 3);

        Assert.Same(a, ApprovalRoute.Resolve([b, a], 3, 7));
        Assert.Same(a, ApprovalRoute.Resolve([a, b], 3, 7));
    }
}
