using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Показники методологій, прив'язані до колонки схованого від читача аркуша/таблиці, не віддаються
/// (<c>GET /documents/{id}/calculation-results</c>): число належить колонці, а колонку читач не бачить.
/// </summary>
/// <remarks>
/// Мутація (локально): прибрати блок <c>HasRestrictions</c> у <c>GetCalculationResultsHandler</c> —
/// червоніє перший тест; тест «без обмежень» тримає паритет звичайної ролі.
/// </remarks>
public sealed class CalculationResultsHiddenColumnTests
{
    private const long Document = 900;
    private const int Period = 202601;
    private const int Version = 10;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Показники_схованого_аркуша_не_віддаються_а_видимого_віддаються()
    {
        var (handler, hiddenSheetId) = Arrange(deny: true);
        Assert.True(hiddenSheetId > 0);

        var codes = (await handler.HandleAsync(Document, Period, CancellationToken.None)).Select(d => d.OutputCode).ToList();

        Assert.Equal(["OUT_A"], codes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Без_обмежень_віддаються_усі_показники()
    {
        var (handler, _) = Arrange(deny: false);

        var codes = (await handler.HandleAsync(Document, Period, CancellationToken.None)).Select(d => d.OutputCode).Order().ToList();

        Assert.Equal(["OUT_A", "OUT_B"], codes);
    }

    private static (GetCalculationResultsHandler Handler, int HiddenSheetId) Arrange(bool deny)
    {
        var template = new TemplateBuilder();
        var visibleSheet = template.Sheet("S1");
        var visibleTable = template.Table(visibleSheet, "T1");
        var visibleColumn = template.Column(visibleTable, "C1");
        var hiddenSheet = template.Sheet("S2");
        var hiddenTable = template.Table(hiddenSheet, "T2");
        var hiddenColumn = template.Column(hiddenTable, "C2");
        var snapshot = template.Build();

        var builder = new AccessBuilder { UserId = 9 }
            .Permission(GetCalculationResultsHandler.Permission)
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        if (deny)
        {
            builder.Deny(ResourceKind.Sheet, hiddenSheet.Id);
        }

        var profile = builder.Build();

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, snapshot));

        var results = Substitute.For<ICalculationResultStore>();
        results.ReadCurrentAsync(Document, Period, Arg.Any<CancellationToken>())
               .Returns([
                   new CalculationResultRow(Version, "R1", "OUT_A", 1m, 1, null),
                   new CalculationResultRow(Version, "R1", "OUT_B", 2m, 1, null),
               ]);

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns([
                         new ColumnResultBinding(visibleTable.Id, visibleColumn.Id, 1, "OUT_A", "{}", [Version]),
                         new ColumnResultBinding(hiddenTable.Id, hiddenColumn.Id, 1, "OUT_B", "{}", [Version]),
                     ]);
        methodologies.GetVersionLabelsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns(new Dictionary<int, MethodologyVersionLabel>());
        methodologies.GetCalculationFreshnessAsync(
                Document, Period, Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new CalculationFreshness(null, null));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        return (new GetCalculationResultsHandler(results, methodologies, access, user), hiddenSheet.Id);
    }
}
