// tests/Ecr.Application.Tests/Periods/ReopenPeriodTransactionTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Periods;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// <c>DAT-06</c>: адміністративне відкриття періоду — стан і запис в аудит
/// одним комітом.
/// </summary>
/// <remarks>
/// ⛔ Це ТРЕТІЙ випадок того самого дефекту. Перші два —
/// <c>ReopenDocumentHandler</c> (див. <c>ReopenRaceTests</c>) і
/// <c>PeriodStateJob</c> (<c>PeriodStateJobTransactionTests</c>) — уже
/// виправлені, і цей файл свідомо повторює їхню форму доказу, а не вигадує
/// свою: якщо доказ на всі три випадки виглядає однаково, наступний такий
/// випадок видно одразу.
///
/// ⚠ Тести інтеграційні за побудовою. Підробка <c>IUnitOfWork</c> перевіряла б
/// підробку: предмет тут — чи існує РЕАЛЬНА транзакція СУБД у момент
/// блокування, і чи відкочується РЕАЛЬНИЙ <c>INSERT</c> аудиту.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReopenPeriodTransactionTests(SqlServerFixture sql)
{
    private const int PeriodKeyValue = 202601;
    private const int UserId = 9;
    private static readonly DateTime Now = new(2026, 4, 1, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.10a")]
    public async Task Період_береться_під_UPDLOCK_усередині_відкритої_транзакції()
    {
        // ⛔ `ReopenPeriodHandler` кликав `IPeriodStore.LockAsync` поза будь-якою
        // явною транзакцією. `UPDLOCK` поза транзакцією звільняється щойно
        // завершується сам `SELECT` — задовго до `period.Reopen(...)` і до
        // `SaveChangesAsync`. Коментар у порті обіцяв «до кінця транзакції»,
        // а транзакції не було, тож замок не тримав нічого і `PeriodStateJob`
        // міг закрити період посеред операції.
        //
        // ⚠ Доводиться саме МОМЕНТОМ виклику: декоратор фіксує, чи була
        // транзакція відкрита ТОДІ, коли рядок брали під блокування. Перевірити
        // це «за наслідком» неможливо — успішний прогін виглядає однаково і з
        // замком, і без нього.
        var arranged = await ArrangeAsync(PeriodState.Closed).ConfigureAwait(true);

        await using var db = CreateContext();
        var spy = new TransactionWatchingPeriodStore(new PeriodStore(db), db);

        var handler = Handler(spy, db, new UnitOfWork(db), arranged.ProjectId);

        await handler
            .HandleAsync(arranged.PeriodId, "уточнення за скаргою", Now.AddDays(3), CancellationToken.None)
            .ConfigureAwait(true);

        Assert.True(spy.Called, "LockAsync не викликався — тест нічого не довів.");
        Assert.True(
            spy.TransactionWasOpen,
            "LockAsync викликано поза транзакцією: UPDLOCK звільняється одразу після SELECT.");

        // Контроль: операція справді відбулася, а не «не дійшла до блокування».
        await using var check = CreateContext();
        Assert.Equal(
            PeriodState.Grace,
            await check.Periods.Where(p => p.Id == arranged.PeriodId).Select(p => p.State)
                       .FirstAsync().ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.10a")]
    public async Task Збій_збереження_не_лишає_в_аудиті_запису_про_відкриття()
    {
        // ⛔ Друга половина `DAT-06`, і саме вона коштує дорожче за незатриманий
        // замок. `AuditWriter` пише сирим `INSERT` по тому самому підключенню і
        // БЕЗ транзакції комітить його одразу (`AuditWriter.CreateCommand`). До
        // виправлення послідовність була така: аудит закомічено → і лише потім
        // `uow.SaveChangesAsync`. Будь-який збій на другому кроці лишав у
        // `aud.StructureChange` запис «період відкрито» про подію, якої не
        // сталося: період у базі — `Closed`, у журналі — `Reopen`.
        //
        // ⚠ Збій вноситься в `SaveChangesAsync` — тобто РІВНО між двома
        // записами, які мусять бути одним комітом. Підміняти сам `AuditWriter`
        // не можна: перевірявся б підставний журнал, а не справжній `INSERT`.
        var arranged = await ArrangeAsync(PeriodState.Closed).ConfigureAwait(true);

        await using var db = CreateContext();
        var handler = Handler(new PeriodStore(db), db, new FailingUnitOfWork(new UnitOfWork(db)), arranged.ProjectId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(
                arranged.PeriodId, "уточнення за скаргою", Now.AddDays(3), CancellationToken.None))
            .ConfigureAwait(true);

        Assert.Equal(FailingUnitOfWork.Marker, error.Message);

        // ⛔ Журналу немає. Це твердження і падає, якщо прибрати обгортку
        // `ExecuteInTransactionAsync` у `ReopenPeriodHandler`.
        Assert.Equal(0, await AuditRowsAsync(arranged.PeriodId).ConfigureAwait(true));

        // ⚠ І стан не змінився — інакше «нуль записів в аудиті» означав би лише
        // те, що до аудиту не дійшло, а не те, що відкотилося все разом.
        await using var check = CreateContext();
        Assert.Equal(
            PeriodState.Closed,
            await check.Periods.Where(p => p.Id == arranged.PeriodId).Select(p => p.State)
                       .FirstAsync().ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.10a")]
    public async Task Успішне_відкриття_лишає_рівно_один_запис_в_аудиті()
    {
        // ⚠ Контроль до тесту вище: без нього «нуль записів при збої» був би
        // зеленим і на системі, яка не пише аудит ВЗАГАЛІ.
        var arranged = await ArrangeAsync(PeriodState.Closed).ConfigureAwait(true);

        await using var db = CreateContext();
        var handler = Handler(new PeriodStore(db), db, new UnitOfWork(db), arranged.ProjectId);

        await handler
            .HandleAsync(arranged.PeriodId, "уточнення за скаргою", Now.AddDays(3), CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(1, await AuditRowsAsync(arranged.PeriodId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Без_гранта_Manage_на_проєкт_період_не_відкривається_і_журнал_чистий()
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ. Прибрати перевірку `LevelFor(Project) < Manage` у
        // `ReopenPeriodHandler` — і червоним стає рівно цей тест: власник права
        // `Period.Reopen` з грантом лише Write відкриває період чужого проєкту.
        // ⚠ Саме Write, а не «жодного гранта»: так видно, що межа — Manage.
        var arranged = await ArrangeAsync(PeriodState.Closed).ConfigureAwait(true);

        await using var db = CreateContext();
        var handler = Handler(new PeriodStore(db), db, new UnitOfWork(db), arranged.ProjectId, GrantLevel.Write);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(
                arranged.PeriodId, "уточнення за скаргою", Now.AddDays(3), CancellationToken.None))
            .ConfigureAwait(true);

        Assert.Equal("err.ECR-AUTH-0403.noProjectManageGrant", error.Details!["messageKey"]);
        Assert.Equal(0, await AuditRowsAsync(arranged.PeriodId).ConfigureAwait(true));

        await using var check = CreateContext();
        Assert.Equal(
            PeriodState.Closed,
            await check.Periods.Where(p => p.Id == arranged.PeriodId).Select(p => p.State)
                       .FirstAsync().ConfigureAwait(true));
    }

    private ReopenPeriodHandler Handler(
        IPeriodStore periods, EcrDbContext db, IUnitOfWork uow, int projectId,
        GrantLevel projectGrant = GrantLevel.Manage)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = UserId }
                  .Permission(ReopenPeriodHandler.Permission)
                  .Grant(ResourceKind.Project, projectId, projectGrant)
                  .Build());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        return new ReopenPeriodHandler(
            periods, access, uow, new AuditWriter(db), user, new TestClock(Now));
    }

    /// <summary>Скільки записів «Reopen» про цей період лежить у журналі.</summary>
    private async Task<int> AuditRowsAsync(int periodId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM aud.StructureChange "
            + "WHERE EntityType = N'Period' AND EntityId = @id AND Operation = N'Reopen';";
        command.Parameters.AddWithValue("@id", periodId);

        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    /// <summary>Проєкт із періодом у заданому стані.</summary>
    private async Task<Arranged> ArrangeAsync(PeriodState state)
    {
        await using var db = CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10];

        var template = new Template(
            EcrCode.Create($"T{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Reopen period" }),
            createdByUserId: 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, "1.0.0.0", createdByUserId: 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var project = new Project(
            EcrCode.Create($"P{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Reopen period" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");
        db.Projects.Add(project);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var period = new Period(
            project.Id, new PeriodKey(PeriodKeyValue), 1,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.RecomputeBoundaries(ProjectBuilder.Policy(), ProjectBuilder.Zone());
        period.AdvanceTo(state, Now);

        db.Periods.Add(period);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Arranged(project.Id, period.Id);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(30))
            .Options);

    private sealed record Arranged(int ProjectId, int PeriodId);

    /// <summary>
    /// Сховище-декоратор, що фіксує, чи була транзакція відкрита в момент
    /// блокування періоду.
    /// </summary>
    private sealed class TransactionWatchingPeriodStore(IPeriodStore inner, EcrDbContext db) : IPeriodStore
    {
        /// <summary>Чи викликали блокування взагалі.</summary>
        public bool Called { get; private set; }

        /// <summary>Чи була транзакція відкрита саме в цей момент.</summary>
        public bool TransactionWasOpen { get; private set; }

        public Task<Period?> LockAsync(int periodId, CancellationToken ct)
        {
            Called = true;
            TransactionWasOpen = db.Database.CurrentTransaction is not null;
            return inner.LockAsync(periodId, ct);
        }

        public Task<Project?> FindProjectAsync(int projectId, CancellationToken ct)
            => inner.FindProjectAsync(projectId, ct);

        public Task<PeriodPolicy> GetPolicyAsync(int periodPolicyId, CancellationToken ct)
            => inner.GetPolicyAsync(periodPolicyId, ct);

        public Task<IReadOnlyList<PeriodPolicy>> ListPoliciesAsync(CancellationToken ct)
            => inner.ListPoliciesAsync(ct);

        public void AddPolicy(PeriodPolicy policy) => inner.AddPolicy(policy);

        public void AddRange(IEnumerable<Period> periods) => inner.AddRange(periods);

        public Task AddProjectAsync(Project project, CancellationToken ct)
            => inner.AddProjectAsync(project, ct);

        public Task<IReadOnlyList<PeriodStateRef>> GetPeriodStatesAsync(
            int projectId, int? periodKey, CancellationToken ct)
            => inner.GetPeriodStatesAsync(projectId, periodKey, ct);

        public Task<PeriodBounds?> FindPeriodBoundsAsync(long documentId, int periodKey, CancellationToken ct)
            => inner.FindPeriodBoundsAsync(documentId, periodKey, ct);

        public Task<PeriodState?> FindPeriodStateAsync(long documentId, int periodKey, CancellationToken ct)
            => inner.FindPeriodStateAsync(documentId, periodKey, ct);
    }

    /// <summary>
    /// Справжня транзакція, але <c>SaveChangesAsync</c> завжди падає.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме так відтворюється збій МІЖ двома записами. Транзакцію відкриває
    /// справжній <c>UnitOfWork</c>, тож перевіряється справжній відкат СУБД, а
    /// не поведінка підробки.
    /// </remarks>
    private sealed class FailingUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        /// <summary>Текст, за яким тест упізнає саме внесений збій.</summary>
        public const string Marker = "збій збереження після запису в аудит";

        public Task<int> SaveChangesAsync(CancellationToken ct)
            => throw new InvalidOperationException(Marker);

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct)
            => inner.BeginTransactionAsync(ct);

        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
            => inner.ExecuteInTransactionAsync(operation, ct);
    }
}
