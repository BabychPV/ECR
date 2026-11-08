using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ 2026-11-07 Period clarity (п. e2, квіз людини 07.11): смуга станів року в переліку документів
/// (<c>DocumentSummary.PeriodStates</c>) і фільтр стану за рік без періоду (<c>DocumentListFilter.Year</c>).
/// </summary>
/// <remarks>
/// Мутаційні докази (локально):
/// <list type="bullet">
/// <item><c>PeriodStatesBatchAsync</c> без межі видимості (<c>db.DocumentSheets.Where(s =&gt; s.IsIncluded)</c> замість
/// <c>VisibleSheets</c>) — схований аркуш тягне січень у «частково», червоний (S18).</item>
/// <item><c>WhereYearState</c> без умови <c>State != Scheduled</c> — майбутні місяці роблять «затверджений рік»
/// частковим, червоний.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentYearStatesStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-93")]
    public async Task Смуга_року_дванадцять_місяців_за_видимими_аркушами_одним_запитом()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var year = chain.PeriodKey.Year;

        int sheet2;
        long a, b;
        await using (var db = builder.CreateContext())
        {
            sheet2 = await YearAsync(db, chain, started: 2);
            a = await DocumentAsync(db, chain, "YA", [chain.SheetDefId, sheet2],
                (chain.SheetDefId, 1, DocumentStatus.Submitted),
                (chain.SheetDefId, 2, DocumentStatus.Approved), (sheet2, 2, DocumentStatus.Approved));
            b = await DocumentAsync(db, chain, "YB", [chain.SheetDefId],
                (chain.SheetDefId, 1, DocumentStatus.Approved), (chain.SheetDefId, 3, DocumentStatus.Rejected));
        }

        var counter = new DbCommandCounter();
        await using var counted = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(counter)
            .Options);

        // Базова сторінка без року (розігрів і число запитів без смуги).
        await new DocumentStore(counted).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(null), default, new CursorRequest(Limit: 50), null, CancellationToken.None);
        var withoutYear = counter.Tally.Snapshot().Total;
        counter.Tally.Reset();

        var page = await new DocumentStore(counted).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(null), new DocumentListFilter(null, null, Year: year),
            new CursorRequest(Limit: 50), visibleProjectIds: null, CancellationToken.None);

        string[] tail = [.. Enumerable.Repeat("Draft", 9)];
        Assert.Equal(
            (string[])["PartlySubmitted", "Approved", "Draft", .. tail],
            page.Items.Single(d => d.Id == a).PeriodStates!.Select(s => s.State));
        Assert.Equal(
            (string[])["Approved", "Draft", "Rejected", .. tail],
            page.Items.Single(d => d.Id == b).PeriodStates!.Select(s => s.State));
        Assert.Equal(
            Enumerable.Range(1, 12).Select(m => year * 100 + m),
            page.Items.Single(d => d.Id == a).PeriodStates!.Select(s => s.PeriodKey));

        // Три документи × 12 місяців — смуга року додає рівно ОДИН запит на сторінку, а не по запиту на документ чи місяць;
        // ✎ g4: позначка застарілості за рік — ще ОДИН запит періодів року з актуальним прогоном (прогонів немає — і все).
        Assert.Equal(3, page.Items.Count);
        Assert.True(counter.Tally.Snapshot().Total == withoutYear + 2, $"без року {withoutYear}; " + counter.Tally.Snapshot().Format());

        // Аркуш 2 схований від читача (S18): січень документа A — «подано», а не «частково».
        IReadOnlyCollection<(int, int)> hidden = [(chain.ProjectId, sheet2)];
        await using var plain = builder.CreateContext();
        var narrowed = await new DocumentStore(plain).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(null), new DocumentListFilter(null, null, HiddenSheetDefIds: hidden, Year: year),
            new CursorRequest(Limit: 50), visibleProjectIds: null, CancellationToken.None);
        Assert.Equal("Submitted", narrowed.Items.Single(d => d.Id == a).PeriodStates![0].State);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стан_за_рік_рахується_за_розпочатими_місяцями()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var year = chain.PeriodKey.Year;

        long partly, submitted, approved, rejected;
        int sheet2;
        await using (var db = builder.CreateContext())
        {
            sheet2 = await YearAsync(db, chain, started: 2);
            partly = await DocumentAsync(db, chain, "RP", [chain.SheetDefId, sheet2],
                (chain.SheetDefId, 1, DocumentStatus.Submitted),
                (chain.SheetDefId, 2, DocumentStatus.Approved), (sheet2, 2, DocumentStatus.Approved));
            submitted = await DocumentAsync(db, chain, "RS", [chain.SheetDefId],
                (chain.SheetDefId, 1, DocumentStatus.Submitted), (chain.SheetDefId, 2, DocumentStatus.Approved));
            approved = await DocumentAsync(db, chain, "RA", [chain.SheetDefId],
                (chain.SheetDefId, 1, DocumentStatus.Approved), (chain.SheetDefId, 2, DocumentStatus.Approved));
            rejected = await DocumentAsync(db, chain, "RR", [chain.SheetDefId],
                (chain.SheetDefId, 1, DocumentStatus.Approved), (chain.SheetDefId, 2, DocumentStatus.Rejected));
        }

        await using var read = builder.CreateContext();

        async Task<long[]> Ids(DocumentListFilter filter)
            => [.. (await new DocumentStore(read).ListAsync(
                    chain.ProjectId, new PeriodKeyFilter(null), filter with { Year = year },
                    new CursorRequest(Limit: 50), visibleProjectIds: null, CancellationToken.None))
                .Items.Select(d => d.Id).Order()];

        Assert.Equal([approved], await Ids(new DocumentListFilter(DocumentStatus.Approved, null)));
        Assert.Equal([submitted], await Ids(new DocumentListFilter(DocumentStatus.Submitted, null)));
        Assert.Equal([rejected], await Ids(new DocumentListFilter(DocumentStatus.Rejected, null)));
        Assert.Equal([partly], await Ids(new DocumentListFilter(null, null, PartlySubmitted: true)));

        // Документ ланцюга без складу — «чернетка» року (як і за місяць).
        Assert.Equal([chain.DocumentId], await Ids(new DocumentListFilter(DocumentStatus.Draft, null)));

        // Схований аркуш 2 не входить: «частково» стає «подано» (S18).
        IReadOnlyCollection<(int, int)> hidden = [(chain.ProjectId, sheet2)];
        Assert.Equal(
            new[] { partly, submitted }.Order(),
            await Ids(new DocumentListFilter(DocumentStatus.Submitted, null, HiddenSheetDefIds: hidden)));
    }

    /// <summary>
    /// ✎ Period clarity (координатор 07.11, «без боргів»): зведення за РІК (<c>GET /documents/summary?year=</c>) —
    /// ОДНИМ запитом і за тим самим правилом, що й фільтр стану переліку за рік: числа смуги дорівнюють кількості
    /// рядків, які дає фільтр.
    /// </summary>
    /// <remarks>
    /// Мутаційний доказ (локально): без <c>p.State &lt;&gt; 0</c> у <c>SummarizeYearAsync</c> майбутні місяці тягнуть
    /// «затверджений» і «поданий» документи в «частково» — червоний; без межі схованого аркуша — «частково» лишається.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-93")]
    public async Task Зведення_за_рік_одним_запитом_збігається_з_фільтром_стану_за_рік()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var year = chain.PeriodKey.Year;

        int sheet2;
        await using (var db = builder.CreateContext())
        {
            sheet2 = await YearAsync(db, chain, started: 2);
            await DocumentAsync(db, chain, "SP", [chain.SheetDefId, sheet2],
                (chain.SheetDefId, 1, DocumentStatus.Submitted),
                (chain.SheetDefId, 2, DocumentStatus.Approved), (sheet2, 2, DocumentStatus.Approved));
            await DocumentAsync(db, chain, "SS", [chain.SheetDefId],
                (chain.SheetDefId, 1, DocumentStatus.Submitted), (chain.SheetDefId, 2, DocumentStatus.Approved));
            await DocumentAsync(db, chain, "SA", [chain.SheetDefId],
                (chain.SheetDefId, 1, DocumentStatus.Approved), (chain.SheetDefId, 2, DocumentStatus.Approved));
            await DocumentAsync(db, chain, "SR", [chain.SheetDefId],
                (chain.SheetDefId, 1, DocumentStatus.Approved), (chain.SheetDefId, 2, DocumentStatus.Rejected));
        }

        var counter = new DbCommandCounter();
        await using var counted = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(counter)
            .Options);

        var summary = await new DocumentListSummaryStore(counted)
            .SummarizeYearAsync(chain.ProjectId, year, visibleProjectIds: null, restrictions: null, currentUserId: null, CancellationToken.None);

        // Документ ланцюга без складу — чернетка; решта — по одному в кожному стані.
        Assert.Equal(
            (1, 1, 1, 1, 1),
            (summary.Draft, summary.PartlySubmitted, summary.Submitted, summary.Approved, summary.Rejected));
        Assert.Equal(0, summary.StaleResultsCount);

        // Агрегат станів — один запит; ✎ g4: плюс один запит періодів року з актуальним прогоном (лічильник застарілих
        // за рік); прогонів немає — далі жодного запиту.
        Assert.True(counter.Tally.Snapshot().Total == 2, counter.Tally.Snapshot().Format());

        // Та сама відповідь, що дає фільтр стану переліку за рік (одне правило — одне число).
        await using var read = builder.CreateContext();
        async Task<int> Rows(DocumentListFilter filter)
            => (await new DocumentStore(read).ListAsync(
                    chain.ProjectId, new PeriodKeyFilter(null), filter with { Year = year },
                    new CursorRequest(Limit: 50), visibleProjectIds: null, CancellationToken.None)).Items.Count;

        Assert.Equal(await Rows(new DocumentListFilter(DocumentStatus.Draft, null)), summary.Draft);
        Assert.Equal(await Rows(new DocumentListFilter(null, null, PartlySubmitted: true)), summary.PartlySubmitted);
        Assert.Equal(await Rows(new DocumentListFilter(DocumentStatus.Submitted, null)), summary.Submitted);
        Assert.Equal(await Rows(new DocumentListFilter(DocumentStatus.Approved, null)), summary.Approved);
        Assert.Equal(await Rows(new DocumentListFilter(DocumentStatus.Rejected, null)), summary.Rejected);

        // Схований аркуш 2 (S18) не вносить стан: «частково» стає «подано».
        var code = (await read.SheetDefs.SingleAsync(s => s.Id == sheet2)).Code;
        var hidden = await new DocumentListSummaryStore(read).SummarizeYearAsync(
            chain.ProjectId, year, visibleProjectIds: null,
            new SummaryRestrictions([(chain.ProjectId, code)], []), currentUserId: null, CancellationToken.None);
        Assert.Equal((0, 2), (hidden.PartlySubmitted, hidden.Submitted));

        // Межа грантів: проєкт поза видимими — нулі.
        var foreign = await new DocumentListSummaryStore(read).SummarizeYearAsync(
            chain.ProjectId, year, visibleProjectIds: [chain.ProjectId + 100_000], restrictions: null, currentUserId: null, CancellationToken.None);
        Assert.Equal(0, foreign.Draft + foreign.PartlySubmitted + foreign.Submitted + foreign.Approved + foreign.Rejected);
    }

    /// <summary>
    /// ✎ g2 (макет 08.11, людина: «Year 2024 — No documents for 2024»): рік, у якому в проєкту НЕМАЄ періодів, — ні
    /// рядків переліку, ні лічильників. Корінь дефекту: рік звужував лише смугу й фільтр стану, тож 2024 показував
    /// документи 2026 із порожньою смугою, а зведення рахувало їх як «чернетки».
    /// </summary>
    /// <remarks>
    /// Мутаційний доказ (локально, після вікна W15l): без <c>WhereProjectYear</c> у <c>ListAsync</c> — у переліку за
    /// чужий рік документ ланцюга, червоний; без <c>EXISTS … doc.Period py</c> у <c>SummarizeYearAsync</c> — Draft = 1.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рік_без_періодів_проєкту_не_дає_ні_документів_ні_лічильників()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var year = chain.PeriodKey.Year;
        var empty = year - 2;

        await using var read = builder.CreateContext();
        var own = await new DocumentStore(read).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(null), new DocumentListFilter(null, null, Year: year),
            new CursorRequest(Limit: 50), visibleProjectIds: null, CancellationToken.None);
        Assert.Contains(own.Items, d => d.Id == chain.DocumentId);

        var other = await new DocumentStore(read).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(null), new DocumentListFilter(null, null, Year: empty),
            new CursorRequest(Limit: 50), visibleProjectIds: null, CancellationToken.None);
        Assert.Empty(other.Items);

        var summary = await new DocumentListSummaryStore(read)
            .SummarizeYearAsync(chain.ProjectId, empty, visibleProjectIds: null, restrictions: null, currentUserId: null, CancellationToken.None);
        Assert.Equal(
            (0, 0, 0, 0, 0),
            (summary.Draft, summary.PartlySubmitted, summary.Submitted, summary.Approved, summary.Rejected));
    }

    /// <summary>Добудовує 12 періодів року ланцюга; перші <paramref name="started"/> — <c>Open</c>. Повертає другий аркуш.</summary>
    private static async Task<int> YearAsync(EcrDbContext db, TestDocument chain, int started)
    {
        var year = chain.PeriodKey.Year;
        var existing = await db.Periods.Where(p => p.ProjectId == chain.ProjectId).ToListAsync();
        for (var m = 1; m <= 12; m++)
        {
            var key = new PeriodKey(year * 100 + m);
            var period = existing.SingleOrDefault(p => p.PeriodKeyValue == key.Value);
            if (period is null)
            {
                period = new Period(
                    chain.ProjectId, key, (byte)m, new DateOnly(year, m, 1), new DateOnly(year, m, DateTime.DaysInMonth(year, m)));
                db.Periods.Add(period);
            }

            if (m <= started)
            {
                period.AdvanceTo(PeriodState.Open, Now);
            }
        }

        var sheet2 = new SheetDef(
            chain.TemplateVersionId, EcrCode.Create($"YS2_{Guid.NewGuid():N}"[..12].ToUpperInvariant()),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet 2" }), 2);
        db.SheetDefs.Add(sheet2);
        await db.SaveChangesAsync();
        return sheet2.Id;
    }

    /// <summary>Документ зі складом і станами аркушів (аркуш, місяць, стан).</summary>
    private static async Task<long> DocumentAsync(
        EcrDbContext db, TestDocument chain, string key, int[] sheets, params (int Sheet, int Month, DocumentStatus State)[] states)
    {
        var document = new Document(chain.ProjectId, $"{key}-{Guid.NewGuid():N}"[..20], 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        foreach (var sheet in sheets)
        {
            db.DocumentSheets.Add(new DocumentSheet(document.Id, sheet));
        }

        foreach (var (sheet, month, wanted) in states)
        {
            var state = new ApprovalState(document.Id, sheet, chain.PeriodKey.Year * 100 + month);
            state.Submit(1, Now);
            if (wanted == DocumentStatus.Approved)
            {
                state.Approve(1, Now);
            }
            else if (wanted == DocumentStatus.Rejected)
            {
                state.Reject(1, "test", Now);
            }

            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync();
        return document.Id;
    }
}
