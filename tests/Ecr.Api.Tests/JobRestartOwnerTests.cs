using Ecr.Api.Controllers;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// UX-09, шухляда «Мої задачі»: автор повторює СВОЮ провалену задачу без
/// <c>System.ViewHealth</c> (та сама межа, що в скасуванні, Q-156), а
/// завершений експорт несе <c>resultUrl</c> на книгу.
/// </summary>
/// <remarks>
/// Той самий прийом, що в <see cref="JobCancelTests"/>: справжні обробники й дія
/// контролера на підроблених портах. Що за <c>resultUrl</c> файл справді
/// віддається тому самому користувачеві, доводить наскрізний
/// <c>OutputScenarios</c> (крок 2 — завантаження саме за <c>resultUrl</c>).
/// </remarks>
public sealed class JobRestartOwnerTests
{
    private const string JobId = "IExcelExportJob-0123456789abcdef0123456789abcdef";
    private const string ExportId = "fedcba9876543210fedcba9876543210";
    private const long DocumentId = 77;
    private const int Author = 9;
    private const int Stranger = 10;

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public JobRestartOwnerTests()
    {
        _jobs.GetStatusAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new JobStatus(JobId, "Failed", 30, null, "boom", ErrorCode: "ECR-JOB-0500"));
        _jobs.RestartAsync(JobId, Arg.Any<CancellationToken>()).Returns(true);
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns(Author);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Автор_без_ViewHealth_повторює_свою_провалену_задачу()
    {
        SignedIn(Author);

        var accepted = Assert.IsType<AcceptedResult>(await Controller().Restart(JobId, CancellationToken.None));

        Assert.Equal(202, accepted.StatusCode);
        await _jobs.Received(1).RestartAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Чужу_задачу_без_ViewHealth_не_повторити()
    {
        SignedIn(Stranger);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Controller().Restart(JobId, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        await _jobs.DidNotReceive().RestartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Системну_задачу_без_ViewHealth_не_повторити()
    {
        SignedIn(Author);
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns((int?)null);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Controller().Restart(JobId, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        await _jobs.DidNotReceive().RestartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task З_ViewHealth_повторюється_будь_яка_задача_навіть_системна()
    {
        SignedIn(Stranger, GetJobStatusHandler.Permission);
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns((int?)null);

        Assert.IsType<AcceptedResult>(await Controller().Restart(JobId, CancellationToken.None));
        await _jobs.Received(1).RestartAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Завершений_експорт_несе_resultUrl_у_стані_і_в_переліку()
    {
        SignedIn(Author, "Document.Export");
        _jobs.GetStatusAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new JobStatus(JobId, "Succeeded", 100, ExportId, null, DocumentId: DocumentId));
        _jobs.ListRecentAsync(Arg.Any<JobListFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Summary(JobId, "Ecr.Application.Ports.IExcelExportJob", "Succeeded", ExportId)]);

        // Шлях — літералом: саме його клієнт віддає в `<a href>`.
        const string expected = "/api/v1/documents/77/export/fedcba9876543210fedcba9876543210";

        var status = Assert.IsType<OkObjectResult>((await Controller().Get(JobId, CancellationToken.None)).Result);
        Assert.Equal(expected, Assert.IsType<JobStatus>(status.Value).ResultUrl);

        var list = Assert.IsType<OkObjectResult>(
            (await Controller().List(null, null, mine: true, null, CancellationToken.None)).Result);
        Assert.Equal(expected, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<JobSummary>>(list.Value)).ResultUrl);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task ResultUrl_null_для_провалу_не_експорту_і_без_права_на_експорт()
    {
        SignedIn(Author, "Document.Export");
        _jobs.ListRecentAsync(Arg.Any<JobListFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                Summary("a", "Ecr.Application.Ports.IExcelExportJob", "Failed", ExportId),
                Summary("b", "Ecr.Application.Ports.IRecalculationJob", "Succeeded", ExportId),
                Summary("c", "Ecr.Application.Ports.IExcelExportJob", "Succeeded", "done"),
            ]);

        var list = Assert.IsType<OkObjectResult>(
            (await Controller().List(null, null, mine: true, null, CancellationToken.None)).Result);

        Assert.All(Assert.IsAssignableFrom<IReadOnlyList<JobSummary>>(list.Value), s => Assert.Null(s.ResultUrl));

        // Без права маршруту завантаження посилання не віддається.
        SignedIn(Author);
        _jobs.GetStatusAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new JobStatus(JobId, "Succeeded", 100, ExportId, null, DocumentId: DocumentId));

        var status = Assert.IsType<OkObjectResult>((await Controller().Get(JobId, CancellationToken.None)).Result);
        Assert.Null(Assert.IsType<JobStatus>(status.Value).ResultUrl);
    }

    private static JobSummary Summary(string jobId, string code, string state, string message)
        => new(jobId, code, state, 100, DateTime.UtcNow, DateTime.UtcNow, Message: message, DocumentId: DocumentId);

    private void SignedIn(int userId, params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = userId };
        foreach (var permission in permissions)
        {
            builder.Permission(permission);
        }

        _user.UserId.Returns(userId);
        _access.BuildProfileAsync(userId, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private JobsController Controller() => new(
        new GetJobStatusHandler(_jobs, _access, _user, new FakeUiStringCatalog()),
        new ListJobsHandler(_jobs, _access, _user, new FakeUiStringCatalog()),
        new RestartJobHandler(_jobs, _access, _user),
        new CancelJobHandler(_jobs, _access, _user));
}
