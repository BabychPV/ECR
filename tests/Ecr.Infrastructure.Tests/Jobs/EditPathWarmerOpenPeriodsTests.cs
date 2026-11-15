// tests/Ecr.Infrastructure.Tests/Jobs/EditPathWarmerOpenPeriodsTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// AN-190: <see cref="EditPathWarmer.OpenPeriodVersionsAsync"/> бере версії лише відкритих (<c>Open</c>/<c>Grace</c>)
/// періодів — <c>Scheduled</c> і <c>Closed</c> не гріються — найновіші першими й не більше стелі.
/// </summary>
/// <remarks>
/// ⚠ База спільна з іншими тестами колекції: перевіряються лише власні версії (входить / не входить) і форма
/// стелі, а не повний перелік. Мутація: прибрати <c>p.State IN (...)</c> — червоний (Scheduled/Closed входять).
/// </remarks>
[Collection("SqlServer")]
public sealed class EditPathWarmerOpenPeriodsTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лише_версії_відкритих_періодів_найновіші_першими_у_межах_стелі()
    {
        // ⚠ Правило 394 / ретро 375: періоди 2099xx, а відкриті тестом періоди закриваються у finally — відкритий
        // період вмикає глобальну поведінку (прогрів, задачі станів періодів) для решти тестів спільної бази.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var open = await builder.BuildAsync(PeriodKey);
        var scheduled = await builder.BuildAsync(PeriodKey);
        var closed = await builder.BuildAsync(PeriodKey);
        var grace = await builder.BuildAsync(PeriodKey);

        try
        {
            await SetStateAsync(builder, open, PeriodState.Open);
            await SetStateAsync(builder, closed, PeriodState.Closed);
            await SetStateAsync(builder, grace, PeriodState.Grace);

            await using var db = builder.CreateContext();
            var all = await EditPathWarmer.OpenPeriodVersionsAsync(db, EditPathWarmer.MaxVersionsLimit, CancellationToken.None);

            Assert.Contains(open.TemplateVersionId, all);
            Assert.Contains(grace.TemplateVersionId, all);
            Assert.DoesNotContain(scheduled.TemplateVersionId, all);
            Assert.DoesNotContain(closed.TemplateVersionId, all);
            Assert.Equal(all.OrderDescending(), all);
            Assert.Equal(all.Distinct(), all);

            var one = await EditPathWarmer.OpenPeriodVersionsAsync(db, 1, CancellationToken.None);
            Assert.Equal([all[0]], one);
        }
        finally
        {
            await using var cleanup = builder.CreateContext();
            int[] projects = [open.ProjectId, grace.ProjectId];
            await cleanup.Periods
                .Where(p => projects.Contains(p.ProjectId) && p.PeriodKeyValue == PeriodKey)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, PeriodState.Closed));
        }
    }

    /// <summary>
    /// B53: <see cref="EditPathWarmer.ReadScopeVersionsAsync"/> бере ВСІ версії шкали проєкту з відкритим періодом —
    /// і ту, що в жодному відкритому періоді не діє (її <see cref="EditPathWarmer.OpenPeriodVersionsAsync"/> не дає, а
    /// <c>ReadScopeAsync</c> обʼєднує в знімок меж читання документа); проєкти без відкритого періоду — ні.
    /// </summary>
    /// <remarks>
    /// Мутації (локальний worktree, відкат): брати лише основну версію проєкту замість шкали — червоний (історичної версії
    /// немає); прибрати <c>WHERE EXISTS</c> відкритого періоду — червоний (версія закритого проєкту входить); виключення
    /// прогрітих після стелі замість <c>NOT IN</c> у запиті — червоний (стеля 1 порожня).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task B53_версії_шкали_проєктів_з_відкритим_періодом_включно_з_історичною()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var open = await builder.BuildAsync(PeriodKey);
        var closed = await builder.BuildAsync(PeriodKey);
        int history;
        int current;
        int closedHistory;

        await using (var db = builder.CreateContext())
        {
            // Шкала відкритого проєкту: до 209901 — історична версія, з 209901 — поточна. Поточну створено ОСТАННЬОЮ:
            // найбільший Id має чинна версія, як на живій базі (F13 B53, D1).
            var old = new TemplateVersion(open.TemplateId, $"0.{Random.Shared.Next(100_000, 999_999)}.0", 1, At);
            var oldClosed = new TemplateVersion(closed.TemplateId, $"0.{Random.Shared.Next(100_000, 999_999)}.0", 1, At);
            db.TemplateVersions.AddRange(old, oldClosed);
            await db.SaveChangesAsync();
            var now = new TemplateVersion(open.TemplateId, $"9.{Random.Shared.Next(100_000, 999_999)}.0", 1, At);
            db.TemplateVersions.Add(now);
            await db.SaveChangesAsync();
            db.ProjectTemplateVersions.Add(new ProjectTemplateVersion(open.ProjectId, open.TemplateId, 200001, old.Id));
            db.ProjectTemplateVersions.Add(new ProjectTemplateVersion(open.ProjectId, open.TemplateId, PeriodKey, now.Id));
            db.ProjectTemplateVersions.Add(new ProjectTemplateVersion(closed.ProjectId, closed.TemplateId, 200001, oldClosed.Id));
            await db.SaveChangesAsync();
            history = old.Id;
            current = now.Id;
            closedHistory = oldClosed.Id;
        }

        try
        {
            await SetStateAsync(builder, open, PeriodState.Open);
            await SetStateAsync(builder, closed, PeriodState.Closed);

            await using var db = builder.CreateContext();
            var effective = await EditPathWarmer.OpenPeriodVersionsAsync(db, EditPathWarmer.MaxVersionsLimit, CancellationToken.None);
            var all = await EditPathWarmer.ReadScopeVersionsAsync(db, EditPathWarmer.MaxVersionsLimit, [], CancellationToken.None);

            // Прогалина, яку закриває B53: історичної версії серед чинних у відкритих періодах немає.
            Assert.Contains(current, effective);
            Assert.DoesNotContain(history, effective);

            Assert.Contains(current, all);
            Assert.Contains(history, all);
            Assert.DoesNotContain(closed.TemplateVersionId, all);
            Assert.DoesNotContain(closedHistory, all);
            Assert.Equal(all.OrderDescending(), all);
            Assert.Equal(all.Distinct(), all);

            // ⛔ F13 D1: прогріті (чинні, з найбільшими Id) виключаються ДО стелі — стеля 1 дає історичну, а не порожньо.
            Assert.True(current > history);
            var first = await EditPathWarmer.ReadScopeVersionsAsync(db, 1, effective, CancellationToken.None);
            Assert.Equal([history], first);
        }
        finally
        {
            await using var cleanup = builder.CreateContext();
            await cleanup.Periods
                .Where(p => p.ProjectId == open.ProjectId && p.PeriodKeyValue == PeriodKey)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, PeriodState.Closed));
        }
    }

    private const int PeriodKey = 209901;

    private static async Task SetStateAsync(TestDocumentBuilder builder, TestDocument document, PeriodState state)
    {
        await using var db = builder.CreateContext();
        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.AdvanceTo(state, At);
        await db.SaveChangesAsync();
    }
}
