using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
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
