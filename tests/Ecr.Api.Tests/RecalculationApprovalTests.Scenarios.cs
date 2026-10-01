// tests/Ecr.Api.Tests/RecalculationApprovalTests.Scenarios.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Перерахунок проєкту за гайдом тестувальника (TESTER-SCENARIOS Н-К1): неіснуючий
/// період — 404 з ключем, і точний ключ відмови, коли закритий період відкривають
/// непридатним (непідтвердженим) погодженням.
/// </summary>
/// <remarks>
/// Мутаційні докази (усі — у <c>src/Ecr.Application/Calculations/RunCalculationHandler.cs</c>):
/// <list type="bullet">
/// <item><see cref="Неіснуючий_період_404_periodForProject"/>: <c>if (targets.Count == 0)</c> →
/// <c>if (targets.Count &lt; 0)</c> — порожній перелік періодів проходить усі перевірки,
/// задача ставиться в чергу, 202 замість 404.</item>
/// <item><see cref="Непідтверджене_погодження_на_закритий_період_422_approvalNotUsable"/>:
/// <c>"err.ECR-CALC-4221.approvalNotUsable"</c> → <c>"err.ECR-CALC-4221.approvalMissing"</c>
/// — код той самий, ключ ні; сусідні тести дивляться лише на код і цього не бачать.</item>
/// </list>
/// </remarks>
public sealed partial class RecalculationApprovalTests
{
    /// <summary>Коректний за форматом, але в проєкті такого періоду немає.</summary>
    private const int Missing = 203012;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    [Trait("Scenario", "Н-К1")]
    public async Task Неіснуючий_період_404_periodForProject()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var initiator = await SignedInAsync(app, s.Initiator).ConfigureAwait(true);

        var response = await initiator.PostAsJsonAsync(Recalculate(s.ProjectId), new { periodKey = Missing })
            .ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-PRD-0404", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-PRD-0404.periodForProject", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    [Trait("Scenario", "Н-К1")]
    public async Task Непідтверджене_погодження_на_закритий_період_422_approvalNotUsable()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var initiator = await SignedInAsync(app, s.Initiator).ConfigureAwait(true);

        // Запит є, другої людини ще не було — погодження не придатне.
        var id = await RequestAsync(initiator, app, s.ProjectId, Closed).ConfigureAwait(true);
        var response = await RunAsync(initiator, s.ProjectId, Closed, id).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-CALC-4221", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CALC-4221.approvalNotUsable", problem.GetProperty("messageKey").GetString());
    }
}
