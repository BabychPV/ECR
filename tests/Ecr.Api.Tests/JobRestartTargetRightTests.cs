using Ecr.Api.Controllers;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// L1-19 (AN-31 хвіст): автор повторює СВОЮ провалену задачу лише доти, доки право на
/// САМУ дію (за типом задачі і її документом) ще за ним. Збір, синк подій і перевірка
/// узгодженості прав під час виконання не перевіряють — відкликання права мало б
/// лишати «Повторити» в шухляді «Мої задачі» живою кнопкою.
/// </summary>
/// <remarks>
/// Відмова — та сама <c>403 jobNotYours</c>, що й для чужої задачі: автор і так знає, що
/// задача його, а перелік прав чи документів тут не розкривається. Експорт та імпорт
/// права перевіряють самі під час виконання — для них нічого не змінилося.
/// </remarks>
public sealed class JobRestartTargetRightTests
{
    private const int Author = 9;
    private const long DocumentId = 77;

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("ICollectionJob")]
    [InlineData("ISourceEventSyncJob")]
    public async Task Збір_і_синк_після_відкликання_Integration_Manage_403(string code)
    {
        var jobId = Failed(code);
        SignedIn("Integration.View");

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Controller().Restart(jobId, CancellationToken.None));

        Assert.Equal("err.ECR-AUTH-0403.jobNotYours", denied.Details?["messageKey"]);
        await _jobs.DidNotReceive().RestartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("ICollectionJob")]
    [InlineData("ISourceEventSyncJob")]
    public async Task Збір_і_синк_з_правом_Integration_Manage_повторюються(string code)
    {
        var jobId = Failed(code);
        SignedIn("Integration.Manage");

        Assert.IsType<AcceptedResult>(await Controller().Restart(jobId, CancellationToken.None));
        await _jobs.Received(1).RestartAsync(jobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Перевірка_узгодженості_без_System_RunJob_403_а_з_правом_повторюється()
    {
        var jobId = Failed("IConsistencyCheckJob");
        SignedIn("Integration.View");

        await Assert.ThrowsAsync<AccessDeniedException>(() => Controller().Restart(jobId, CancellationToken.None));
        await _jobs.DidNotReceive().RestartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        SignedIn("System.RunJob");
        Assert.IsType<AcceptedResult>(await Controller().Restart(jobId, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Перерахунок_документа_що_став_невидимим_авторові_403_jobNotYours()
    {
        var jobId = Failed("IRecalculationJob", DocumentId);
        SignedIn("Document.View");
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Controller().Restart(jobId, CancellationToken.None));

        // Відповідь — та сама, що для чужої задачі: без 404 документа і без назви права.
        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        Assert.Equal("err.ECR-AUTH-0403.jobNotYours", denied.Details?["messageKey"]);
        await _jobs.DidNotReceive().RestartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Перерахунок_документа_автор_без_Document_View_403()
    {
        var jobId = Failed("IRecalculationJob", DocumentId);
        SignedIn();
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        await Assert.ThrowsAsync<AccessDeniedException>(() => Controller().Restart(jobId, CancellationToken.None));
        await _jobs.DidNotReceive().RestartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Перерахунок_документа_з_правом_і_видимістю_повторюється()
    {
        var jobId = Failed("IRecalculationJob", DocumentId);
        SignedIn("Document.View");
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        Assert.IsType<AcceptedResult>(await Controller().Restart(jobId, CancellationToken.None));
        await _jobs.Received(1).RestartAsync(jobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Невідомий_тип_задачі_автору_без_ViewHealth_закритий()
    {
        var jobId = Failed("IUnknownJob");
        SignedIn();

        await Assert.ThrowsAsync<AccessDeniedException>(() => Controller().Restart(jobId, CancellationToken.None));
        await _jobs.DidNotReceive().RestartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task З_ViewHealth_будь_який_тип_повторюється_без_права_на_дію()
    {
        var jobId = Failed("ICollectionJob");
        SignedIn("System.ViewHealth");

        Assert.IsType<AcceptedResult>(await Controller().Restart(jobId, CancellationToken.None));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("IExcelExportJob")]
    [InlineData("IExcelImportJob")]
    public async Task Експорт_та_імпорт_самі_перевіряють_право_тому_перезапуск_автору_лишається(string code)
    {
        var jobId = Failed(code, DocumentId);
        SignedIn();

        Assert.IsType<AcceptedResult>(await Controller().Restart(jobId, CancellationToken.None));
    }

    // Реальний формат Quartz для цільових задач: `{Тип}~{ціль}~{guid}` (TargetPrefixOf).
    private const string Guid32 = "0123456789abcdef0123456789abcdef";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Quartz_перерахунок_документа_видимий_повторюється_а_невидимий_403()
    {
        var jobId = Failed("IRecalculationJob", DocumentId, $"IRecalculationJob~doc{DocumentId}-p202609~{Guid32}");
        SignedIn("Document.View");
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        Assert.IsType<AcceptedResult>(await Controller().Restart(jobId, CancellationToken.None));

        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Controller().Restart(jobId, CancellationToken.None));
        Assert.Equal("err.ECR-AUTH-0403.jobNotYours", denied.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Quartz_каскад_правки_документа_видимий_повторюється()
    {
        var jobId = Failed("IFormulaRecalculationJob", DocumentId, $"IFormulaRecalculationJob~d{DocumentId}~{Guid32}");
        SignedIn("Document.View");
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        Assert.IsType<AcceptedResult>(await Controller().Restart(jobId, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Quartz_синк_подій_джерела_потребує_Integration_Manage()
    {
        var jobId = Failed("ISourceEventSyncJob", null, $"ISourceEventSyncJob~src1~{Guid32}");
        SignedIn("Integration.View");

        await Assert.ThrowsAsync<AccessDeniedException>(() => Controller().Restart(jobId, CancellationToken.None));

        SignedIn("Integration.Manage");
        Assert.IsType<AcceptedResult>(await Controller().Restart(jobId, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Quartz_скан_сиріт_лише_з_ViewHealth()
    {
        var jobId = Failed("IOrphanScanJob", null, $"IOrphanScanJob~all~{Guid32}");
        SignedIn("Period.Reopen");

        await Assert.ThrowsAsync<AccessDeniedException>(() => Controller().Restart(jobId, CancellationToken.None));

        SignedIn("System.ViewHealth");
        Assert.IsType<AcceptedResult>(await Controller().Restart(jobId, CancellationToken.None));
    }

    private string Failed(string code, long? documentId = null, string? fullId = null)
    {
        var jobId = fullId ?? $"{code}-{Guid32}";
        _jobs.GetStatusAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(new JobStatus(jobId, "Failed", 30, null, "boom", ErrorCode: "ECR-SYS-0500", DocumentId: documentId));
        _jobs.RestartAsync(jobId, Arg.Any<CancellationToken>()).Returns(true);
        _jobs.GetCreatedByUserIdAsync(jobId, Arg.Any<CancellationToken>()).Returns(Author);

        return jobId;
    }

    private void SignedIn(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = Author };
        foreach (var permission in permissions)
        {
            builder.Permission(permission);
        }

        _user.UserId.Returns(Author);
        _access.BuildProfileAsync(Author, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private JobsController Controller() => new(
        new GetJobStatusHandler(_jobs, _access, _user, new FakeUiStringCatalog()),
        new ListJobsHandler(_jobs, _access, _user, new FakeUiStringCatalog()),
        new RestartJobHandler(_jobs, _access, _user),
        new CancelJobHandler(_jobs, _access, _user));
}
