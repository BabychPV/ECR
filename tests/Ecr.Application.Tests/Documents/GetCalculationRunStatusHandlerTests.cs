// tests/Ecr.Application.Tests/Documents/GetCalculationRunStatusHandlerTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// D14: <c>GET /documents/{id}/calculation-run</c> віддає рядки без правила ЦЬОГО документа з профілю актуального
/// прогону; вузький читач діагностики не бачить (ключі схованих аркушів), але прогін бачить.
/// </summary>
/// <remarks>
/// Мутація: прибрати блок <c>HasRestrictions</c> у <see cref="GetCalculationRunStatusHandler"/> — червоніє тест вузького читача.
/// </remarks>
public sealed class GetCalculationRunStatusHandlerTests
{
    private const long Document = 900;
    private const int Period = 202601;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Віддає_рядки_без_правила_лише_цього_документа()
    {
        var handler = Arrange(restricted: false, Profile());

        var status = await handler.HandleAsync(Document, Period, CancellationToken.None);

        Assert.Equal(55, status.RunId);
        Assert.NotNull(status.UnmatchedRows);
        Assert.Equal(1, status.UnmatchedRows.Count);
        var row = Assert.Single(status.UnmatchedRows.Rows);
        Assert.Equal("R001", row.RowKey);
        Assert.Equal(2, row.RowNumber);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Вузький_читач_бачить_прогін_але_не_ключі_рядків()
    {
        var handler = Arrange(restricted: true, Profile());

        var status = await handler.HandleAsync(Document, Period, CancellationToken.None);

        Assert.Equal(55, status.RunId);
        Assert.Null(status.UnmatchedRows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Немає_актуального_прогону_або_профіль_старого_формату_діагностики_немає()
    {
        var none = await Arrange(restricted: false, null).HandleAsync(Document, Period, CancellationToken.None);
        var legacy = await Arrange(restricted: false, "[{\"module\":\"M\",\"ms\":\"1.0\",\"rows\":1,\"calls\":1}]")
            .HandleAsync(Document, Period, CancellationToken.None);

        Assert.Null(none.RunId);
        Assert.Null(none.UnmatchedRows);
        Assert.Equal(55, legacy.RunId);
        Assert.Null(legacy.UnmatchedRows);
    }

    private static string Profile()
    {
        var profile = new ModuleProfile();
        profile.RecordUnmatched([new UnmatchedRow(500, 2, "R001", Document), new UnmatchedRow(600, 1, "OTHER", Document + 1)]);

        return profile.ToJson();
    }

    private static GetCalculationRunStatusHandler Arrange(bool restricted, string? profileJson)
    {
        var builder = new AccessBuilder { UserId = 9 }
            .Permission(GetCalculationResultsHandler.Permission)
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        if (restricted)
        {
            builder.Deny(ResourceKind.Sheet, 777);
        }

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());

        var results = Substitute.For<ICalculationResultStore>();
        results.ReadCurrentRunProfileAsync(Document, Period, Arg.Any<CancellationToken>())
               .Returns(profileJson is null ? null : new CurrentRunProfile(55, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), profileJson));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        return new GetCalculationRunStatusHandler(results, access, user);
    }
}