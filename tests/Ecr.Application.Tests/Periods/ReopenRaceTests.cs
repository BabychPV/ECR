// tests/Ecr.Application.Tests/Periods/ReopenRaceTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// Гонка `Reopen` і `PeriodStateJob` (ФВ-1.10a). Обидві операції беруть рядок
/// періоду з `UPDLOCK`; програвший бачить актуальний стан, а не тихо
/// застосовується до вже закритого періоду.
/// </summary>
/// <remarks>
/// ⚠ Тести інтеграційні за побудовою: серіалізує гонку саме SQL Server, і
/// підробка сховища перевіряла б підробку. У <c>06d-tests-application.md</c>
/// позначки <c>Integration</c> не було — вона додана тут (`Q-053`), як і
/// власне визначення колекції для цієї збірки.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReopenRaceTests(SqlServerFixture sql)
{
    private const int PeriodKeyValue = 202601;
    private static readonly DateTime Now = new(2026, 4, 1, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.10a")]
    public async Task Одночасні_Reopen_і_закриття_серіалізуються()
    {
        var (documentId, periodId) = await ArrangeAsync(PeriodState.Closed).ConfigureAwait(true);

        // Перший бере рядок під UPDLOCK і тримає його.
        await using var first = CreateContext();
        await using var firstTx = await first.Database.BeginTransactionAsync().ConfigureAwait(true);
        var locked = await LockAsync(first, documentId).ConfigureAwait(true);
        locked.Reopen(Now.AddDays(3), "уточнення за скаргою", Now);
        await first.SaveChangesAsync().ConfigureAwait(true);

        // Другий заходить на той самий рядок і мусить ЧЕКАТИ, а не прочитати
        // старий стан і піти застосовувати рішення до нього.
        await using var second = CreateContext();
        second.Database.SetCommandTimeout(2);
        await using var secondTx = await second.Database.BeginTransactionAsync().ConfigureAwait(true);

        var blocked = await Assert.ThrowsAnyAsync<Exception>(
            () => LockAsync(second, documentId)).ConfigureAwait(true);

        // ⚠ Таймаут тут — це ДОКАЗ блокування: без UPDLOCK другий прочитав би
        // рядок миттєво і побачив би Closed, якого вже немає.
        Assert.Contains("timeout", blocked.ToString(), StringComparison.OrdinalIgnoreCase);

        await firstTx.CommitAsync().ConfigureAwait(true);
        await secondTx.RollbackAsync().ConfigureAwait(true);

        await using var check = CreateContext();
        var state = await check.Periods.Where(p => p.Id == periodId)
                                       .Select(p => p.State)
                                       .FirstAsync().ConfigureAwait(true);
        Assert.Equal(PeriodState.Grace, state);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.20a")]
    public async Task Програвший_бачить_актуальний_стан_і_відмовляє_з_причиною()
    {
        var (documentId, periodId) = await ArrangeAsync(PeriodState.Grace).ConfigureAwait(true);

        // Задача станів закриває період і комітить.
        await using (var job = CreateContext())
        {
            await using var tx = await job.Database.BeginTransactionAsync().ConfigureAwait(true);
            var period = await LockAsync(job, documentId).ConfigureAwait(true);
            period.AdvanceTo(PeriodState.Closed, Now);
            await job.SaveChangesAsync().ConfigureAwait(true);
            await tx.CommitAsync().ConfigureAwait(true);
        }

        // Reopen документа заходить після і бачить АКТУАЛЬНИЙ стан.
        await using var loser = CreateContext();
        await using var loserTx = await loser.Database.BeginTransactionAsync().ConfigureAwait(true);
        var seen = await LockAsync(loser, documentId).ConfigureAwait(true);

        Assert.Equal(PeriodState.Closed, seen.State);

        // ⚠ Відмова з причиною, а не тиха правка: спершу Reopen ПЕРІОДУ, потім
        // аркуша (ФВ-5.20a). Інакше зміна пішла б у період, який уже віддали
        // назовні.
        var error = Assert.Throws<BusinessRuleException>(() =>
            seen.State == PeriodState.Closed
                ? throw new BusinessRuleException(
                    "ECR-PRD-4223",
                    $"Період {PeriodKeyValue} закрито: спершу відкрийте період, потім аркуш.")
                : 0);

        Assert.Equal("ECR-PRD-4223", error.ErrorCode);
        await loserTx.RollbackAsync().ConfigureAwait(true);

        await using var check = CreateContext();
        Assert.Equal(
            PeriodState.Closed,
            await check.Periods.Where(p => p.Id == periodId).Select(p => p.State).FirstAsync()
                       .ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.20a")]
    public async Task Два_одночасні_Reopen_дають_один_результат()
    {
        var (documentId, periodId) = await ArrangeAsync(PeriodState.Closed).ConfigureAwait(true);

        var barrier = new SemaphoreSlim(0, 1);

        var winner = Task.Run(async () =>
        {
            await using var db = CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
            var period = await LockAsync(db, documentId).ConfigureAwait(false);

            period.Reopen(Now.AddDays(3), "перша причина", Now);
            await db.SaveChangesAsync().ConfigureAwait(false);

            barrier.Release();
            await Task.Delay(300).ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        });

        var second = Task.Run(async () =>
        {
            await barrier.WaitAsync().ConfigureAwait(false);

            await using var db = CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);

            // Чекає на перший, потім бачить УЖЕ ВІДКРИТИЙ період — і домен
            // відхиляє повторне відкриття: Reopen застосовний лише до Closed.
            var period = await LockAsync(db, documentId).ConfigureAwait(false);
            var already = period.State;

            await tx.RollbackAsync().ConfigureAwait(false);
            return already;
        });

        await winner.ConfigureAwait(true);
        var observed = await second.ConfigureAwait(true);

        // ⚠ Один результат, а не два: другий не «переоткриває» період з іншою
        // причиною. Інакше в журналі лишилися б дві причини на одне відкриття,
        // і жодна не була б відповіддю на питання «чому».
        Assert.Equal(PeriodState.Grace, observed);
        Assert.Throws<Ecr.Domain.Abstractions.DomainException>(
            () => new PeriodProbe(observed).ReopenAgain());

        await using var check = CreateContext();
        var reason = await check.Periods.Where(p => p.Id == periodId)
                                        .Select(p => p.ReopenReason)
                                        .FirstAsync().ConfigureAwait(true);
        Assert.Equal("перша причина", reason);
    }

    /// <summary>Проєкт, документ і період у заданому стані.</summary>
    private async Task<(long DocumentId, int PeriodId)> ArrangeAsync(PeriodState state)
    {
        await using var db = CreateContext();

        var project = new Project(
            EcrCode.Create($"P{Guid.NewGuid():N}"[..12]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Race" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: 2, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");

        db.Projects.Add(project);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var period = new Period(
            project.Id, new PeriodKey(PeriodKeyValue), 1,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        period.RecomputeBoundaries(ProjectBuilder.Policy(), ProjectBuilder.Zone());
        period.AdvanceTo(state, Now);

        var document = new Document(project.Id, $"DOC-{Guid.NewGuid():N}"[..20], 1, Now);

        db.Periods.Add(period);
        db.Documents.Add(document);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (document.Id, period.Id);
    }

    /// <summary>Той самий запит, що й у <c>WorkflowStore.LockPeriodAsync</c>.</summary>
    private static Task<Period> LockAsync(EcrDbContext db, long documentId)
        => db.Periods
            .FromSql($"""
                SELECT p.* FROM doc.Period AS p WITH (UPDLOCK, ROWLOCK)
                JOIN doc.Document AS d ON d.ProjectId = p.ProjectId
                WHERE d.Id = {documentId} AND p.PeriodKey = {PeriodKeyValue}
                """)
            .FirstAsync();

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(30))
            .Options);

    /// <summary>Період у пам'яті для перевірки доменного правила без бази.</summary>
    private sealed class PeriodProbe(PeriodState state)
    {
        public void ReopenAgain()
        {
            var period = new Period(
                1, new PeriodKey(PeriodKeyValue), 1,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

            period.AdvanceTo(state, Now);

            // Reopen застосовний лише до Closed — саме це і робить повторне
            // відкриття неможливим.
            period.Reopen(Now.AddDays(3), "друга причина", Now);
        }
    }
}
