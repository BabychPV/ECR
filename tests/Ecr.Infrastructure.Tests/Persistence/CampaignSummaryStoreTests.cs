using Ecr.Application.Common;
using Ecr.Application.Reporting;
using Ecr.Application.Reporting.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Огляд кампанії на реальному SQL Server (<c>BE-22</c>).</summary>
/// <remarks>
/// ⚠ Рядки шукаються за ВЛАСНИМ <c>ProjectId</c>, а не за позицією в переліку:
/// база <c>EcrTest_*</c> переживає прогін, і проєкти сусідніх тестів у тому ж
/// періоді законно стоять поруч. Твердження про позицію було б то зеленим, то
/// червоним залежно від порядку прогону.
/// </remarks>
[Collection("SqlServer")]
public sealed class CampaignSummaryStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Стеля, достатня, щоб побачити власні проєкти серед чужих.</summary>
    private const int NoLimit = 10_000;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-22")]
    public async Task Огляд_рахує_етапи_проєкту_і_побудований_зріз()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        // ⛔ Кількості РІЗНІ навмисно — 1 / 2 / 3 / 4. Перша редакція цього
        // тесту давала по одному документу на етап, і мутація «Approved читає
        // `Status = 1` замість `2`» лишала набір ЗЕЛЕНИМ: поданий документ
        // просто ставав на місце затвердженого, а всі чотири числа лишалися
        // одиницями. Рівні лічильники приховують перестановку між етапами;
        // різні — ні, і тепер злам будь-якого одного етапу видно саме в його
        // числі.
        //
        // Базовий документ ланцюга — чернетка: аркуш у складі є, рядка стану
        // немає (`S-17`).
        await ArrangeAsync(db, chain, chain.DocumentId, status: null);
        await AddAsync(db, chain, DocumentStatus.Submitted, count: 2);
        await AddAsync(db, chain, DocumentStatus.Approved, count: 3);
        await AddAsync(db, chain, DocumentStatus.Rejected, count: 4);

        await AddCurrentSnapshotAsync(db, chain.ProjectId, chain.PeriodKey.Value);

        var page = await new CampaignSummaryStore(db)
            .ListAsync(chain.PeriodKey.Value, NoLimit, CancellationToken.None);

        var row = page.Projects.Single(p => p.ProjectId == chain.ProjectId);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ. Перевернути в `CampaignSummaryStore` один рядок
        // агрегату — `WHEN a.Status = 2` (Approved) на `= 1` — і червоним стає
        // рівно це твердження: `Approved` дає 2 замість 3.
        Assert.Equal((10, 1, 2, 3, 4),
            (row.Documents, row.Draft, row.Submitted, row.Approved, row.Rejected));

        // Останній етап кампанії: аркуші затверджено — але доки зрізу немає,
        // регулятор не отримав нічого.
        Assert.Equal(1, row.Snapshots);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-22")]
    public async Task Проєкт_без_жодного_документа_видно_з_нулями_а_не_втрачено()
    {
        // ⛔ Саме цей рядок і є відповіддю «кампанія тут не починалася».
        // Агрегат, побудований ВІД документів, такий проєкт просто не показав
        // би — і найгірший відстаючий зник би з переліку відстаючих.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var empty = await AddEmptyProjectAsync(db, chain, "BE22_EMPTY");

        var page = await new CampaignSummaryStore(db)
            .ListAsync(chain.PeriodKey.Value, NoLimit, CancellationToken.None);

        var row = page.Projects.Single(p => p.ProjectId == empty);

        Assert.Equal((0, 0, 0, 0, 0, 0),
            (row.Documents, row.Draft, row.Submitted, row.Approved, row.Rejected, row.Snapshots));
        Assert.Equal("Empty campaign", row.NameL10n.Get("en"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-22")]
    public async Task Стеля_ріже_перелік_а_Total_лишається_повним()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        await AddEmptyProjectAsync(db, chain, "BE22_CAP_A");
        await AddEmptyProjectAsync(db, chain, "BE22_CAP_B");

        var page = await new CampaignSummaryStore(db)
            .ListAsync(chain.PeriodKey.Value, limit: 2, CancellationToken.None);

        // ⛔ Два числа, а не одне. Перелік обрізано стелею — і саме `Total`
        // каже, що кампанія бачиться не цілком; без нього обрізана відповідь
        // не відрізнялася б від вичерпаної.
        Assert.Equal(2, page.Projects.Count);
        Assert.True(page.Total >= 3, $"Проєктів періоду має бути щонайменше 3, а не {page.Total}.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-22")]
    public async Task Підсумки_рахуються_по_всіх_205_проєктах_а_перелік_обрізано_до_200()
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: рахувати `totals` з обрізаного переліку рядків
        // замість агрегату — і червоним стає рівно це: 200 замість 205, а
        // готовий проєкт за стелею зникає з підсумку.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var key = await FreshPeriodKeyAsync(builder);

        // Проєкт ланцюга (`PRJ…`) сортується ПІСЛЯ всіх `CMP…`, тобто лежить за
        // стелею, — і саме він єдиний готовий.
        var chain = await builder.BuildAsync(periodKey: key, ct: CancellationToken.None);
        await using var db = builder.CreateContext();
        await ArrangeAsync(db, chain, chain.DocumentId, DocumentStatus.Approved);
        await AddCurrentSnapshotAsync(db, chain.ProjectId, key);
        await AddProjectsAsync(db, chain, key, count: 204, "CMP");

        var summary = await Handler(db, Now).HandleAsync(key, CancellationToken.None);

        Assert.Equal(200, summary.Projects.Count);
        Assert.DoesNotContain(summary.Projects, p => p.ProjectId == chain.ProjectId);
        Assert.Equal(205, summary.TotalProjects);

        var t = summary.Totals;
        Assert.Equal((205, 1, 1, 1), (t.Projects, t.Documents, t.Approved, t.Snapshots));
        Assert.Equal((1, 0, 0, 204), (t.Done, t.Overdue, t.AtRisk, t.InProgress));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-22")]
    [InlineData(-1, CampaignProgress.Overdue)]
    [InlineData(0, CampaignProgress.AtRisk)]
    [InlineData(3, CampaignProgress.AtRisk)]
    [InlineData(4, CampaignProgress.InProgress)]
    public async Task Класифікація_на_межах_строку(int lastDayInDays, CampaignProgress expected)
    {
        // `lastDayInDays` — через скільки днів ОСТАННІЙ день подання (0 —
        // сьогодні). Пояс +5, зараз 10:00 місцевого: у UTC-добах строк «падав»
        // би на день раніше, і випадок «через 4 дні» став би ризиком.
        var today = new DateOnly(2026, 6, 15);
        var lastDay = today.AddDays(lastDayInDays);
        var (db, projectId, key) = await CampaignWithOneProjectAsync(lastDay);
        await using var _ = db;

        var summary = await Handler(db, LocalToUtc(today, 10, 0)).HandleAsync(key, CancellationToken.None);
        var row = summary.Projects.Single(p => p.ProjectId == projectId);

        Assert.Equal(expected, row.Progress);

        // Строк — опівніч ПІСЛЯ останнього дня, і віддається в поясі проєкту.
        Assert.Equal(lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue), row.SubmissionDeadline!.Value.DateTime);
        Assert.Equal(LocalToUtc(lastDay.AddDays(1), 0, 0), row.SubmissionDeadline.Value.UtcDateTime);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-22")]
    public async Task Строк_минає_опівночі_в_поясі_проєкту_а_не_в_UTC()
    {
        var lastDay = new DateOnly(2026, 6, 15);
        var (db, projectId, key) = await CampaignWithOneProjectAsync(lastDay);
        await using var _ = db;

        // 23:30 останнього дня — ще ризик; 00:30 наступного — прострочено,
        // хоча в UTC це все ще 15 червня.
        var before = await Handler(db, LocalToUtc(lastDay, 23, 30)).HandleAsync(key, CancellationToken.None);
        var after = await Handler(db, LocalToUtc(lastDay.AddDays(1), 0, 30)).HandleAsync(key, CancellationToken.None);

        Assert.Equal(CampaignProgress.AtRisk, before.Projects.Single(p => p.ProjectId == projectId).Progress);
        Assert.Equal(CampaignProgress.Overdue, after.Projects.Single(p => p.ProjectId == projectId).Progress);
        Assert.Equal(1, after.Totals.Overdue);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-22")]
    [InlineData(false, CampaignProgress.Overdue)]
    [InlineData(true, CampaignProgress.Done)]
    public async Task Готово_лише_коли_все_затверджено_і_є_зріз(bool withSnapshot, CampaignProgress expected)
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати зріз з умови «готово» — і червоним
        // стає рівно випадок без зрізу: усе затверджено, але регулятор ще не
        // отримав нічого, а строк минув учора.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var key = await FreshPeriodKeyAsync(builder);
        var chain = await builder.BuildAsync(periodKey: key, ct: CancellationToken.None);
        await using var db = builder.CreateContext();
        await ArrangeAsync(db, chain, chain.DocumentId, DocumentStatus.Approved);

        if (withSnapshot)
        {
            await AddCurrentSnapshotAsync(db, chain.ProjectId, key);
        }

        var today = new DateOnly(2026, 6, 15);
        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == key);
        SetLastDay(period, today.AddDays(-1));
        await db.SaveChangesAsync(CancellationToken.None);

        var summary = await Handler(db, LocalToUtc(today, 10, 0)).HandleAsync(key, CancellationToken.None);

        Assert.Equal(expected, summary.Projects.Single(p => p.ProjectId == chain.ProjectId).Progress);
        Assert.Equal(withSnapshot ? 1 : 0, summary.Totals.Done);
    }

    /// <summary>Пояс проєктів у тестах класифікації (той самий, що дає будівник ланцюга).</summary>
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Almaty");

    private static DateTime LocalToUtc(DateOnly day, int hour, int minute)
        => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, minute)), Zone);

    /// <summary>
    /// Ставить межі так, щоб <paramref name="lastDay"/> був останнім днем
    /// подання: строк — опівніч наступної доби в поясі проєкту.
    /// </summary>
    private static void SetLastDay(Period period, DateOnly lastDay)
    {
        var grace = lastDay.AddDays(1).DayNumber - period.PeriodEnd.DayNumber;
        period.RecomputeBoundaries(
            new PeriodPolicy(EcrCode.Create("BE22_MEM"), 0, grace, grace + 10, 0), Zone);
    }

    /// <summary>Свіжий період з одним проєктом (ланцюг) і строком на <paramref name="lastDay"/>.</summary>
    private async Task<(EcrDbContext Db, int ProjectId, int Key)> CampaignWithOneProjectAsync(DateOnly lastDay)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var key = await FreshPeriodKeyAsync(builder);
        var chain = await builder.BuildAsync(periodKey: key, ct: CancellationToken.None);
        var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == key);
        SetLastDay(period, lastDay);
        await db.SaveChangesAsync(CancellationToken.None);

        return (db, chain.ProjectId, key);
    }

    /// <summary>
    /// Період, якого в спільній тестовій базі ще немає ні в кого: база переживає
    /// прогони, і підсумок по спільному періоду рахував би й сусідні тести.
    /// </summary>
    private static async Task<int> FreshPeriodKeyAsync(TestDocumentBuilder builder)
    {
        await using var db = builder.CreateContext();

        while (true)
        {
            var key = PeriodKey.Create(Random.Shared.Next(3000, 10_000), Random.Shared.Next(1, 13)).Value;
            if (!await db.Periods.AnyAsync(p => p.PeriodKeyValue == key, CancellationToken.None))
            {
                return key;
            }
        }
    }

    /// <summary><paramref name="count"/> порожніх проєктів у періоді <paramref name="key"/>, пакетом.</summary>
    private static async Task AddProjectsAsync(EcrDbContext db, TestDocument chain, int key, int count, string prefix)
    {
        var tag = $"{Guid.NewGuid():N}"[..8];
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync(CancellationToken.None);
        var name = new LocalizedText(new Dictionary<string, string> { ["en"] = "Campaign" });

        var projects = Enumerable.Range(0, count)
            .Select(i => new Project(
                EcrCode.Create($"{prefix}{i:D3}_{tag}"), name,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
                chain.TemplateVersionId, PeriodKind.Monthly, policyId, "Asia/Almaty"))
            .ToList();

        db.Projects.AddRange(projects);
        await db.SaveChangesAsync(CancellationToken.None);

        var period = new PeriodKey(key);
        db.Periods.AddRange(projects.Select(p => new Period(
            p.Id, period, (byte)period.Sequence,
            new DateOnly(period.Year, period.Sequence, 1),
            new DateOnly(period.Year, period.Sequence, DateTime.DaysInMonth(period.Year, period.Sequence)))));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Обробник над справжнім сховищем; користувач має лише право огляду.</summary>
    private static GetCampaignSummaryHandler Handler(EcrDbContext db, DateTime utcNow)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(7, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 7 }.Permission(GetCampaignSummaryHandler.Permission).Build());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(7);

        return new GetCampaignSummaryHandler(
            new CampaignSummaryStore(db), access, user, new TestClock(utcNow), new CampaignProgressPolicy(3));
    }

    /// <summary>Заводить <paramref name="count"/> документів у заданому стані.</summary>
    private static async Task AddAsync(
        EcrDbContext db, TestDocument chain, DocumentStatus status, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var document = new Document(chain.ProjectId, $"BE22-{Guid.NewGuid():N}"[..20], 1, Now);
            db.Documents.Add(document);
            await db.SaveChangesAsync(CancellationToken.None);

            await ArrangeAsync(db, chain, document.Id, status);
        }
    }

    /// <summary>Проєкт із тим самим періодом і БЕЗ документів; повертає його Id.</summary>
    private static async Task<int> AddEmptyProjectAsync(EcrDbContext db, TestDocument chain, string code)
    {
        var tag = $"{Guid.NewGuid():N}"[..8];
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync(CancellationToken.None);

        var project = new Project(
            EcrCode.Create($"{code}_{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Empty campaign" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            chain.TemplateVersionId, PeriodKind.Monthly, policyId, "Asia/Almaty");

        db.Projects.Add(project);
        await db.SaveChangesAsync(CancellationToken.None);

        var key = chain.PeriodKey;
        db.Periods.Add(new Period(
            project.Id, key, (byte)key.Sequence,
            new DateOnly(key.Year, key.Sequence, 1),
            new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence))));

        await db.SaveChangesAsync(CancellationToken.None);
        return project.Id;
    }

    /// <summary>Поточний зріз звіту за проєкт і період.</summary>
    private static async Task AddCurrentSnapshotAsync(EcrDbContext db, int projectId, int periodKey)
    {
        var tag = $"{Guid.NewGuid():N}"[..8];
        var definition = new ReportDef(
            EcrCode.Create($"RPT{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "BE-22" }),
            isRegulatory: true);

        db.ReportDefs.Add(definition);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = new ReportVersion(definition.Id, "1.0", "[]", "{}", Now);
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);

        var snapshot = new ReportSnapshot(
            version.Id, projectId, periodKey, SnapshotStatus.Draft, Now, builtByUserId: 1);
        snapshot.MakeCurrent();

        db.ReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Аркуш у складі документа і, за потреби, його стан у періоді.</summary>
    private static async Task ArrangeAsync(
        EcrDbContext db, TestDocument chain, long documentId, DocumentStatus? status)
    {
        db.DocumentSheets.Add(new DocumentSheet(documentId, chain.SheetDefId));

        if (status is { } target)
        {
            var state = new ApprovalState(documentId, chain.SheetDefId, chain.PeriodKey.Value);
            state.Submit(1, Now);

            if (target == DocumentStatus.Approved)
            {
                state.Approve(1, Now);
            }
            else if (target == DocumentStatus.Rejected)
            {
                state.Reject(1, "BE-22", Now);
            }

            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }
}
