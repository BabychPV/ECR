// tests/Ecr.Application.Tests/Documents/DocumentCardStaleYearRuleTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.CalcStatus;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ D-577 (огляд L3): «застаріло» картки для річної розкладки рахується за РОКОМ лише коли річна розкладка — у версії
/// ПЕРІОДУ (шкала версій проєкту), а не в чинній версії документа (документ, що перейшов на нову версію, у старих періодах
/// лишається на старій розкладці).
/// </summary>
/// <remarks>Мутація (локально): повернути правило за <c>result.TemplateLayout</c> — червоні обидва рядки.</remarks>
public sealed class DocumentCardStaleYearRuleTests
{
    private const long DocumentId = 577_301;
    private const int UserId = 7;
    private const int Period = 202609;

    [Theory]
    [InlineData(true, nameof(TemplateLayout.PerMonthCopies), 2026)]
    [InlineData(false, nameof(TemplateLayout.YearWithMonthColumns), null)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-577")]
    public async Task Рік_картки_за_версією_періоду(bool periodVersionIsYear, string currentLayout, int? expectedYear)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = UserId }
            .Permission("Document.View")
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var documents = Substitute.For<IDocumentStore>();
        documents.FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSummary(
                DocumentId, AccessBuilder.ProjectId, "DOC-L3", DateTime.UnixEpoch, 1, new Dictionary<string, string>(),
                ResultsStale: true, PeriodKind: nameof(PeriodKind.Monthly), TemplateLayout: currentLayout));

        var store = Substitute.For<IDocumentCalcStatusStore>();
        store.StaleMonthsAsync(DocumentId, AccessBuilder.ProjectId, Period, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<StaleMonthFact>());
        var stale = new DocumentStaleMonths(
            store, Substitute.For<IPeriodStore>(), Substitute.For<IWorkflowStore>(), documents,
            Substitute.For<IDocumentSheetCopyStore>());

        var layouts = Substitute.For<ITemplateLayoutRegistry>();
        layouts.HasAny.Returns(true);
        layouts.IsYearVersion(41).Returns(periodVersionIsYear);
        var timeline = Substitute.For<IProjectVersionTimeline>();
        timeline.VersionOfAsync(AccessBuilder.ProjectId, Period, Arg.Any<CancellationToken>()).Returns(41);

        var card = await new GetDocumentHandler(documents, access, user, staleMonths: stale, layouts: layouts, timeline: timeline)
            .HandleAsync(DocumentId, Period, CancellationToken.None);

        Assert.NotNull(card);
        Assert.False(card.ResultsStale);
        await store.Received(1).StaleMonthsAsync(DocumentId, AccessBuilder.ProjectId, Period, expectedYear, Arg.Any<CancellationToken>());
    }
}
