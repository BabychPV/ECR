using System.Reflection;
using Ecr.Api.Controllers;
using Ecr.Api.Errors;
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
/// BE-02: довгу фонову задачу можна зупинити з інтерфейсу.
/// </summary>
/// <remarks>
/// ⛔ До цього річний перерахунок (двадцять хвилин у чинній системі) не
/// зупинявся НІЧИМ: <c>IBackgroundJobScheduler.CancelAsync</c> кликало лише
/// витіснення зсередини планувальника (<c>D2-64</c>), тобто «щоб зупинити —
/// запусти ще раз».
///
/// ⚠ Тести йдуть через ОБРОБНИК і через ДІЮ КОНТРОЛЕРА на підроблених портах,
/// а не через HTTP на живій базі: предмет перевірки — межа права й межа стану,
/// а вони цілком живуть в обробнику (архітектурне правило 7). Статус <c>409</c>
/// перевіряється на тій самій мапі конвеєра, що й решта кодів
/// (<c>ErrorContractTests</c>), бо саме вона перетворює виняток на відповідь.
/// </remarks>
public sealed class JobCancelTests
{
    private const string JobId = "IRecalculationJob#42";
    private const int Author = 9;
    private const int Stranger = 10;

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    /// <summary>Стан, який повертає підроблений планувальник; <c>CancelAsync</c> його змінює.</summary>
    private string _state = "Running";

    public JobCancelTests()
    {
        _jobs.GetStatusAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(_ => new JobStatus(JobId, _state, 40, null, null));

        // ⚠ Підробка ВЕДЕ СЕБЕ як адаптер: `QuartzJobAdapter` закриває прогін
        // станом `Cancelled` (`QuartzJobAdapter.cs:143`). Без цього тест
        // «після скасування GET дає Cancelled» перевіряв би власну константу.
        _jobs.CancelAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _state = "Cancelled";
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Задача, що виконується: дія приймає запит (<c>202</c>), просить
    /// планувальник, і наступне читання стану дає <c>Cancelled</c>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Скасування_задачі_що_виконується_дає_202_і_далі_стан_Cancelled()
    {
        WithHealthPermission(Stranger);

        var accepted = Assert.IsType<AcceptedResult>(
            await Controller().Cancel(JobId, CancellationToken.None));

        // ⚠ Саме 202, а не 204: у мить відповіді задача ЩЕ виконується —
        // скасування це прохання, яке вона побачить на межі батчу.
        Assert.Equal(202, accepted.StatusCode);
        var body = Assert.IsType<Ecr.Api.Contracts.JobAcceptedResponse>(accepted.Value);
        Assert.Equal(JobId, body.JobId);

        await _jobs.Received(1).CancelAsync(JobId, Arg.Any<CancellationToken>());

        var after = await _jobs.GetStatusAsync(JobId, CancellationToken.None);
        Assert.Equal("Cancelled", after.State);
    }

    /// <summary>Завершену задачу скасовувати нічого — <c>409</c>, не <c>202</c>.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Успішну_задачу_скасувати_не_можна()
    {
        _state = "Succeeded";
        WithHealthPermission(Stranger);

        var conflict = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(JobId, CancellationToken.None));

        Assert.Equal(CancelJobHandler.NotActiveErrorCode, conflict.ErrorCode);
        await _jobs.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // ⛔ Код каже 409 — і конвеєр мусить це підтвердити. `ECR-ROW-0409`
        // доїжджав клієнтові як 422 саме тому, що власного арма не мав
        // (`Q-150`), і тут та сама пастка: арм у `ExceptionHandlingMiddleware`
        // написаний під `RestartJobHandler`, а код збігається значенням.
        var map = typeof(ExceptionHandlingMiddleware)
            .GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!;

        var (status, code, _, _) =
            ((int, string, string, IReadOnlyDictionary<string, object?>?))map.Invoke(null, [conflict])!;

        Assert.Equal(409, status);
        Assert.Equal(CancelJobHandler.NotActiveErrorCode, code);
    }

    /// <summary>Чужу задачу без <c>System.ViewHealth</c> не скасувати.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Чужу_задачу_без_ViewHealth_скасувати_не_можна()
    {
        WithoutPermission(Stranger);
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns(Author);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(JobId, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        await _jobs.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// ⛔ Головне твердження файлу (Q-156, та сама межа, що в
    /// <see cref="GetJobStatusHandler"/>): автор скасовує ВЛАСНУ задачу без
    /// <c>System.ViewHealth</c>.
    /// </summary>
    /// <remarks>
    /// Інакше оператор, який щойно запустив двадцятихвилинний перерахунок
    /// помилково, отримав би <c>jobId</c> у відповіді <c>202</c> і <c>403</c>
    /// на спробу його спинити — рівно той дефект, що <c>S-28</c> для читання
    /// стану.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Свою_задачу_автор_скасовує_без_ViewHealth()
    {
        WithoutPermission(Author);
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns(Author);

        await Handler().HandleAsync(JobId, CancellationToken.None);

        await _jobs.Received(1).CancelAsync(JobId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Системна задача (за розкладом, <c>CreatedByUserId = null</c>) не належить
    /// нікому: без права її не скасувати.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Системна_задача_без_автора_не_скасовується_без_ViewHealth()
    {
        WithoutPermission(Author);
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns((int?)null);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(JobId, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    /// <summary>
    /// Невідома задача — <c>404</c>, і саме ДО перевірки права: інакше
    /// «задачі немає» ховалося б за «немає права» (той самий порядок, що
    /// Q-179/Q-180).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Невідома_задача_дає_404_а_не_403()
    {
        WithoutPermission(Stranger);
        _jobs.GetStatusAsync("no-such-job", Arg.Any<CancellationToken>())
            .Returns(new JobStatus("no-such-job", "Unknown", 0, null, null));

        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync("no-such-job", CancellationToken.None));

        Assert.Equal("ECR-JOB-0404", missing.ErrorCode);
        await _jobs.DidNotReceive().GetCreatedByUserIdAsync("no-such-job", Arg.Any<CancellationToken>());
    }

    private void WithHealthPermission(int userId)
    {
        _user.UserId.Returns(userId);
        _access.BuildProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = userId }
                .Permission(GetJobStatusHandler.Permission)
                .Build());
    }

    private void WithoutPermission(int userId)
    {
        _user.UserId.Returns(userId);
        _access.BuildProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = userId }.Build());
    }

    private CancelJobHandler Handler() => new(_jobs, _access, _user);

    /// <summary>
    /// Контролер зі СПРАВЖНІМИ обробниками на підроблених портах.
    /// </summary>
    /// <remarks>
    /// ⚠ Обробники — запечатані класи без інтерфейсу, підмінити їх не можна й
    /// не треба: перевіряється саме та зв'язка, що працює в проді — дія кличе
    /// обробник, а обробник кличе порт.
    /// </remarks>
    private JobsController Controller() => new(
        new GetJobStatusHandler(_jobs, _access, _user, new FakeUiStringCatalog()),
        new ListJobsHandler(_jobs, _access, _user, new FakeUiStringCatalog()),
        new RestartJobHandler(_jobs, _access, _user),
        Handler());
}
