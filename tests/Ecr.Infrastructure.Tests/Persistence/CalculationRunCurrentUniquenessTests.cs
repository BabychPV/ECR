// tests/Ecr.Infrastructure.Tests/Persistence/CalculationRunCurrentUniquenessTests.cs
using System.Globalization;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// «Актуальний прогін на область — щонайбільше один» на РЕАЛЬНОМУ SQL Server
/// (ФВ-9.11).
/// </summary>
/// <remarks>
/// ⛔ До <c>UX_CalculationRun_Current</c> цей інваріант не тримало НІЩО: ні
/// блокування, ні <c>rowversion</c>, ні унікальний індекс. <c>SwitchCurrentRunAsync</c>
/// знімає актуальність зі старих прогонів за ЗНІМКОМ, прочитаним на початку
/// власної транзакції (TOCTOU, той самий клас, що Q-241/Q-245), тож два
/// одночасні завершення прогонів одного проєкту й періоду ОБИДВА не бачать
/// одне одного, обидва комітяться — і в <c>calc.CalculationRun</c> лишаються
/// ДВА рядки зі <c>Status = 'Current'</c>.
///
/// ⚠ Наслідок не падає, а бреше: <c>ReadCurrentAsync</c> добирає результати
/// підзапитом <c>EXISTS (… Status = 'Current')</c>, тобто повертає ОБ'ЄДНАННЯ
/// двох прогонів — кожне число документа двічі, за двома різними версіями
/// методології. Звіт при цьому будується, не кидає жодної помилки і показує
/// подвоєні викиди.
///
/// ⚠ Гонитва відтворюється ДЕТЕРМІНОВАНО — двома контекстами, а не реальним
/// паралелізмом (той самий підхід, що <c>RowStoreRaceTests</c> і
/// <c>RegistryEntryDuplicateRaceTests</c>): справжні потоки дали б флакі-тест,
/// а перевіряти треба сам конфлікт, а не планувальник ОС.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationRunCurrentUniquenessTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Два_одночасні_перемикання_лишають_рівно_один_актуальний_прогін()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        var periodKey = document.PeriodKey.Value;

        long firstId;
        long secondId;

        await using (var seed = chain.CreateContext())
        {
            // Два завершені прогони ОДНОГО проєкту й періоду: саме так виглядає
            // перерахунок, який поставили в чергу двічі (ручний і за розкладом).
            var first = new CalculationRun(document.ProjectId, periodKey, triggeredByUserId: null, Now);
            var second = new CalculationRun(document.ProjectId, periodKey, triggeredByUserId: null, Now);
            seed.CalculationRuns.Add(first);
            seed.CalculationRuns.Add(second);
            await seed.SaveChangesAsync(CancellationToken.None);

            firstId = first.Id;
            secondId = second.Id;
        }

        // Два ОКРЕМІ контексти — два воркери, кожен зі своїм знімком. Обидва
        // читають стан, у якому актуального прогону ще НЕМАЄ, тож жоден із них
        // не має кого знімати з актуальності.
        await using var winner = chain.CreateContext();
        await using var loser = chain.CreateContext();

        await new CalculationResultStore(winner, new TestClock(Now))
            .SwitchCurrentRunAsync(firstId, "{}", CancellationToken.None);
        await new CalculationResultStore(loser, new TestClock(Now))
            .SwitchCurrentRunAsync(secondId, "{}", CancellationToken.None);

        await winner.SaveChangesAsync(CancellationToken.None);

        // ⛔ Головна перевірка: другий коміт мусить ВІДМОВИТИ на рівні БАЗИ.
        // Доти він проходив мовчки, і саме тут народжувався звіт із подвоєними
        // числами — помилки не було видно ніде.
        await Assert.ThrowsAsync<DbUpdateException>(
            () => loser.SaveChangesAsync(CancellationToken.None));

        await using var check = chain.CreateContext();
        var current = await check.CalculationRuns
            .AsNoTracking()
            .CountAsync(r => r.ProjectId == document.ProjectId
                             && r.PeriodKey == periodKey
                             && r.Status == CalculationRun.CurrentStatus);

        Assert.Equal(1, current);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Актуальні_прогони_різних_періодів_і_річний_співіснують()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        await using var db = chain.CreateContext();

        // ⚠ Межа індексу перевіряється навмисно: надто широкий
        // (`UNIQUE (ProjectId)` або індекс без фільтра по `Status`) зробив би
        // законні стани неможливими — а це гірше за дефект, бо ламало б
        // кожен другий перерахунок. Область актуальності — ПАРА
        // «проєкт × період», і річний прогін (`PeriodKey = NULL`) — своя,
        // окрема область, а не «будь-який період».
        var january = Current(db, document.ProjectId, document.PeriodKey.Value);
        var february = Current(db, document.ProjectId, document.PeriodKey.Value + 1);
        var annual = Current(db, document.ProjectId, periodKey: null);

        // Знятий з актуальності прогін лишається читабельним поруч із чинним:
        // фільтр індексу по `Status` — саме про це (ЗБР-1, «нічого не
        // затирається»).
        var superseded = new CalculationRun(document.ProjectId, document.PeriodKey.Value, null, Now);
        superseded.Complete("Succeeded", Now, "{}", errorMessage: null);
        superseded.Supersede();
        db.CalculationRuns.Add(superseded);

        await db.SaveChangesAsync(CancellationToken.None);

        Assert.True(january.IsCurrent);
        Assert.True(february.IsCurrent);
        Assert.True(annual.IsCurrent);
        Assert.Equal(CalculationRun.SupersededStatus, superseded.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "CalcRunDocumentScope")]
    public async Task Перерахунок_одного_документа_не_ховає_результати_сусіднього()
    {
        // ⛔ Третя хвиля UX-PASS R4, «CalculationRun ховає результати сусідніх
        // документів»: документ A і документ B того самого проєкту й періоду,
        // перераховані ПОСЛІДОВНО — кожен своїм прогоном
        // (`RecalculateDocumentHandler`, `DocumentId` задано). До фіксу
        // `SwitchCurrentRunAsync` документа B знімав актуальність із прогону
        // документа A (обидва мали той самий `(ProjectId, PeriodKey)`), і
        // результати A зникали з `ReadCurrentAsync`, хоча в документі A
        // нічого не змінювалося.
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var documentA = await chain.BuildAsync();
        var periodKey = documentA.PeriodKey.Value;

        await using var db = chain.CreateContext();

        // Документ B — той самий проєкт і період, інший документ.
        var documentB = new Document(documentA.ProjectId, $"DOC-B-{Guid.NewGuid():N}"[..20], 1, Now);
        db.Documents.Add(documentB);
        await db.SaveChangesAsync(CancellationToken.None);

        var methodology = new Methodology(
            EcrCode.Create($"MDOC_{Guid.NewGuid():N}"[..14]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync(CancellationToken.None);

        var methodologyVersion = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(methodologyVersion);
        await db.SaveChangesAsync(CancellationToken.None);

        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).FirstAsync(CancellationToken.None);
        var store = new CalculationResultStore(db, new TestClock(Now));

        // Документ A перераховується ПЕРШИМ — власний документний прогін.
        var runA = new CalculationRun(
            documentA.ProjectId, periodKey, triggeredByUserId: null, Now, documentId: documentA.DocumentId);
        db.CalculationRuns.Add(runA);
        await db.SaveChangesAsync(CancellationToken.None);
        await InsertResultAsync(db, runA.Id, methodologyVersion.Id, periodKey, documentA.DocumentId, unit.Id, "E_CO2", 100m);
        await store.SwitchCurrentRunAsync(runA.Id, "{}", CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        // Документ B перераховується ДРУГИМ — окремий документний прогін
        // того самого проєкту й періоду.
        var runB = new CalculationRun(
            documentA.ProjectId, periodKey, triggeredByUserId: null, Now, documentId: documentB.Id);
        db.CalculationRuns.Add(runB);
        await db.SaveChangesAsync(CancellationToken.None);
        await InsertResultAsync(db, runB.Id, methodologyVersion.Id, periodKey, documentB.Id, unit.Id, "E_NOX", 50m);
        await store.SwitchCurrentRunAsync(runB.Id, "{}", CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ Головна перевірка: прогін документа B НЕ зняв актуальність із
        // прогону документа A — обидва лишаються `Current` одночасно.
        await using var check = chain.CreateContext();
        var statusA = await check.CalculationRuns.AsNoTracking()
            .Where(r => r.Id == runA.Id).Select(r => r.Status).SingleAsync(CancellationToken.None);
        var statusB = await check.CalculationRuns.AsNoTracking()
            .Where(r => r.Id == runB.Id).Select(r => r.Status).SingleAsync(CancellationToken.None);

        Assert.Equal(CalculationRun.CurrentStatus, statusA);
        Assert.Equal(CalculationRun.CurrentStatus, statusB);

        // ⛔ І користувацький симптом: результати документа A досі видно —
        // перерахунок B їх більше не ховає.
        var readStore = new CalculationResultStore(check, new TestClock(Now));
        var resultsA = await readStore.ReadCurrentAsync(documentA.DocumentId, periodKey, CancellationToken.None);
        var resultsB = await readStore.ReadCurrentAsync(documentB.Id, periodKey, CancellationToken.None);

        var rowA = Assert.Single(resultsA);
        Assert.Equal("E_CO2", rowA.OutputCode);
        Assert.Equal(100m, rowA.Value);

        var rowB = Assert.Single(resultsB);
        Assert.Equal("E_NOX", rowB.OutputCode);
        Assert.Equal(50m, rowB.Value);
    }

    /// <summary>
    /// Вставляє один рядок <c>calc.CalculationResult</c> сирим SQL: `Id`
    /// береться з послідовності ДО вставки (як і в <c>CalculationResultStore</c>),
    /// EF його сам не видає.
    /// </summary>
    private static async Task InsertResultAsync(
        EcrDbContext db, long runId, int methodologyVersionId, int periodKey, long documentId,
        int unitId, string outputCode, decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {runId}, {methodologyVersionId},
                    {periodKey}, {documentId}, N'row-1', {outputCode},
                    CAST({text} AS decimal(34,16)), {unitId})
            """);
    }

    private static CalculationRun Current(EcrDbContext db, int projectId, int? periodKey)
    {
        var run = new CalculationRun(projectId, periodKey, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now, "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        return run;
    }
}
