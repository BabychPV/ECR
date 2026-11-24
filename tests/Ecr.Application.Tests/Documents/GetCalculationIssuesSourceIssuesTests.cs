// tests/Ecr.Application.Tests/Documents/GetCalculationIssuesSourceIssuesTests.cs
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
/// B80 (N4, R3.2): поле <c>sourceIssues</c> відповіді <c>GET /documents/{id}/calculation-issues</c> — проблеми
/// документа-джерела з <see cref="ISourceIssueReader"/>; документ-джерело, якого читач не може відкрити, не називається.
/// </summary>
/// <remarks>
/// ⛔ D-6 / R4.5: читач без доступу до документа-джерела бачить вид проблеми й місяці, але НЕ id, код проєкту, ключ і
/// момент затвердження джерела. Мутацію видимості (Deny) запускає незалежний аудитор (AU), не автор — рядок мутації у
/// звіті N4.
/// </remarks>
public sealed class GetCalculationIssuesSourceIssuesTests
{
    private const long Document = 900;
    private const long Source = 77;
    private const int Period = 209903;
    private static readonly DateTime SourceApproved = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LandApproved = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

    private static readonly SourceIssueRow Changed = new(
        ISourceIssueReader.SourceChangedAfterApproval, Period, 3, Period, Source, "HSE400_PRJ", "HSE400-2099",
        SourceApproved, LandApproved);

    /// <summary>Пара: сховище нічого не віддає (стан <c>Any</c>) — поле <c>null</c>; віддає — поле заповнене.</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Без_проблем_джерела_поле_null_з_проблемою_заповнене(bool hasIssue)
    {
        var handler = Arrange(hasIssue ? [Changed] : [], sourceVisible: true, out _);

        var result = await handler.HandleAsync(Document, Period, null, CancellationToken.None);

        if (!hasIssue)
        {
            Assert.Null(result.SourceIssues);
            return;
        }

        var issue = Assert.Single(result.SourceIssues!);
        Assert.Equal(ISourceIssueReader.SourceChangedAfterApproval, issue.Kind);
        Assert.Equal((3, Period), (issue.MonthNumber, issue.SourcePeriodKey));
    }

    /// <summary>Allow: читач може відкрити документ-джерело — id, код проєкту, ключ і момент затвердження названо.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Читач_джерела_бачить_документ_джерело()
    {
        var handler = Arrange([Changed], sourceVisible: true, out _);

        var issue = Assert.Single((await handler.HandleAsync(Document, Period, null, CancellationToken.None)).SourceIssues!);

        Assert.Equal(Source, issue.SourceDocumentId);
        Assert.Equal("HSE400_PRJ", issue.SourceProjectCode);
        Assert.Equal("HSE400-2099", issue.SourceDocumentKey);
        Assert.Equal(SourceApproved, issue.SourceApprovedAt);
        Assert.Equal(LandApproved, issue.ApprovedAt);
    }

    /// <summary>
    /// Deny: читач НЕ може відкрити документ-джерело — вид і місяці є, а id, код проєкту, ключ і момент затвердження
    /// джерела — <c>null</c>; жодне поле не несе коду чи назви проєкту джерела.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Читач_без_доступу_до_джерела_бачить_проблему_без_документа_джерела()
    {
        var handler = Arrange([Changed], sourceVisible: false, out _);

        var result = await handler.HandleAsync(Document, Period, null, CancellationToken.None);

        var issue = Assert.Single(result.SourceIssues!);
        Assert.Equal(ISourceIssueReader.SourceChangedAfterApproval, issue.Kind);
        Assert.Equal((3, Period), (issue.MonthNumber, issue.SourcePeriodKey));
        Assert.Null(issue.SourceDocumentId);
        Assert.Null(issue.SourceProjectCode);
        Assert.Null(issue.SourceDocumentKey);
        Assert.Null(issue.SourceApprovedAt);
        Assert.Equal(LandApproved, issue.ApprovedAt);

        var text = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("HSE400_PRJ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HSE400-2099", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ✎ F5-R (D-570): рядок Any несе момент зміни даних джерела (<c>sourceChangedAt</c>) — і читачеві без доступу до
    /// джерела (момент документа-джерела не називає); затвердження джерела — <c>null</c>.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-570")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Рядок_Any_несе_момент_зміни_джерела(bool sourceVisible)
    {
        var changedAt = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        var handler = Arrange([Changed with { SourceApprovedAt = null, SourceChangedAt = changedAt }], sourceVisible, out _);

        var issue = Assert.Single((await handler.HandleAsync(Document, Period, null, CancellationToken.None)).SourceIssues!);

        Assert.Equal(ISourceIssueReader.SourceChangedAfterApproval, issue.Kind);
        Assert.Equal(changedAt, issue.SourceChangedAt);
        Assert.Null(issue.SourceApprovedAt);
        Assert.Equal(sourceVisible ? Source : null, issue.SourceDocumentId);
    }

    /// <summary>Сховищу передається запитаний період; без рядків проблем розрахунку поле все одно рахується.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Періоди_запиту_передаються_сховищу()
    {
        var handler = Arrange([Changed], sourceVisible: true, out var reader);

        await handler.HandleAsync(Document, Period, null, CancellationToken.None);

        await reader.Received(1).ListSourceIssuesAsync(
            Document, Arg.Is<IReadOnlyCollection<int>>(p => p.SequenceEqual(new[] { Period })), Arg.Any<CancellationToken>());
    }

    private static GetCalculationIssuesHandler Arrange(
        IReadOnlyList<SourceIssueRow> rows, bool sourceVisible, out ISourceIssueReader reader)
    {
        var profile = new AccessBuilder { UserId = 9 }
            .Permission(GetCalculationIssuesHandler.Permission)
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Build();

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Source, Arg.Any<CancellationToken>())
              .Returns(sourceVisible ? EditDecision.Allow() : EditDecision.Deny(EditDenyReason.NoGrant));

        // Рядків проблем розрахунку немає — поле джерела не залежить від них.
        var issues = Substitute.For<ICalculationIssueStore>();
        issues.ListAsync(Document, Period, null, Arg.Any<CancellationToken>()).Returns([]);

        reader = Substitute.For<ISourceIssueReader>();
        reader.ListSourceIssuesAsync(Document, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
              .Returns(rows);

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        return new GetCalculationIssuesHandler(issues, access, user, sourceIssues: reader);
    }
}
