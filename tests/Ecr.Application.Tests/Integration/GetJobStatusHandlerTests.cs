using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Q-156: автор власної задачі читає її стан без <c>System.ViewHealth</c>;
/// те право лишається обов'язковим лише для ЧУЖИХ задач.
/// </summary>
public sealed class GetJobStatusHandlerTests
{
    private const string JobId = "job-1";
    private const int Author = 9;
    private const int Stranger = 10;

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    // ⚠ Q-326: підробка каталогу, не substitute без налаштувань — тести
    // цього файлу навмисно перевіряють ПРАВА, а `status.Message` тут завжди
    // "export-abc": не JSON-конверт, тож резолвер (`JobProgressMessageResolver`)
    // повертає його як є й НІКОЛИ не звертається до каталогу. Підробка лишає
    // це видимим, а не прихованим за незвʼязаним substitute.
    private readonly FakeUiStringCatalog _catalog = new();

    public GetJobStatusHandlerTests()
    {
        _jobs.GetStatusAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new JobStatus(JobId, "Succeeded", 100, "export-abc", null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Автор_читає_стан_власної_задачі_без_ViewHealth()
    {
        _user.UserId.Returns(Author);
        _access.BuildProfileAsync(Author, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Author }.Build());
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns(Author);

        var status = await Handler().HandleAsync(JobId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("Succeeded", status!.State);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Чужий_без_ViewHealth_відхиляється()
    {
        _user.UserId.Returns(Stranger);
        _access.BuildProfileAsync(Stranger, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Stranger }.Build());
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns(Author);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(JobId, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task З_ViewHealth_чужа_задача_видима()
    {
        _user.UserId.Returns(Stranger);
        _access.BuildProfileAsync(Stranger, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Stranger }.Permission(GetJobStatusHandler.Permission).Build());
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns(Author);

        var status = await Handler().HandleAsync(JobId, CancellationToken.None);

        Assert.NotNull(status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Невідома_задача_дає_404_а_не_403_без_права()
    {
        _user.UserId.Returns(Stranger);
        _access.BuildProfileAsync(Stranger, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Stranger }.Build());
        _jobs.GetStatusAsync("unknown", Arg.Any<CancellationToken>())
            .Returns(new JobStatus("unknown", "Unknown", 0, null, null));

        var status = await Handler().HandleAsync("unknown", CancellationToken.None);

        Assert.Null(status);
        await _jobs.DidNotReceive().GetCreatedByUserIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Q-326: структурований конверт (<see cref="JobProgressMessageEnvelope"/>)
    /// резолвиться мовою ЧИТАЧА, а не написаний готовим українським рядком —
    /// саме той дефект, який lane6 медіум-аудиту (<c>Q-325</c>) знайшов
    /// системним у 13 файлах фонових задач.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Структурований_конверт_прогресу_резолвиться_каталогом_мовою_читача()
    {
        const string StructuredJobId = "job-structured";
        _catalog.Add("en", "jobs.recalcFormulasDone", "Template formulas: recalculated cells — {cells}.");

        var envelope = new JobProgressMessageEnvelope(
            "jobs.recalcFormulasDone",
            new Dictionary<string, string> { ["cells"] = "42" });
        _jobs.GetStatusAsync(StructuredJobId, Arg.Any<CancellationToken>())
            .Returns(new JobStatus(
                StructuredJobId, "Succeeded", 100, JobProgressMessageCodec.Encode(envelope), null));

        _user.UserId.Returns(Author);
        _user.Language.Returns("en");
        _access.BuildProfileAsync(Author, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Author }.Build());
        _jobs.GetCreatedByUserIdAsync(StructuredJobId, Arg.Any<CancellationToken>()).Returns(Author);

        var status = await Handler().HandleAsync(StructuredJobId, CancellationToken.None);

        Assert.Equal("Template formulas: recalculated cells — 42.", status!.Message);
    }

    /// <summary>
    /// Q-326: старий прямий запис (готовий український текст, ДО цієї
    /// картки) не є JSON-конвертом — лишається читабельним як є, а не
    /// перетворюється на ключ каталогу чи порожнечу.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Старий_прямий_текст_без_конверта_лишається_без_змін()
    {
        const string LegacyJobId = "job-legacy";
        _jobs.GetStatusAsync(LegacyJobId, Arg.Any<CancellationToken>())
            .Returns(new JobStatus(LegacyJobId, "Succeeded", 100, "Перерахунок методологій.", null));

        _user.UserId.Returns(Author);
        _user.Language.Returns("en");
        _access.BuildProfileAsync(Author, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Author }.Build());
        _jobs.GetCreatedByUserIdAsync(LegacyJobId, Arg.Any<CancellationToken>()).Returns(Author);

        var status = await Handler().HandleAsync(LegacyJobId, CancellationToken.None);

        Assert.Equal("Перерахунок методологій.", status!.Message);
    }

    private GetJobStatusHandler Handler() => new(_jobs, _access, _user, _catalog);
}
