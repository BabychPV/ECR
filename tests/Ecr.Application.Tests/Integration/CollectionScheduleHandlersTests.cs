// tests/Ecr.Application.Tests/Integration/CollectionScheduleHandlersTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Редагування розкладу збору (<c>BE-21b</c>, ФВ-14.3): cron перевіряється ДО
/// бази, <c>If-Match</c> стереже паралельну правку, видалення знімає задачу з
/// планувальника.
/// </summary>
public sealed class CollectionScheduleHandlersTests
{
    private const int Actor = 7;
    private const string Hourly = "0 5 * * * ?";
    private const string Nightly = "0 15 2 * * ?";

    /// <summary>П'ятипольний unix-cron: планувальник його не приймає.</summary>
    private const string Unsupported = "15 2 * * *";

    private static readonly DateTime Now = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeStore _store = new();
    private readonly FakeScheduler _scheduler = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public CollectionScheduleHandlersTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Actor);
        Allow("Integration.EditSchedule");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21b")]
    public async Task Невалідний_cron_відхиляється_ДО_бази_і_розклад_лишається_попереднім()
    {
        var schedule = Add(Hourly);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(schedule.Id, Unsupported, isEnabled: true, Version(schedule), CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.collectionScheduleCron", refused.Details!["messageKey"]);

        // ⛔ Головне твердження: відмова прийшла ДО запису. Перевірка, яка
        // дивиться лише на код відповіді, лишилась би зеленою й тоді, коли
        // невалідний cron уже збережено, а відмову віддав планувальник.
        Assert.Equal(Hourly, schedule.CronExpression);
        Assert.Empty(_scheduler.Scheduled);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21b")]
    public async Task Cron_довший_за_ширину_стовпця_відхиляється_як_і_порожній()
    {
        var schedule = Add(Hourly);

        // ⚠ Саме 101 символ ЛІТЕРАЛОМ, а не `MaxCronLength + 1`: твердження
        // проти константи з того самого модуля поїхало б разом із нею, і
        // розширення стовпця до 4000 лишило б перевірку зеленою.
        var tooLong = new string('*', 101);

        var long_ = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(schedule.Id, tooLong, isEnabled: true, Version(schedule), CancellationToken.None));
        var empty = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(schedule.Id, "   ", isEnabled: true, Version(schedule), CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.collectionScheduleCronLength", long_.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.collectionScheduleCronLength", empty.Details!["messageKey"]);
        Assert.Equal(Hourly, schedule.CronExpression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21b")]
    public async Task Чужа_версія_рядка_дає_409_а_запит_без_If_Match_дає_422()
    {
        var schedule = Add(Hourly);

        var stale = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Save().HandleAsync(schedule.Id, Nightly, isEnabled: true, "AAAAAAAAAAE=", CancellationToken.None));
        var headerless = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(schedule.Id, Nightly, isEnabled: true, ifMatch: null, CancellationToken.None));

        Assert.Equal("ECR-JOB-0409", stale.ErrorCode);
        Assert.Equal("err.ECR-JOB-0409.collectionScheduleChanged", stale.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.collectionScheduleIfMatch", headerless.Details!["messageKey"]);
        Assert.Equal(Hourly, schedule.CronExpression);

        // Своя версія проходить і в лапках, і без них — обидві форми ETag живі.
        var saved = await Save().HandleAsync(
            schedule.Id, Nightly, isEnabled: true, $"\"{Version(schedule)}\"", CancellationToken.None);

        Assert.Equal(Nightly, saved.Cron);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21b")]
    public async Task Успішна_зміна_доводить_розклад_до_планувальника_а_вимкнення_знімає_його()
    {
        var schedule = Add(Hourly);
        schedule.MarkInvalid("попередня постановка не вдалася", Now);

        var enabled = await Save().HandleAsync(
            schedule.Id, Nightly, isEnabled: true, Version(schedule), CancellationToken.None);

        Assert.Equal((Nightly, true, (string?)null), (enabled.Cron, enabled.IsEnabled, enabled.LastError));
        Assert.Equal("ENT-1", enabled.SourceEntityCode);

        var placed = _scheduler.Scheduled.Single();
        Assert.Equal(Nightly, placed.Cron);
        Assert.Equal(CollectionScheduleApplier.PayloadOf(schedule.SourceEntityId), placed.Payload);

        var disabled = await Save().HandleAsync(
            schedule.Id, Nightly, isEnabled: false, Version(schedule), CancellationToken.None);

        Assert.False(disabled.IsEnabled);
        Assert.Equal(CollectionScheduleApplier.PayloadOf(schedule.SourceEntityId), _scheduler.Unscheduled.Single());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21b")]
    public async Task Збій_постановки_лишає_LastError_у_рядку_і_чесну_відмову_а_не_ок()
    {
        var schedule = Add(Hourly);
        _scheduler.RefuseSchedule = true;

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(schedule.Id, Nightly, isEnabled: true, Version(schedule), CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.collectionScheduleNotApplied", refused.Details!["messageKey"]);

        // Cron уже змінено — це правда, і вона видима; але «увімкнено» без
        // причини відмови було б неправдою.
        Assert.Equal(Nightly, schedule.CronExpression);
        Assert.Equal(Now, schedule.LastErrorAt);
        Assert.False(string.IsNullOrWhiteSpace(schedule.LastError));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21b")]
    public async Task Видалення_знімає_задачу_з_планувальника_і_лише_потім_прибирає_рядок()
    {
        var schedule = Add(Hourly);

        await Delete().HandleAsync(schedule.Id, Version(schedule), CancellationToken.None);

        // ⛔ Рядок читає лише старт застосунку, а тригер живе в планувальнику:
        // без зняття збір ішов би за розкладом, якого вже немає.
        Assert.Equal(CollectionScheduleApplier.PayloadOf(schedule.SourceEntityId), _scheduler.Unscheduled.Single());
        Assert.Same(schedule, _store.Removed.Single());

        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Delete().HandleAsync(schedule.Id, Version(schedule), CancellationToken.None));

        Assert.Equal("err.ECR-INT-0404.collectionSchedule", missing.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21c")]
    public async Task Створення_заводить_розклад_ставить_його_в_планувальник_а_другий_на_ту_саму_сутність_дає_409()
    {
        _store.Entities[77] = ("ENT-77", "Entity 77");

        var created = await Create().HandleAsync(77, Nightly, isEnabled: true, CancellationToken.None);

        Assert.Equal((77, Nightly, true), (created.SourceEntityId, created.Cron, created.IsEnabled));
        Assert.Equal(("ENT-77", "Entity 77"), (created.SourceEntityCode, created.SourceEntityName));

        var placed = _scheduler.Scheduled.Single();
        Assert.Equal(Nightly, placed.Cron);
        Assert.Equal(CollectionScheduleApplier.PayloadOf(77), placed.Payload);

        // ⛔ Головне твердження. Другий розклад на ту саму сутність — це другий
        // тригер планувальника з ТИМ САМИМ завданням, тобто подвійний збір,
        // якого не видно ніде, крім кількості прогонів.
        var duplicate = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Create().HandleAsync(77, Hourly, isEnabled: true, CancellationToken.None));

        Assert.Equal("ECR-JOB-0409", duplicate.ErrorCode);
        Assert.Equal("err.ECR-JOB-0409.collectionScheduleExists", duplicate.Details!["messageKey"]);
        Assert.Single(_store.Rows);
        Assert.Single(_scheduler.Scheduled);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21c")]
    public async Task Створення_з_невалідним_cron_або_для_неіснуючої_сутності_не_доходить_до_бази()
    {
        _store.Entities[77] = ("ENT-77", null);

        var badCron = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(77, Unsupported, isEnabled: true, CancellationToken.None));
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Create().HandleAsync(999, Nightly, isEnabled: true, CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.collectionScheduleCron", badCron.Details!["messageKey"]);
        Assert.Equal("err.ECR-INT-0404.sourceEntity", missing.Details!["messageKey"]);

        Assert.Empty(_store.Rows);
        Assert.Empty(_scheduler.Scheduled);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21b")]
    public async Task Без_права_Integration_EditSchedule_жодна_дія_не_виконується()
    {
        var schedule = Add(Hourly);
        _store.Entities[77] = ("ENT-77", null);
        Allow("Integration.Manage");

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => new ListCollectionSchedulesHandler(_store, _access, _user).HandleAsync(null, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Create().HandleAsync(77, Nightly, isEnabled: true, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().HandleAsync(schedule.Id, Nightly, isEnabled: false, Version(schedule), CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Delete().HandleAsync(schedule.Id, Version(schedule), CancellationToken.None));

        Assert.Equal(Hourly, schedule.CronExpression);
        Assert.Single(_store.Rows);
        Assert.Empty(_store.Removed);
        Assert.Empty(_scheduler.Unscheduled);
    }

    private void Allow(string permission)
        => _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(permission).Build());

    private CreateCollectionScheduleHandler Create()
        => new(_store, _scheduler, new CollectionScheduleApplier(_scheduler), _access, _uow, _user, _clock);

    private SaveCollectionScheduleHandler Save()
        => new(_store, _scheduler, new CollectionScheduleApplier(_scheduler), _access, _uow, _user, _clock);

    private DeleteCollectionScheduleHandler Delete() => new(_store, _scheduler, _access, _uow, _user);

    private static string Version(CollectionSchedule schedule) => Convert.ToBase64String(schedule.RowVersion);

    /// <summary>Розклад із присвоєним ключем і версією рядка, як його віддала б база.</summary>
    private CollectionSchedule Add(string cron)
    {
        var id = _store.Rows.Count + 1;
        var schedule = new CollectionSchedule(sourceEntityId: 40 + id, cron);

        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(schedule, id);
        typeof(CollectionSchedule).GetProperty(nameof(CollectionSchedule.RowVersion))!
            .SetValue(schedule, new byte[] { 0, 0, 0, 0, 0, 0, 7, (byte)id });

        _store.Rows.Add(new ScheduledSourceEntity(schedule, $"ENT-{id}", $"Entity {id}", 3, "SRC-3"));

        return schedule;
    }

    private sealed class FakeStore : ICollectionScheduleStore
    {
        public List<ScheduledSourceEntity> Rows { get; } = [];

        public List<CollectionSchedule> Removed { get; } = [];

        /// <summary>Сутності джерела, які «є в базі»: ключ → код і підпис.</summary>
        public Dictionary<int, (string Code, string? Name)> Entities { get; } = [];

        public Task<IReadOnlyList<ScheduledSourceEntity>> ListAsync(string? dataSourceCode, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ScheduledSourceEntity>>(Rows);

        public Task<ScheduledSourceEntity?> FindAsync(int collectionScheduleId, CancellationToken ct)
            => Task.FromResult(Rows.Find(r => r.Schedule.Id == collectionScheduleId));

        public Task<SourceEntityScheduling?> FindSourceEntityAsync(int sourceEntityId, CancellationToken ct)
            => Task.FromResult(
                Entities.TryGetValue(sourceEntityId, out var entity)
                    ? new SourceEntityScheduling(
                        entity.Code,
                        entity.Name,
                        Rows.Find(r => r.Schedule.SourceEntityId == sourceEntityId)?.Schedule.Id,
                        3,
                        "SRC-3")
                    : null);

        /// <summary>Ключ присвоюється одразу — базу тут заміняє цей список.</summary>
        public void Add(CollectionSchedule schedule)
        {
            typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!
                .SetValue(schedule, Rows.Count + 1);

            var entity = Entities[schedule.SourceEntityId];
            Rows.Add(new ScheduledSourceEntity(schedule, entity.Code, entity.Name, 3, "SRC-3"));
        }

        public void Remove(CollectionSchedule schedule)
        {
            Removed.Add(schedule);
            Rows.RemoveAll(r => r.Schedule.Id == schedule.Id);
        }
    }

    /// <summary>
    /// Планувальник, який запам'ятовує постановки.
    /// </summary>
    /// <remarks>
    /// ⚠ Синтаксис cron перевіряє САМ планувальник (Quartz), і його правила
    /// доводить <c>RecurringUnscheduleTests</c>. Тут перевіряється інше: що
    /// обробник питає порт і зупиняється на «ні» — тому фейк приймає рівно
    /// шестипольні вирази, і цього достатньо, щоб відрізнити запитаний порт від
    /// незапитаного.
    /// </remarks>
    private sealed class FakeScheduler : IBackgroundJobScheduler
    {
        public List<(string Cron, object? Payload)> Scheduled { get; } = [];

        public List<object?> Unscheduled { get; } = [];

        /// <summary>Постановка відмовляє — як Quartz на виразі, який він не взяв.</summary>
        public bool RefuseSchedule { get; set; }

        public bool IsValidCron(string expression, out string? error)
        {
            if (expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is 6 or 7)
            {
                error = null;
                return true;
            }

            error = "cron expression is not supported";
            return false;
        }

        public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct)
            where TJob : IBackgroundJob
        {
            if (RefuseSchedule)
            {
                throw new ArgumentException("scheduler refused the trigger", nameof(cronExpression));
            }

            Scheduled.Add((cronExpression, payload));

            return Task.CompletedTask;
        }

        public Task<bool> UnscheduleAsync<TJob>(object? payload, CancellationToken ct)
            where TJob : IBackgroundJob
        {
            Unscheduled.Add(payload);

            return Task.FromResult(true);
        }

        // Решта порту цим обробникам не потрібна: мовчазна заглушка сховала б
        // виклик, якого тут бути не має.
        public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct, int? createdByUserId = null)
            where TJob : IBackgroundJob => throw new NotSupportedException();

        public Task<string> EnqueueExclusiveAsync<TJob>(
            string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
            where TJob : IBackgroundJob => throw new NotSupportedException();

        public Task CancelAsync(string jobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> RestartAsync(string jobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<int?> GetCreatedByUserIdAsync(string jobId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<JobSummary>> ListRecentAsync(
            JobListFilter filter, int limit, CancellationToken ct) => throw new NotSupportedException();
    }
}
