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

        // Три документи × 12 місяців — смуга року додає рівно ОДИН запит на сторінку, а не по запиту на документ чи місяць.
        Assert.Equal(3, page.Items.Count);
        Assert.True(counter.Tally.Snapshot().Total == withoutYear + 1, $"без року {withoutYear}; " + counter.Tally.Snapshot().Format());

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
