// tests/Ecr.Application.Tests/Integration/CollectionScheduleHandlersTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
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
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public CollectionScheduleHandlersTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Actor);
        Allow("Integration.EditSchedule");

        // Підробка UoW виконує замикання транзакції, інакше зміна й журнал (ФВ-12.10) не запустилися б узагалі.
        _store.InTransaction = () => _inTransaction;
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => RunInTransactionAsync(call.Arg<Func<CancellationToken, Task>>(), call.Arg<CancellationToken>()));
    }

    private bool _inTransaction;

    private async Task RunInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        _inTransaction = true;

        try
        {
            await operation(ct);
        }
        finally
        {
            _inTransaction = false;
        }
    }

    /// <summary>Підміна відмови зовнішнього ключа (SQL 547), яку фейк-сховище розпізнає.</summary>
    private sealed class ForeignKeyFailure() : Exception("FK_CS_DependsOn");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S-D1")]
    public async Task Паралельні_A_B_і_B_A_цикл_ловиться_повторною_перевіркою_під_замком_джерела()
    {
        var a = Add(Hourly);
        var b = Add(Hourly);

        // Рання перевірка `PUT B→A` проходить (A ще нічого не залежить). Поки B чекає замок, паралельний
        // запит комітить `A→B`: повтор під замком мусить побачити цикл.
        // ⚠ МУТАЦІЇ: прибрати `LockDependenciesAsync` → Locks порожній; прибрати повторний `RequireValidAsync`
        // у SaveCollectionScheduleHandler → виняток зникає, і ліворуч лишається цикл A→B→A.
        _store.OnLock = () => a.SetDependency(b.Id);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(
                b.Id, Hourly, isEnabled: true, lookbackDays: null, new ScheduleDependencyChange(a.Id), Version(b),
                CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.collectionScheduleDependencyCycle", refused.Details!["messageKey"]);
        Assert.Equal([(3, true)], _store.Locks);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S-D2")]
    public async Task Порушення_FK_при_видаленні_чи_правці_залежності_це_409_а_не_500()
    {
        // ⚠ МУТАЦІЯ: прибрати `RunMappingConflictAsync` у Delete/Save/Create → голий виняток (500) замість 409.
        var a = Add(Hourly);
        var b = Add(Hourly);
        _uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<int>(new ForeignKeyFailure()));

        var onUpdate = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Save().HandleAsync(
                b.Id, Hourly, isEnabled: true, lookbackDays: null, new ScheduleDependencyChange(a.Id), Version(b),
                CancellationToken.None));
        var onDelete = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Delete().HandleAsync(a.Id, Version(a), CancellationToken.None));

        Assert.Equal("err.ECR-JOB-0409.collectionScheduleChanged", onDelete.Details!["messageKey"]);
        Assert.Equal("err.ECR-JOB-0409.collectionScheduleChanged", onUpdate.Details!["messageKey"]);

        // Сторонній виняток не маскується під конфлікт.
        _uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<int>(new InvalidOperationException("db")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Delete().HandleAsync(b.Id, Version(b), CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S-D2")]
    public async Task Видалення_розкладу_бере_замок_у_транзакції_і_пише_аудит_кожному_залежному_якому_знято_залежність()
    {
        // ⚠ МУТАЦІЯ: прибрати цикл запису аудиту залежних → Received(3) стає Received(1).
        var a = Add(Hourly);
        var b = Add(Hourly);
        var c = Add(Hourly);
        b.SetDependency(a.Id);
        c.SetDependency(a.Id);

        await Delete().HandleAsync(a.Id, Version(a), CancellationToken.None);

        Assert.Equal([(3, true)], _store.Locks);
        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityId == a.Id && r.Operation == DeleteCollectionScheduleHandler.AuditOperation),
            Arg.Any<CancellationToken>());

        foreach (var dependent in new[] { b, c })
        {
            await _audit.Received(1).WriteStructureChangeAsync(
                Arg.Is<StructureChangeRecord>(r =>
                    r.EntityId == dependent.Id && r.Operation == SaveCollectionScheduleHandler.AuditOperation
                    && r.OldJson!.Contains($"\"dependsOnScheduleId\":{a.Id}", StringComparison.Ordinal)
                    && r.NewJson!.Contains("\"dependsOnScheduleId\":null", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
        }

        Assert.Null(b.DependsOnScheduleId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.10")]
    public async Task ФВ_12_10_зміна_пише_старий_і_новий_розклад_а_відмова_cron_журналу_не_пише()
    {
        var schedule = Add(Hourly);

        await Save().HandleAsync(schedule.Id, Nightly, isEnabled: false, lookbackDays: 5, Version(schedule), CancellationToken.None);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "ext.CollectionSchedule" && r.EntityId == schedule.Id
                && r.Operation == SaveCollectionScheduleHandler.AuditOperation
                && r.OldJson!.Contains(Hourly, StringComparison.Ordinal) && r.OldJson.Contains("\"isEnabled\":true", StringComparison.Ordinal)
                && r.NewJson!.Contains(Nightly, StringComparison.Ordinal) && r.NewJson.Contains("\"isEnabled\":false", StringComparison.Ordinal)
                && r.NewJson.Contains("\"lookbackDays\":5", StringComparison.Ordinal)
                && r.ChangedByUserId == Actor && r.ChangedAt == Now),
            Arg.Any<CancellationToken>());

        _audit.ClearReceivedCalls();
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(schedule.Id, Unsupported, isEnabled: true, lookbackDays: null, Version(schedule), CancellationToken.None));
        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21b")]
    public async Task Невалідний_cron_відхиляється_ДО_бази_і_розклад_лишається_попереднім()
    {
        var schedule = Add(Hourly);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(schedule.Id, Unsupported, isEnabled: true, lookbackDays: null, Version(schedule), CancellationToken.None));

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
            () => Save().HandleAsync(schedule.Id, tooLong, isEnabled: true, lookbackDays: null, Version(schedule), CancellationToken.None));
        var empty = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(schedule.Id, "   ", isEnabled: true, lookbackDays: null, Version(schedule), CancellationToken.None));

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
            () => Save().HandleAsync(schedule.Id, Nightly, isEnabled: true, lookbackDays: null, "AAAAAAAAAAE=", CancellationToken.None));
        var headerless = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(schedule.Id, Nightly, isEnabled: true, lookbackDays: null, ifMatch: null, CancellationToken.None));

        Assert.Equal("ECR-JOB-0409", stale.ErrorCode);
        Assert.Equal("err.ECR-JOB-0409.collectionScheduleChanged", stale.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.collectionScheduleIfMatch", headerless.Details!["messageKey"]);
        Assert.Equal(Hourly, schedule.CronExpression);

        // Своя версія проходить і в лапках, і без них — обидві форми ETag живі.
        var saved = await Save().HandleAsync(
            schedule.Id, Nightly, isEnabled: true, lookbackDays: null, $"\"{Version(schedule)}\"", CancellationToken.None);

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
            schedule.Id, Nightly, isEnabled: true, lookbackDays: null, Version(schedule), CancellationToken.None);

        Assert.Equal((Nightly, true, (string?)null), (enabled.Cron, enabled.IsEnabled, enabled.LastError));
        Assert.Equal("ENT-1", enabled.SourceEntityCode);

        var placed = _scheduler.Scheduled.Single();
        Assert.Equal(Nightly, placed.Cron);
        Assert.Equal(CollectionScheduleApplier.PayloadOf(schedule.SourceEntityId), placed.Payload);

        var disabled = await Save().HandleAsync(
            schedule.Id, Nightly, isEnabled: false, lookbackDays: null, Version(schedule), CancellationToken.None);

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
            () => Save().HandleAsync(schedule.Id, Nightly, isEnabled: true, lookbackDays: null, Version(schedule), CancellationToken.None));

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
    [Trait("Requirement", "ФВ-13.11")]
    public async Task Створення_заводить_розклад_ставить_його_в_планувальник_а_другий_на_ту_саму_сутність_дає_409()
    {
        _store.Entities[77] = ("ENT-77", "Entity 77");

        var created = await Create().HandleAsync(77, Nightly, isEnabled: true, lookbackDays: null, CancellationToken.None);

        Assert.Equal((77, Nightly, true), (created.SourceEntityId, created.Cron, created.IsEnabled));
        Assert.Equal(("ENT-77", "Entity 77"), (created.SourceEntityCode, created.SourceEntityName));

        var placed = _scheduler.Scheduled.Single();
        Assert.Equal(Nightly, placed.Cron);
        Assert.Equal(CollectionScheduleApplier.PayloadOf(77), placed.Payload);

        // ⛔ Головне твердження. Другий розклад на ту саму сутність — це другий
        // тригер планувальника з ТИМ САМИМ завданням, тобто подвійний збір,
        // якого не видно ніде, крім кількості прогонів.
        var duplicate = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Create().HandleAsync(77, Hourly, isEnabled: true, lookbackDays: null, CancellationToken.None));

        Assert.Equal("ECR-JOB-0409", duplicate.ErrorCode);
        Assert.Equal("err.ECR-JOB-0409.collectionScheduleExists", duplicate.Details!["messageKey"]);
        Assert.Single(_store.Rows);
        Assert.Single(_scheduler.Scheduled);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.15")]
    [Trait("Requirement", "ФВ-12.8")]
    public async Task Дві_сутності_отримують_два_незалежні_тригери_кожна_зі_своєю_частотою()
    {
        // ⛔ Розклад — на сутність, не один на систему: частота опитування
        // «природна для класу даних» (ФВ-12.8), тож концентрація й добовий
        // обсяг мають різні cron, і кожен тригер збирає лише СВОЮ сутність.
        _store.Entities[77] = ("STACK-77", null);
        _store.Entities[78] = ("FLOW-78", null);

        await Create().HandleAsync(77, Hourly, isEnabled: true, lookbackDays: null, CancellationToken.None);
        await Create().HandleAsync(78, Nightly, isEnabled: true, lookbackDays: null, CancellationToken.None);

        // МУТАЦІЙНИЙ ДОКАЗ: у `CollectionScheduleApplier.ApplyAsync` ставити
        // один сталий cron замість `schedule.CronExpression` (одна частота на
        // систему) → твердження червоніє на другому тригері.
        Assert.Equal(
            new (string Cron, object? Payload)[]
            {
                (Hourly, CollectionScheduleApplier.PayloadOf(77)),
                (Nightly, CollectionScheduleApplier.PayloadOf(78)),
            },
            _scheduler.Scheduled);
        Assert.NotEqual(CollectionScheduleApplier.PayloadOf(77), CollectionScheduleApplier.PayloadOf(78));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21c")]
    public async Task Створення_з_невалідним_cron_або_для_неіснуючої_сутності_не_доходить_до_бази()
    {
        _store.Entities[77] = ("ENT-77", null);

        var badCron = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(77, Unsupported, isEnabled: true, lookbackDays: null, CancellationToken.None));
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Create().HandleAsync(999, Nightly, isEnabled: true, lookbackDays: null, CancellationToken.None));

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
            () => Create().HandleAsync(77, Nightly, isEnabled: true, lookbackDays: null, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().HandleAsync(schedule.Id, Nightly, isEnabled: false, lookbackDays: null, Version(schedule), CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Delete().HandleAsync(schedule.Id, Version(schedule), CancellationToken.None));

        Assert.Equal(Hourly, schedule.CronExpression);
        Assert.Single(_store.Rows);
        Assert.Empty(_store.Removed);
        Assert.Empty(_scheduler.Unscheduled);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task ФВ_13_15_вікно_збору_зберігається_правкою_і_створенням_а_без_поля_лишається_наявним()
    {
        var schedule = Add(Hourly);
        Assert.Equal(7, schedule.LookbackDays);

        var saved = await Save().HandleAsync(
            schedule.Id, Hourly, isEnabled: true, lookbackDays: 30, Version(schedule), CancellationToken.None);

        Assert.Equal(30, schedule.LookbackDays);
        Assert.Equal(30, saved.LookbackDays);

        // Поле не прийшло (старий клієнт) — вікно не скидається на типове.
        var untouched = await Save().HandleAsync(
            schedule.Id, Nightly, isEnabled: true, lookbackDays: null, Version(schedule), CancellationToken.None);

        Assert.Equal(30, untouched.LookbackDays);

        _store.Entities[77] = ("ENT-77", null);
        var created = await Create().HandleAsync(77, Nightly, isEnabled: true, lookbackDays: 366, CancellationToken.None);

        Assert.Equal(366, created.LookbackDays);
        Assert.Equal(366, _store.Rows.Single(r => r.Schedule.SourceEntityId == 77).Schedule.LookbackDays);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task ФВ_13_15_вікно_поза_1_366_днів_відхиляється_422_ДО_бази()
    {
        var schedule = Add(Hourly);
        _store.Entities[77] = ("ENT-77", null);

        // ⚠ Межі ЛІТЕРАЛАМИ, а не від констант домену: твердження проти
        // константи поїхало б разом із нею.
        foreach (var bad in new[] { 0, -1, 367 })
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Save().HandleAsync(
                    schedule.Id, Hourly, isEnabled: true, lookbackDays: bad, Version(schedule), CancellationToken.None));

            Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
            Assert.Equal("err.ECR-REQ-0422.collectionScheduleLookback", refused.Details!["messageKey"]);
            Assert.Equal(("1", "366"), (refused.Details["min"], refused.Details["max"]));

            var refusedCreate = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Create().HandleAsync(77, Hourly, isEnabled: true, lookbackDays: bad, CancellationToken.None));

            Assert.Equal("err.ECR-REQ-0422.collectionScheduleLookback", refusedCreate.Details!["messageKey"]);
        }

        Assert.Equal(7, schedule.LookbackDays);
        Assert.Single(_store.Rows);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        // Самі межі проходять.
        var lowest = await Save().HandleAsync(
            schedule.Id, Hourly, isEnabled: true, lookbackDays: 1, Version(schedule), CancellationToken.None);

        Assert.Equal(1, lowest.LookbackDays);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.8")]
    public async Task ФВ_12_8_розклад_для_власної_форми_не_створюється_навіть_вимкненим()
    {
        _store.Entities[77] = ("OWN-77", null);
        _store.LocalEntities.Add(77);

        foreach (var enabled in new[] { true, false })
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Create().HandleAsync(77, Nightly, enabled, lookbackDays: null, CancellationToken.None));

            Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
            Assert.Equal("err.ECR-REQ-0422.scheduleForLocalEntity", refused.Details!["messageKey"]);
            Assert.Equal("OWN-77", refused.Details["code"]);
        }

        Assert.Empty(_store.Rows);
        Assert.Empty(_scheduler.Scheduled);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.8")]
    public async Task ФВ_12_8_наявний_розклад_власної_форми_не_вмикається_але_вимикається()
    {
        var schedule = Add(Hourly, RegistrySourceKind.Local);
        schedule.Disable();

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(
                schedule.Id, Nightly, isEnabled: true, lookbackDays: null, Version(schedule), CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.scheduleForLocalEntity", refused.Details!["messageKey"]);
        Assert.False(schedule.IsEnabled);
        Assert.Equal(Hourly, schedule.CronExpression);
        Assert.Empty(_scheduler.Scheduled);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        // ⚠ Вимкнути (і так прибрати зайвий тригер) — можна: заведений до
        // правила розклад не має ставати незнімним.
        var disabled = await Save().HandleAsync(
            schedule.Id, Nightly, isEnabled: false, lookbackDays: null, Version(schedule), CancellationToken.None);

        Assert.False(disabled.IsEnabled);
        Assert.Equal(CollectionScheduleApplier.PayloadOf(schedule.SourceEntityId), _scheduler.Unscheduled.Single());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task ФВ_13_15_залежність_ставиться_створенням_і_правкою_знімається_ClearDependency_а_без_поля_лишається()
    {
        var first = Add(Hourly);
        var second = Add(Nightly);

        var saved = await Save().HandleAsync(
            second.Id, Nightly, isEnabled: true, lookbackDays: null,
            new ScheduleDependencyChange(first.Id), Version(second), CancellationToken.None);

        Assert.Equal(first.Id, saved.DependsOnScheduleId);
        Assert.Equal(first.Id, second.DependsOnScheduleId);

        // Запит без поля (старий клієнт, старий підпис) залежності не скидає.
        await Save().HandleAsync(second.Id, Hourly, isEnabled: true, lookbackDays: null, Version(second), CancellationToken.None);
        Assert.Equal(first.Id, second.DependsOnScheduleId);

        var cleared = await Save().HandleAsync(
            second.Id, Hourly, isEnabled: true, lookbackDays: null,
            new ScheduleDependencyChange(null, Clear: true), Version(second), CancellationToken.None);

        Assert.Null(cleared.DependsOnScheduleId);
        Assert.Null(second.DependsOnScheduleId);

        _store.Entities[77] = ("ENT-77", null);
        var created = await Create().HandleAsync(77, Nightly, isEnabled: true, lookbackDays: null, first.Id, CancellationToken.None);

        Assert.Equal(first.Id, created.DependsOnScheduleId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task ФВ_13_15_залежність_від_неіснуючого_розкладу_чи_іншого_з_єднання_відхиляється_422_ДО_бази()
    {
        var own = Add(Hourly);
        var foreign = Add(Nightly, dataSourceId: 4);

        var missing = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(
                own.Id, Hourly, isEnabled: true, lookbackDays: null,
                new ScheduleDependencyChange(999), Version(own), CancellationToken.None));
        var otherSource = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(
                own.Id, Hourly, isEnabled: true, lookbackDays: null,
                new ScheduleDependencyChange(foreign.Id), Version(own), CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", missing.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.collectionScheduleDependencyNotFound", missing.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.collectionScheduleDependencyOtherSource", otherSource.Details!["messageKey"]);

        // Нове створення з чужого з'єднання — теж відмова; нічого не збережено.
        _store.Entities[77] = ("ENT-77", null);
        var createRefused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(77, Nightly, isEnabled: true, lookbackDays: null, foreign.Id, CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.collectionScheduleDependencyOtherSource", createRefused.Details!["messageKey"]);
        Assert.Null(own.DependsOnScheduleId);
        Assert.Equal(2, _store.Rows.Count);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task ФВ_13_15_цикл_залежностей_на_себе_прямий_і_через_ланцюг_відхиляється_422_ДО_бази()
    {
        var a = Add(Hourly);
        var b = Add(Hourly);
        var c = Add(Hourly);

        // Ланцюг a → b → c (a залежить від b, b від c).
        a.SetDependency(b.Id);
        b.SetDependency(c.Id);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: прибрати обхід ланцюга в `CollectionScheduleDependencyRules`
        // (лишити лише `selfId == dependsOnId`) — червоніє «c → a», хоча сама пряма й проста
        // «a → b → a» проходять.
        foreach (var (self, target) in new[] { (c, a), (b, a), (a, a), (c, b) })
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Save().HandleAsync(
                    self.Id, Hourly, isEnabled: true, lookbackDays: null,
                    new ScheduleDependencyChange(target.Id), Version(self), CancellationToken.None));

            Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
            Assert.Equal("err.ECR-REQ-0422.collectionScheduleDependencyCycle", refused.Details!["messageKey"]);
        }

        Assert.Null(c.DependsOnScheduleId);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        // Не цикл: c → d (поза ланцюгом) проходить.
        var d = Add(Hourly);
        var ok = await Save().HandleAsync(
            c.Id, Hourly, isEnabled: true, lookbackDays: null,
            new ScheduleDependencyChange(d.Id), Version(c), CancellationToken.None);

        Assert.Equal(d.Id, ok.DependsOnScheduleId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task ФВ_13_15_видалення_розкладу_знімає_залежність_у_залежних()
    {
        var first = Add(Hourly);
        var second = Add(Nightly);
        second.SetDependency(first.Id);

        await Delete().HandleAsync(first.Id, Version(first), CancellationToken.None);

        // ⛔ FK без каскаду: без зняття видалення впало б на ключі в базі.
        Assert.Null(second.DependsOnScheduleId);
        Assert.Same(first, _store.Removed.Single());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.15")]
    public void ФВ_13_15_payload_планового_збору_не_містить_ознаки_ручного_а_ручний_містить()
    {
        // ⛔ Payload планового збору — ключ тригера Quartz: зайве поле розійшлося б зі збереженими
        // тригерами, і після оновлення розклад ставився б удруге.
        var scheduled = System.Text.Json.JsonSerializer.Serialize(CollectionScheduleApplier.PayloadOf(77));
        var manual = System.Text.Json.JsonSerializer.Serialize(new CollectionTask(77, null, null, Manual: true));

        Assert.DoesNotContain("anual", scheduled, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Manual\":true", manual, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task ФВ_13_15_ланцюг_із_50_предків_приймається_51_вважається_циклом_а_зламане_кільце_не_зависає()
    {
        // Ланцюг t0 → t1 → … → t51: у t0 51 предок, у t1 — 50.
        var chain = Enumerable.Range(0, 52).Select(_ => Add(Hourly)).ToList();
        for (var i = 0; i < chain.Count - 1; i++)
        {
            chain[i].SetDependency(chain[i + 1].Id);
        }

        var self = Add(Hourly);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ (CollectionScheduleDependencyRules.MaxChainDepth): `++visited > Max` → `>=` чи `<`
        // відхилив би законний ланцюг із рівно 50 предків; `--visited` пропустив би 51.
        var ok = await Save().HandleAsync(
            self.Id, Hourly, isEnabled: true, lookbackDays: null,
            new ScheduleDependencyChange(chain[1].Id), Version(self), CancellationToken.None);
        Assert.Equal(chain[1].Id, ok.DependsOnScheduleId);

        var tooDeep = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(
                self.Id, Hourly, isEnabled: true, lookbackDays: null,
                new ScheduleDependencyChange(chain[0].Id), Version(self), CancellationToken.None));
        Assert.Equal("err.ECR-REQ-0422.collectionScheduleDependencyCycle", tooDeep.Details!["messageKey"]);
        Assert.Equal(chain[0].Id.ToString(System.Globalization.CultureInfo.InvariantCulture), tooDeep.Details["dependsOn"]);

        // Зламані дані: кільце x ↔ y, що не проходить через розклад, який правиться. Без межі глибини
        // обхід крутився б вічно; з нею — чесна 422, а не завислий запит.
        var x = Add(Hourly);
        var y = Add(Hourly);
        x.SetDependency(y.Id);
        y.SetDependency(x.Id);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(
                self.Id, Hourly, isEnabled: true, lookbackDays: null,
                new ScheduleDependencyChange(x.Id), Version(self), CancellationToken.None));
        Assert.Equal("err.ECR-REQ-0422.collectionScheduleDependencyCycle", refused.Details!["messageKey"]);
    }

    private void Allow(string permission)
        => _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(permission).Build());

    private CreateCollectionScheduleHandler Create()
        => new(_store, _scheduler, new CollectionScheduleApplier(_scheduler), _access, _uow, _user, _clock, _audit);

    private SaveCollectionScheduleHandler Save()
        => new(_store, _scheduler, new CollectionScheduleApplier(_scheduler), _access, _uow, _user, _clock, _audit);

    private DeleteCollectionScheduleHandler Delete() => new(_store, _scheduler, _access, _uow, _user, _clock, _audit);

    private static string Version(CollectionSchedule schedule) => Convert.ToBase64String(schedule.RowVersion);

    /// <summary>Розклад із присвоєним ключем і версією рядка, як його віддала б база.</summary>
    private CollectionSchedule Add(string cron, RegistrySourceKind kind = RegistrySourceKind.External, int dataSourceId = 3)
    {
        var id = _store.Rows.Count + 1;
        var schedule = new CollectionSchedule(sourceEntityId: 40 + id, cron);

        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(schedule, id);
        typeof(CollectionSchedule).GetProperty(nameof(CollectionSchedule.RowVersion))!
            .SetValue(schedule, new byte[] { 0, 0, 0, 0, 0, 0, 7, (byte)id });

        _store.Rows.Add(new ScheduledSourceEntity(schedule, $"ENT-{id}", $"Entity {id}", dataSourceId, $"SRC-{dataSourceId}", kind));

        return schedule;
    }

    private sealed class FakeStore : ICollectionScheduleStore
    {
        public List<ScheduledSourceEntity> Rows { get; } = [];

        public List<CollectionSchedule> Removed { get; } = [];

        /// <summary>Сутності джерела, які «є в базі»: ключ → код і підпис.</summary>
        public Dictionary<int, (string Code, string? Name)> Entities { get; } = [];

        /// <summary>Сутності-власні форми (ФВ-12.8); решта — <see cref="RegistrySourceKind.External"/>.</summary>
        public HashSet<int> LocalEntities { get; } = [];

        public RegistrySourceKind KindOf(int sourceEntityId)
            => LocalEntities.Contains(sourceEntityId) ? RegistrySourceKind.Local : RegistrySourceKind.External;

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
                        "SRC-3",
                        KindOf(sourceEntityId))
                    : null);

        /// <summary>Узяті замки залежностей: з'єднання і чи було це всередині транзакції.</summary>
        public List<(int DataSourceId, bool InTransaction)> Locks { get; } = [];

        /// <summary>Чи виконується зараз замикання транзакції (виставляє тест).</summary>
        public Func<bool> InTransaction { get; set; } = () => false;

        /// <summary>Дія в момент взяття замка — «паралельний запит коміттить свою зміну».</summary>
        public Action? OnLock { get; set; }

        public Task LockDependenciesAsync(int dataSourceId, CancellationToken ct)
        {
            Locks.Add((dataSourceId, InTransaction()));
            OnLock?.Invoke();

            return Task.CompletedTask;
        }

        public Task<int?> ReadDependsOnAsync(int collectionScheduleId, CancellationToken ct)
            => Task.FromResult(Rows.Find(r => r.Schedule.Id == collectionScheduleId)?.Schedule.DependsOnScheduleId);

        public bool IsForeignKeyViolation(Exception failure) => failure is ForeignKeyFailure;

        public Task<IReadOnlyList<CollectionSchedule>> FindDependentsAsync(int collectionScheduleId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CollectionSchedule>>(
                [.. Rows.Where(r => r.Schedule.DependsOnScheduleId == collectionScheduleId).Select(r => r.Schedule)]);

        /// <summary>Ключ присвоюється одразу — базу тут заміняє цей список.</summary>
        public void Add(CollectionSchedule schedule)
        {
            typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!
                .SetValue(schedule, Rows.Count + 1);

            var entity = Entities[schedule.SourceEntityId];
            Rows.Add(new ScheduledSourceEntity(
                schedule, entity.Code, entity.Name, 3, "SRC-3", KindOf(schedule.SourceEntityId)));
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

        public Task<string> EnqueueCoalescedAsync<TJob>(
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
