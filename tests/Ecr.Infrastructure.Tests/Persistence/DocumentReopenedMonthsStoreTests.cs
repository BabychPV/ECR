using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ 10.11 (слово людини через координатора): закритий місяць, відкритий для ОДНОГО документа
/// (<c>doc.DocumentPeriodReopen</c>), у переліку — <c>DocumentSummary.ReopenedPeriods</c> рядка цього документа і фільтр
/// <c>DocumentListFilter.HasReopenedMonths</c>.
/// </summary>
/// <remarks>
/// Мутаційні докази (локально, тимчасовий відкат, не в коміті):
/// <list type="bullet">
/// <item><c>ActiveReopens</c> без <c>OpenUntil &gt; now</c> — прострочений виняток документа C дає «Reopened», червоний.</item>
/// <item><c>ActiveReopens</c> без <c>ClosedAt == null</c> — закритий виняток документа B дає «Reopened», червоний.</item>
/// <item>фільтр <c>HasReopenedMonths</c> не застосовано в <c>ListAsync</c> — фільтр повертає всі документи, червоний.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentReopenedMonthsStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_за_рік_несе_діючий_виняток_лише_свого_документа_а_фільтр_лишає_лише_його()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var year = chain.PeriodKey.Year;
        var key = chain.PeriodKey.Value;
        var now = DateTime.UtcNow;
        var until = DateTime.SpecifyKind(now.AddDays(3).Date.AddHours(18), DateTimeKind.Utc);

        long a = chain.DocumentId, b, c;
        await using (var db = builder.CreateContext())
        {
            var user = await UserAsync(db);
            b = await DocumentAsync(db, chain.ProjectId, "RB");
            c = await DocumentAsync(db, chain.ProjectId, "RC");

            // A — діючий; B — закритий людиною; C — строк минув, але job ще не позначив закритим.
            db.DocumentPeriodReopens.Add(DocumentPeriodReopen.Open(a, key, until, "Лист №19: лише A", user, now));
            var closed = DocumentPeriodReopen.Open(b, key, until, "Лист №19: B", user, now);
            closed.Close(now, user, DocumentPeriodReopen.ClosedManually);
            db.DocumentPeriodReopens.Add(closed);
            db.DocumentPeriodReopens.Add(DocumentPeriodReopen.Open(c, key, now.AddHours(-1), "Лист №19: C", user, now.AddHours(-2)));
            await db.SaveChangesAsync();
        }

        await using var read = builder.CreateContext();
        Task<PagedResult<DocumentSummary>> List(PeriodKeyFilter period, DocumentListFilter filter, int[]? visible = null)
            => new DocumentStore(read).ListAsync(
                chain.ProjectId, period, filter, new CursorRequest(Limit: 50), visible, CancellationToken.None);

        var page = await List(new PeriodKeyFilter(null), new DocumentListFilter(null, null, Year: year));
        var reopenedA = Assert.Single(page.Items.Single(d => d.Id == a).ReopenedPeriods!);
        Assert.Equal(key, reopenedA.PeriodKey);
        Assert.Equal(until, reopenedA.OpenUntil);
        Assert.Equal(DateTimeKind.Utc, reopenedA.OpenUntil.Kind);
        Assert.Empty(page.Items.Single(d => d.Id == b).ReopenedPeriods!);
        Assert.Empty(page.Items.Single(d => d.Id == c).ReopenedPeriods!);

        // Без року (смуги немає) позначка не рахується: `null`, а не порожньо.
        var noYear = await List(new PeriodKeyFilter(null), default);
        Assert.All(noYear.Items, d => Assert.Null(d.ReopenedPeriods));

        // Фільтр: за рік, за місяць і без обох — лише A; «без винятку» — решта.
        Assert.Equal([a], Ids(await List(new PeriodKeyFilter(null), new DocumentListFilter(null, null, Year: year, HasReopenedMonths: true))));
        Assert.Equal([a], Ids(await List(new PeriodKeyFilter(key), new DocumentListFilter(null, null, HasReopenedMonths: true))));
        Assert.Equal([a], Ids(await List(new PeriodKeyFilter(null), new DocumentListFilter(null, null, HasReopenedMonths: true))));
        Assert.Equal(
            new[] { b, c }.Order(),
            Ids(await List(new PeriodKeyFilter(null), new DocumentListFilter(null, null, Year: year, HasReopenedMonths: false))));

        // Інший місяць того самого року — нікого.
        var otherMonth = key % 100 == 12 ? key - 1 : key + 1;
        Assert.Empty((await List(new PeriodKeyFilter(otherMonth), new DocumentListFilter(null, null, HasReopenedMonths: true))).Items);

        // Межа видимості переліку: документи проєкту поза видимими не потрапляють навіть із винятком.
        Assert.Empty((await List(
            new PeriodKeyFilter(null), new DocumentListFilter(null, null, Year: year, HasReopenedMonths: true),
            [chain.ProjectId + 100_000])).Items);

        // Строк минув — виняток більше не діє ні в позначці, ні у фільтрі (без job, що позначає закритим).
        var expired = new DocumentListFilter(null, null, Year: year, AsOfUtc: until.AddMinutes(1));
        Assert.Empty((await List(new PeriodKeyFilter(null), expired with { HasReopenedMonths = true })).Items);
        Assert.Empty((await List(new PeriodKeyFilter(null), expired)).Items.Single(d => d.Id == a).ReopenedPeriods!);

        // Закрито людиною — A повертається до «без винятку».
        await using (var db = builder.CreateContext())
        {
            var open = await db.DocumentPeriodReopens.SingleAsync(x => x.DocumentId == a && x.ClosedAt == null);
            open.Close(DateTime.UtcNow, null, DocumentPeriodReopen.ClosedManually);
            await db.SaveChangesAsync();
        }

        await using var after = builder.CreateContext();
        var closedPage = await new DocumentStore(after).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(null), new DocumentListFilter(null, null, Year: year, HasReopenedMonths: true),
            new CursorRequest(Limit: 50), null, CancellationToken.None);
        Assert.Empty(closedPage.Items);
    }

    private static long[] Ids(PagedResult<DocumentSummary> page) => [.. page.Items.Select(d => d.Id).Order()];

    /// <summary>Автор винятку (FK на користувача).</summary>
    private static async Task<int> UserAsync(EcrDbContext db)
    {
        var name = $"ro_{Guid.NewGuid():N}"[..20];
        var user = new Ecr.Domain.Entities.Security.User(name, name, AuthProvider.Local);
        user.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash("List-Reopened-Months-2026!"));
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<long> DocumentAsync(EcrDbContext db, int projectId, string key)
    {
        var document = new Document(projectId, $"{key}-{Guid.NewGuid():N}"[..20], 1, DateTime.UtcNow);
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document.Id;
    }
}
