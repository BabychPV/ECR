using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
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
/// «Результати застаріли» у переліку документів (<c>resultsStale</c>): позначка в рядку, фільтр до стелі сторінки
/// і лічильники <c>/documents/summary</c> компонують ОДИН предикат (<c>StaleResultsQuery</c>) на справжній базі.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>Origin &lt;&gt; 'Recalculation'</c> →
/// <see cref="Перерахунок_правка_до_прогону_чужий_період_і_документ_без_результатів_не_застарілі"/> червоний;
/// замінити <c>MAX(r0.Id)</c> на <c>MIN</c> → <see cref="Береться_найновіший_актуальний_прогін_документа"/> червоний;
/// прибрати <c>!narrowed.Contains</c> у <c>DocumentStore.ListAsync</c> →
/// <see cref="Проєкти_зі_звуженням_виключені_з_фільтра_в_обох_напрямках"/> червоний;
/// прибрати <c>c.ChangedByUserId = by</c> → <see cref="Фільтр_за_автором_правки_бачить_лише_його_правки"/> червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentStaleResultsStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_входу_після_прогону_робить_застарілим_а_повторний_прогін_знімає()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var version = await VersionAsync(db);
        var run = await RunAsync(db, chain, version, Now, [chain.DocumentId]);

        Assert.False((await CardAsync(db, chain, period)).ResultsStale);

        var editedAt = Now.AddHours(1);
        await EditAsync(chain.DocumentId, period, chain.ColumnDefIds[1], editedAt);

        var stale = await CardAsync(db, chain, period);
        Assert.True(stale.ResultsStale);
        Assert.NotNull(stale.ResultsStaleSince);
        Assert.True(Math.Abs((stale.ResultsStaleSince!.Value - editedAt).TotalSeconds) < 1, $"{stale.ResultsStaleSince:O}");

        // Повторний прогін (F9): старий знято, новий стартує ПІСЛЯ правки.
        run.Supersede();
        await db.SaveChangesAsync(CancellationToken.None);
        await RunAsync(db, chain, version, Now.AddHours(2), [chain.DocumentId]);

        var fresh = await CardAsync(db, chain, period);
        Assert.False(fresh.ResultsStale);
        Assert.Null(fresh.ResultsStaleSince);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перерахунок_правка_до_прогону_чужий_період_і_документ_без_результатів_не_застарілі()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var recalculated = await AddDocumentAsync(db, chain, "ST-RECALC");
        var before = await AddDocumentAsync(db, chain, "ST-BEFORE");
        var otherPeriod = await AddDocumentAsync(db, chain, "ST-OTHERP");
        var noResults = await AddDocumentAsync(db, chain, "ST-NORES");

        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [recalculated, before, otherPeriod]);

        var column = chain.ColumnDefIds[1];
        await EditAsync(recalculated, period, column, Now.AddHours(1), origin: "Recalculation");
        await EditAsync(before, period, column, Now.AddHours(-1));
        await EditAsync(otherPeriod, period + 1, column, Now.AddHours(1));
        await EditAsync(noResults, period, column, Now.AddHours(1));

        var page = await ListAsync(db, chain, period, default);

        // ⛔ Не `null`: період відомий, результатів у документа просто немає — це «не застарілі», а не «невідомо».
        foreach (var id in new[] { recalculated, before, otherPeriod, noResults })
        {
            Assert.False(page.Single(d => d.Id == id).ResultsStale, $"документ {id}");
        }

        // Без періоду застарілість не визначена: `null`, а не `false`.
        var noPeriod = await new DocumentStore(db).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(null), default, new CursorRequest(Limit: 50),
            visibleProjectIds: null, CancellationToken.None);
        Assert.All(noPeriod.Items, d => Assert.Null(d.ResultsStale));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Береться_найновіший_актуальний_прогін_документа()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var version = await VersionAsync(db);

        // Дві актуальні області того самого проєкту й періоду: прогін усього проєкту (раніший) і прогін ЛИШЕ цього
        // документа (пізніший). Уніфікований індекс допускає обидва - різний DocumentId.
        await RunAsync(db, chain, version, Now, [chain.DocumentId]);
        await RunAsync(db, chain, version, Now.AddHours(1), [chain.DocumentId], runDocumentId: chain.DocumentId);

        // Правка між двома стартами: проти раннього прогону це «застаріло», проти найновішого - ні.
        await EditAsync(chain.DocumentId, period, chain.ColumnDefIds[1], Now.AddMinutes(30));
        Assert.False((await CardAsync(db, chain, period)).ResultsStale);

        await EditAsync(chain.DocumentId, period, chain.ColumnDefIds[1], Now.AddMinutes(90));
        Assert.True((await CardAsync(db, chain, period)).ResultsStale);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_стоїть_у_запиті_до_стелі_сторінки_і_перетинається_з_пошуком()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var staleIds = new List<long>();
        var freshIds = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            freshIds.Add(await AddDocumentAsync(db, chain, $"STF{tag}-F{i}"));
            staleIds.Add(await AddDocumentAsync(db, chain, $"STF{tag}-S{i}"));
        }

        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [.. staleIds, .. freshIds]);
        foreach (var id in staleIds)
        {
            await EditAsync(id, period, chain.ColumnDefIds[1], Now.AddHours(1));
        }

        // По одному документу на сторінку: фільтр ДО `Take` проходить рівно застарілі, без порожніх сторінок.
        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var page = await new DocumentStore(db).ListAsync(
                chain.ProjectId, new PeriodKeyFilter(period),
                new DocumentListFilter(null, null, ResultsStale: true, Query: $"STF{tag}"),
                new CursorRequest(Limit: 1, cursor), visibleProjectIds: null, CancellationToken.None);

            Assert.Single(page.Items);
            Assert.True(page.Items[0].ResultsStale);
            seen.AddRange(page.Items.Select(d => d.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(staleIds.Order(), seen.Order());

        var notStale = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: false, Query: $"STF{tag}"));
        Assert.Equal(freshIds.Order(), notStale.Select(d => d.Id).Order());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Проєкти_зі_звуженням_виключені_з_фільтра_в_обох_напрямках()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [chain.DocumentId]);
        await EditAsync(chain.DocumentId, period, chain.ColumnDefIds[1], Now.AddHours(1));

        // Контроль: читач без звуження бачить документ у «застарілих».
        var open = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true));
        Assert.Contains(open, d => d.Id == chain.DocumentId);

        // ⛔ Звужений: ні `true`, ні `false` не повертають документ - інакше «не застарілі» = решта розкриває його.
        foreach (var want in new[] { true, false })
        {
            var narrowed = await ListAsync(
                db, chain, period, new DocumentListFilter(null, null, ResultsStale: want, NarrowedProjectIds: [chain.ProjectId]));
            Assert.Empty(narrowed);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_за_автором_правки_бачить_лише_його_правки()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var byMe = await AddDocumentAsync(db, chain, "ST-BYME");
        var byOther = await AddDocumentAsync(db, chain, "ST-BYOTHER");
        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [byMe, byOther]);

        const int me = 41;
        const int other = 42;
        await EditAsync(byMe, period, chain.ColumnDefIds[1], Now.AddHours(1), user: me);
        await EditAsync(byOther, period, chain.ColumnDefIds[1], Now.AddHours(1), user: other);

        var mine = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true, StaleByUserId: me));
        Assert.Equal(new[] { byMe }, mine.Select(d => d.Id).ToArray());

        var all = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true));
        Assert.Equal(new[] { byMe, byOther }.Order(), all.Select(d => d.Id).Order());

        var nobody = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true, StaleByUserId: 43));
        Assert.Empty(nobody);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_довідника_який_читає_методологія_застарює_документ_але_не_є_правкою_користувача()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var registry = new RegistryDef(EcrCode.Create($"SR{tag}"), Name("Registry"), isTemporal: false);
        registry.MarkDataChanged(Now.AddHours(1));
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = await VersionAsync(db);
        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(version, "F1", registry.Id, "X"));
        await db.SaveChangesAsync(CancellationToken.None);
        await RunAsync(db, chain, version, Now, [chain.DocumentId]);

        var stale = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true));
        Assert.Contains(stale, d => d.Id == chain.DocumentId);

        // Довідник змінила не людина з журналу комірок - «моїх» це не стосується.
        var mine = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true, StaleByUserId: 1));
        Assert.DoesNotContain(mine, d => d.Id == chain.DocumentId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лічильники_зведення_збігаються_з_фільтром_і_не_рахують_звужені_проєкти()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var byMe = await AddDocumentAsync(db, chain, "SC-BYME");
        var byOther = await AddDocumentAsync(db, chain, "SC-BYOTHER");
        var fresh = await AddDocumentAsync(db, chain, "SC-FRESH");
        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [byMe, byOther, fresh]);

        const int me = 51;
        await EditAsync(byMe, period, chain.ColumnDefIds[1], Now.AddHours(1), user: me);
        await EditAsync(byOther, period, chain.ColumnDefIds[1], Now.AddHours(1), user: 52);

        var store = new DocumentListSummaryStore(db);
        var summary = await store.SummarizeAsync(
            chain.ProjectId, period, [chain.ProjectId], restrictions: null, me, CancellationToken.None);

        var filtered = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true));
        Assert.Equal(2, summary.StaleResultsCount);
        Assert.Equal(filtered.Count, summary.StaleResultsCount);
        Assert.Equal(1, summary.StaleResultsMineCount);

        // Без користувача «моїх» немає, а загальне число те саме.
        var anonymous = await store.SummarizeAsync(
            chain.ProjectId, period, [chain.ProjectId], restrictions: null, currentUserId: null, CancellationToken.None);
        Assert.Equal(2, anonymous.StaleResultsCount);
        Assert.Equal(0, anonymous.StaleResultsMineCount);

        // ⛔ Проєкт зі звуженням не рахується: число по всьому документу розкрило б правку схованого входу.
        var narrowed = await store.SummarizeAsync(
            chain.ProjectId, period, [chain.ProjectId], new SummaryRestrictions([], [chain.ProjectId]), me, CancellationToken.None);
        Assert.Equal(0, narrowed.StaleResultsCount);
        Assert.Equal(0, narrowed.StaleResultsMineCount);

        // Чужий проєкт з тими самими даними поза межею грантів не потрапляє в число.
        var foreign = await builder.BuildAsync(ct: CancellationToken.None);
        await RunAsync(db, foreign, version, Now, [foreign.DocumentId]);
        await EditAsync(foreign.DocumentId, foreign.PeriodKey.Value, foreign.ColumnDefIds[1], Now.AddHours(1), user: me);
        var scoped = await store.SummarizeAsync(
            null, period, [chain.ProjectId], restrictions: null, me, CancellationToken.None);
        Assert.Equal(2, scoped.StaleResultsCount);
    }

    // ── допоміжне ────────────────────────────────────────────────────────────────────────

    private static async Task<DocumentSummary> CardAsync(EcrDbContext db, TestDocument chain, int period)
        => (await new DocumentStore(db).FindAsync(chain.DocumentId, new PeriodKeyFilter(period), CancellationToken.None))!;

    private static async Task<IReadOnlyList<DocumentSummary>> ListAsync(
        EcrDbContext db, TestDocument chain, int period, DocumentListFilter filter)
        => (await new DocumentStore(db).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(period), filter, new CursorRequest(Limit: 200),
            visibleProjectIds: null, CancellationToken.None)).Items;

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
        var methodology = new Methodology(EcrCode.Create($"SM_{tag}"), Name("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);
        return version.Id;
    }

    /// <summary>Актуальний прогін, що стартував о <paramref name="startedAt"/> і дав число кожному з документів.</summary>
    private static async Task<CalculationRun> RunAsync(
        EcrDbContext db, TestDocument chain, int versionId, DateTime startedAt, long[] documents,
        long? runDocumentId = null)
    {
        var run = new CalculationRun(
            chain.ProjectId, chain.PeriodKey.Value, triggeredByUserId: null, startedAt, runDocumentId);
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
                        {chain.PeriodKey.Value}, {documentId}, N'row-1', N'E_CO2', CAST(1 AS decimal(34,16)), {unitId})
                """);
        }

        return run;
    }

    /// <summary>Рядок журналу — прямим ADO: <c>aud.*</c> поза моделлю EF.</summary>
    private async Task EditAsync(
        long documentId, int periodKey, int columnDefId, DateTime changedAt, int user = 1, string origin = "UserEdit")
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES (@at, @period, @document, 1, N'R1', @column, N'1', N'2', @user, @origin, 0);
            """;
        command.Parameters.AddWithValue("@at", changedAt);
        command.Parameters.AddWithValue("@period", periodKey);
        command.Parameters.AddWithValue("@document", documentId);
        command.Parameters.AddWithValue("@column", columnDefId);
        command.Parameters.AddWithValue("@user", user);
        command.Parameters.AddWithValue("@origin", origin);

        await command.ExecuteNonQueryAsync();
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
