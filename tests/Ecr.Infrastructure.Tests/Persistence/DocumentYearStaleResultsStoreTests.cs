using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ g4 (координатор 08.11, «State filter, counters and the Needs recalculation chip count for the selected year only»):
/// застарілість результатів за РІК без періоду — документ застарілий за рік, якщо застарілий хоч в одному періоді року.
/// Фільтр <c>resultsStale</c>, позначка рядка (з найранішим «відколи») і лічильники зведення року дають одне й те саме
/// на справжній базі, а проєкти зі звуженням виключені в обох напрямках.
/// </summary>
/// <remarks>
/// Мутаційний доказ (локально, 08.11): у <c>StaleResultsYear.GetAsync</c> об'єднувати лише ПЕРШИЙ період року
/// (<c>periodKeys.Take(1)</c>) — документ, застарілий лише в лютому, випадає з фільтра, позначки й лічильника:
/// <see cref="Рік_об_єднує_місяці_фільтр_позначка_і_лічильник_збігаються"/> червоний; у <c>StaleResultsYear.Documents</c>
/// брати лише перший період — <see cref="Переповнена_карта_року_дає_те_саме_прямим_запитом"/> червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentYearStaleResultsStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    private const int Me = 71;
    private const int Other = 72;

    /// <summary>Сцена: два періоди року з актуальними прогонами; A застарілий у січні й лютому, B — лише в лютому.</summary>
    private sealed record Scene(
        TestDocumentBuilder Builder, TestDocument Chain, int Year, long StaleJanuary, long StaleFebruary, long Fresh);

    private async Task<Scene> SceneAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(periodKey: 202601, ct: CancellationToken.None);
        var year = chain.PeriodKey.Year;
        var january = chain.PeriodKey.Value;
        var february = year * 100 + 2;
        await using var db = builder.CreateContext();

        if (!await db.Periods.AnyAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == february))
        {
            db.Periods.Add(new Period(
                chain.ProjectId, new PeriodKey(february), 2, new DateOnly(year, 2, 1), new DateOnly(year, 2, 28)));
            await db.SaveChangesAsync();
        }

        var a = await AddDocumentAsync(db, chain, "YSA");
        var b = await AddDocumentAsync(db, chain, "YSB");
        var fresh = await AddDocumentAsync(db, chain, "YSF");
        var version = await VersionAsync(db);
        await RunAsync(db, chain, january, version, Now, [a, b, fresh]);
        await RunAsync(db, chain, february, version, Now, [a, b, fresh]);

        var column = chain.ColumnDefIds[1];

        // A: січень +1 год і лютий +30 хв (найраніше «відколи» за рік — лютневе); обидві правки — мої.
        await EditAsync(a, january, column, Now.AddHours(1), Me);
        await EditAsync(a, february, column, Now.AddMinutes(30), Me);

        // B: лише лютий, чужа правка. Січень B свіжий — за місяць січня він НЕ застарілий.
        await EditAsync(b, february, column, Now.AddHours(2), Other);

        return new Scene(builder, chain, year, a, b, fresh);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рік_об_єднує_місяці_фільтр_позначка_і_лічильник_збігаються()
    {
        var s = await SceneAsync();
        await using var db = s.Builder.CreateContext();
        var store = new DocumentStore(db);

        // Контроль: за січень B не застарілий — рік дає більше, ніж будь-який один місяць.
        var januaryOnly = await IdsAsync(store, s, new DocumentListFilter(null, null, ResultsStale: true), s.Chain.PeriodKey.Value);
        Assert.Equal(new[] { s.StaleJanuary }, januaryOnly);

        Assert.Equal(
            new[] { s.StaleJanuary, s.StaleFebruary }.Order(),
            await IdsAsync(store, s, new DocumentListFilter(null, null, ResultsStale: true, Year: s.Year)));
        Assert.Equal(
            new[] { s.Chain.DocumentId, s.Fresh }.Order(),
            await IdsAsync(store, s, new DocumentListFilter(null, null, ResultsStale: false, Year: s.Year)));
        Assert.Equal(
            new[] { s.StaleJanuary },
            await IdsAsync(store, s, new DocumentListFilter(null, null, ResultsStale: true, StaleByUserId: Me, Year: s.Year)));

        // Позначка рядка за рік без фільтра: true/false (не null), «відколи» — найраніший момент серед місяців.
        var page = await PageAsync(store, s, new DocumentListFilter(null, null, Year: s.Year));
        var a = page.Single(d => d.Id == s.StaleJanuary);
        Assert.True(a.ResultsStale);
        Assert.True(Math.Abs((a.ResultsStaleSince!.Value - Now.AddMinutes(30)).TotalSeconds) < 1, $"{a.ResultsStaleSince:O}");
        Assert.Equal(DateTimeKind.Utc, a.ResultsStaleSince!.Value.Kind);
        var b = page.Single(d => d.Id == s.StaleFebruary);
        Assert.True(b.ResultsStale);
        Assert.True(Math.Abs((b.ResultsStaleSince!.Value - Now.AddHours(2)).TotalSeconds) < 1, $"{b.ResultsStaleSince:O}");
        Assert.False(page.Single(d => d.Id == s.Fresh).ResultsStale);
        Assert.Null(page.Single(d => d.Id == s.Fresh).ResultsStaleSince);
        Assert.False(page.Single(d => d.Id == s.Chain.DocumentId).ResultsStale);

        // Лічильники зведення року — те саме число, що й фільтр.
        var summary = await new DocumentListSummaryStore(db).SummarizeYearAsync(
            s.Chain.ProjectId, s.Year, [s.Chain.ProjectId], restrictions: null, Me, CancellationToken.None);
        Assert.Equal((2, 1), (summary.StaleResultsCount, summary.StaleResultsMineCount));

        var anonymous = await new DocumentListSummaryStore(db).SummarizeYearAsync(
            s.Chain.ProjectId, s.Year, [s.Chain.ProjectId], restrictions: null, currentUserId: null, CancellationToken.None);
        Assert.Equal((2, 0), (anonymous.StaleResultsCount, anonymous.StaleResultsMineCount));

        // Рік, у якому проєкт не має періодів, — нулі (та сама межа, що й решта зведення року).
        var empty = await new DocumentListSummaryStore(db).SummarizeYearAsync(
            s.Chain.ProjectId, s.Year - 2, [s.Chain.ProjectId], restrictions: null, Me, CancellationToken.None);
        Assert.Equal((0, 0), (empty.StaleResultsCount, empty.StaleResultsMineCount));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Проєкт_зі_звуженням_поза_фільтром_року_в_обох_напрямках_і_поза_лічильником()
    {
        var s = await SceneAsync();
        await using var db = s.Builder.CreateContext();
        var store = new DocumentStore(db);

        foreach (var want in new[] { true, false })
        {
            Assert.Empty(await IdsAsync(
                store, s, new DocumentListFilter(null, null, ResultsStale: want, NarrowedProjectIds: [s.Chain.ProjectId], Year: s.Year)));
        }

        var narrowed = await new DocumentListSummaryStore(db).SummarizeYearAsync(
            s.Chain.ProjectId, s.Year, [s.Chain.ProjectId], new SummaryRestrictions([], [s.Chain.ProjectId]), Me,
            CancellationToken.None);
        Assert.Equal((0, 0), (narrowed.StaleResultsCount, narrowed.StaleResultsMineCount));

        // Межа грантів: проєкт поза видимими — нуль.
        var foreign = await new DocumentListSummaryStore(db).SummarizeYearAsync(
            s.Chain.ProjectId, s.Year, [s.Chain.ProjectId + 100_000], restrictions: null, Me, CancellationToken.None);
        Assert.Equal(0, foreign.StaleResultsCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Переповнена_карта_року_дає_те_саме_прямим_запитом()
    {
        var s = await SceneAsync();
        await using var db = s.Builder.CreateContext();

        // Стеля 0: кожна місячна карта «переповнена» — фільтр, позначка й лічильник ідуть прямими запитами по місяцях.
        var store = new DocumentStore(db) { StaleMapMaxDocuments = 0 };
        Assert.Equal(
            new[] { s.StaleJanuary, s.StaleFebruary }.Order(),
            await IdsAsync(store, s, new DocumentListFilter(null, null, ResultsStale: true, Year: s.Year)));
        Assert.Equal(
            new[] { s.Chain.DocumentId, s.Fresh }.Order(),
            await IdsAsync(store, s, new DocumentListFilter(null, null, ResultsStale: false, Year: s.Year)));
        Assert.Equal(
            new[] { s.StaleJanuary },
            await IdsAsync(store, s, new DocumentListFilter(null, null, ResultsStale: true, StaleByUserId: Me, Year: s.Year)));

        var page = await PageAsync(store, s, new DocumentListFilter(null, null, Year: s.Year));
        var a = page.Single(d => d.Id == s.StaleJanuary);
        Assert.True(a.ResultsStale);
        Assert.True(Math.Abs((a.ResultsStaleSince!.Value - Now.AddMinutes(30)).TotalSeconds) < 1, $"{a.ResultsStaleSince:O}");
        Assert.True(page.Single(d => d.Id == s.StaleFebruary).ResultsStale);
        Assert.False(page.Single(d => d.Id == s.Fresh).ResultsStale);

        var summary = await new DocumentListSummaryStore(db) { StaleMapMaxDocuments = 0 }.SummarizeYearAsync(
            s.Chain.ProjectId, s.Year, [s.Chain.ProjectId], restrictions: null, Me, CancellationToken.None);
        Assert.Equal((2, 1), (summary.StaleResultsCount, summary.StaleResultsMineCount));
    }

    // ── допоміжне ────────────────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<DocumentSummary>> PageAsync(
        DocumentStore store, Scene s, DocumentListFilter filter, int? periodKey = null)
        => (await store.ListAsync(
            s.Chain.ProjectId, new PeriodKeyFilter(periodKey), filter, new CursorRequest(Limit: 200),
            visibleProjectIds: null, CancellationToken.None)).Items;

    private static async Task<long[]> IdsAsync(DocumentStore store, Scene s, DocumentListFilter filter, int? periodKey = null)
        => [.. (await PageAsync(store, s, filter, periodKey)).Select(d => d.Id).Order()];

    private static async Task<long> AddDocumentAsync(EcrDbContext db, TestDocument chain, string key)
    {
        var document = new Document(chain.ProjectId, $"{key}-{chain.ProjectId}", 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync(CancellationToken.None);
        return document.Id;
    }

    /// <summary>Нова методологія з однією версією (код унікальний у базі).</summary>
    private static async Task<int> VersionAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var methodology = new Methodology(EcrCode.Create($"YM_{tag}"), Name("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);
        return version.Id;
    }

    /// <summary>Актуальний прогін періоду <paramref name="periodKey"/>, що дав число кожному з документів.</summary>
    private static async Task RunAsync(
        EcrDbContext db, TestDocument chain, int periodKey, int versionId, DateTime startedAt, long[] documents)
    {
        var run = new CalculationRun(chain.ProjectId, periodKey, triggeredByUserId: null, startedAt, null);
        run.Complete("Succeeded", startedAt.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync(CancellationToken.None);
        foreach (var documentId in documents)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO calc.CalculationResult
                    (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
                VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {versionId},
                        {periodKey}, {documentId}, N'row-1', N'E_CO2', CAST(1 AS decimal(34,16)), {unitId})
                """);
        }
    }

    /// <summary>Рядок журналу — прямим ADO: <c>aud.*</c> поза моделлю EF.</summary>
    private async Task EditAsync(long documentId, int periodKey, int columnDefId, DateTime changedAt, int user)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES (@at, @period, @document, 1, N'R1', @column, N'1', N'2', @user, N'UserEdit', 0);
            """;
        command.Parameters.AddWithValue("@at", changedAt);
        command.Parameters.AddWithValue("@period", periodKey);
        command.Parameters.AddWithValue("@document", documentId);
        command.Parameters.AddWithValue("@column", columnDefId);
        command.Parameters.AddWithValue("@user", user);

        await command.ExecuteNonQueryAsync();
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
