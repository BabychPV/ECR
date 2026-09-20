// tests/Ecr.Application.Tests/Consistency/RunConsistencyCheckHandlerTests.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Consistency;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Consistency;

/// <summary>
/// <c>BE-30</c>: перевірку узгодженості можна прогнати на вимогу.
/// </summary>
/// <remarks>
/// ⛔ Рішення людини на <c>Q15-03</c> прибрало «взяти до відома» знахідку:
/// вона зникає сама, коли наступна перевірка проходить. Тому прогін на вимогу
/// — ЄДИНИЙ спосіб зняти з переліку знахідку, причину якої вже усунули, а до
/// цього обробника перевірка ходила лише за нічним розкладом.
/// </remarks>
public sealed class RunConsistencyCheckHandlerTests
{
    private const int Operator = 77;
    private const string Reason = "Полагоджено довідник, звіряємо повторно.";

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    /// <summary>Незавершені задачі, які «бачить» планувальник.</summary>
    private readonly List<JobSummary> _queue = [];

    /// <summary>Остання подія, що пішла в журнал безпеки.</summary>
    private SecurityEventRecord? _recorded;

    public RunConsistencyCheckHandlerTests()
    {
        _user.UserId.Returns(Operator);
        Grant(RunConsistencyCheckHandler.Permission);

        _audit.WriteSecurityEventAsync(
                Arg.Do<SecurityEventRecord>(e => _recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _jobs.EnqueueAsync<IConsistencyCheckJob>(
                Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("IConsistencyCheckJob-new");

        // ⚠ Підміна фільтрує САМА — рівно так, як це робить сховище
        // (`JobProgressStore.ListRecentAsync`: рівність по стану). Повертати
        // весь перелік на будь-який фільтр означало б перевіряти обробник
        // проти вигаданої поведінки порту.
        _jobs.ListRecentAsync(Arg.Any<JobListFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var state = ((JobListFilter)call[0]).State;

                return (IReadOnlyList<JobSummary>)_queue
                    .Where(job => string.Equals(job.State, state, StringComparison.Ordinal))
                    .ToList();
            });
    }

    private RunConsistencyCheckHandler Handler() => new(
        _jobs, _access, _user, _audit, new TestClock(new DateTime(2026, 9, 21, 9, 30, 0, DateTimeKind.Utc)));

    private void Grant(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = Operator };

        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(Operator, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    /// <summary>Незавершена задача в черзі.</summary>
    private static JobSummary Job(string jobId, string jobCode, string state) => new(
        jobId, jobCode, state, 0,
        new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-30")]
    public async Task Без_права_System_RunJob_перевірку_запустити_не_можна()
    {
        // ⛔ `System.RunJob` — одне з восьми прав, які сід видавав і які не
        // відкривали нічого (`BE-28`). Тест доводить, що воно тепер щось
        // означає: без нього дія відмовляє, і задача в чергу НЕ потрапляє.
        Grant();

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(Reason, CancellationToken.None));

        Assert.Contains(RunConsistencyCheckHandler.Permission, denied.Message, StringComparison.Ordinal);

        await _jobs.DidNotReceiveWithAnyArgs()
            .EnqueueAsync<IConsistencyCheckJob>(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-30")]
    public async Task Прогін_ставить_задачу_і_пише_причину_в_журнал_безпеки()
    {
        var jobId = await Handler().HandleAsync($"  {Reason}  ", CancellationToken.None);

        Assert.Equal("IConsistencyCheckJob-new", jobId);

        // ⚠ Автор передається в чергу: без нього той, хто натиснув кнопку, не
        // прочитав би стан ВЛАСНОЇ задачі без `System.ViewHealth` (Q-156).
        await _jobs.Received(1).EnqueueAsync<IConsistencyCheckJob>(
            Arg.Any<object?>(), Arg.Any<CancellationToken>(), Operator);

        // ⛔ Головне твердження: у журналі є і ДІЯ, і ПРИЧИНА. Подія без
        // причини відповідає на «хто» і мовчить про «навіщо» — а прогін на
        // вимогу питають саме про друге.
        await _audit.Received(1).WriteSecurityEventAsync(
            Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>());

        Assert.NotNull(_recorded);
        Assert.Equal(RunConsistencyCheckHandler.EventType, _recorded.EventType);
        Assert.Equal(Operator, _recorded.ChangedByUserId);

        // ⚠ Подробиці РОЗБИРАЮТЬСЯ, а не шукаються підрядком: серіалізатор
        // екранує кирилицю (`П…`), тож пошук рядка тут був би зеленим
        // лише для латиниці — тобто саме на тих причинах, яких не буває.
        var details = JsonDocument.Parse(_recorded.DetailsJson ?? "{}").RootElement;

        // Причина ОБРІЗАНА: у журнал іде те, що ввели, а не пробіли навколо.
        Assert.Equal(Reason, details.GetProperty("reason").GetString());
        Assert.Equal(jobId, details.GetProperty("jobId").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-30")]
    public async Task Без_причини_прогін_відхиляється_і_нічого_не_ставить()
    {
        // ⚠ Пробіли — це «причини немає», а не причина: поле, яке приймає
        // пробіл, перетворює обов'язковість на формальність.
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync("   ", CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestInvalid, refused.ErrorCode);

        await _jobs.DidNotReceiveWithAnyArgs()
            .EnqueueAsync<IConsistencyCheckJob>(default, default, default);
        await _audit.DidNotReceiveWithAnyArgs().WriteSecurityEventAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-30")]
    public async Task Причина_довша_за_стелю_відхиляється()
    {
        // ⚠ Число літералом, а не `MaxReasonLength + 1`: твердження проти
        // константи з того самого модуля рухається разом із нею й нічого не
        // тримає. 400 — це і є вимога.
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(new string('x', 401), CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestInvalid, refused.ErrorCode);
        Assert.Equal(400, RunConsistencyCheckHandler.MaxReasonLength);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-30")]
    public async Task Поки_перевірка_незавершена_другий_прогін_дає_409(string state)
    {
        // ⛔ Два прогони одночасно читають усі партиції `doc.CellValue` і
        // пишуть те саме: `MERGE` зіставляє знахідки за трійкою
        // (RuleCode, EntityType, EntityId), тож другий не додасть жодного
        // рядка. Тому відмова, а не дублікат роботи.
        _queue.Add(Job("IConsistencyCheckJob-busy", "Ecr.Application.Ports.IConsistencyCheckJob", state));

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Reason, CancellationToken.None));

        Assert.Equal(RunConsistencyCheckHandler.AlreadyRunningErrorCode, refused.ErrorCode);

        // Відмова називає ЗАДАЧУ: інакше єдина дія у відповідь — тикати кнопку
        // доти, доки не спрацює.
        Assert.Equal("IConsistencyCheckJob-busy", refused.Details?["jobId"]);
        Assert.Equal(state, refused.Details?["state"]);

        await _jobs.DidNotReceiveWithAnyArgs()
            .EnqueueAsync<IConsistencyCheckJob>(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-30")]
    public async Task Нічний_прогін_тієї_самої_перевірки_теж_блокує()
    {
        // ⛔ Назв у черзі ДВІ. Ручний прогін ставиться маркером, а нічний
        // розклад — конкретним класом з `Ecr.Infrastructure`, і `JobCode` —
        // повне ім'я того типу, яким задачу поставили. Звірка лише з маркером
        // лишала б відкритим рівно той випадок, коли натискають кнопку о 02:15.
        _queue.Add(Job("ConsistencyCheckJob-nightly", "Ecr.Infrastructure.Jobs.ConsistencyCheckJob", "Running"));

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Reason, CancellationToken.None));

        Assert.Equal(RunConsistencyCheckHandler.AlreadyRunningErrorCode, refused.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-30")]
    public async Task Чужа_незавершена_задача_прогону_не_заважає()
    {
        // ⛔ Контроль до двох тестів вище: блокує САМЕ перевірка узгодженості,
        // а не «будь-що в черзі». Без цього твердження обробник, що відмовляє
        // на кожну активну задачу, виглядав би правильним — і двадцятихвилинний
        // перерахунок чужого проєкту закривав би кнопку всім.
        _queue.Add(Job("IRecalculationJob-42", "Ecr.Application.Ports.IRecalculationJob", "Running"));

        var jobId = await Handler().HandleAsync(Reason, CancellationToken.None);

        Assert.Equal("IConsistencyCheckJob-new", jobId);
    }
}
