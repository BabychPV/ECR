// tests/Ecr.Infrastructure.Tests/Persistence/Period0SupersedeSeedTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Секція <c>COLL:period0-supersede</c> у <c>09-seed.sql</c>: старі річні прогони
/// нічного перерахунку, що лишили результати в «періоді 0», стають
/// <c>Superseded</c> (✎ P-1124-3: перевірку через річний зріз звітності прибрано разом зі зрізами).
/// </summary>
/// <remarks>
/// ⛔ Скрипт виконується тим самим шляхом, що й у розгортанні: <see cref="SeedRunner"/>
/// (його кличе <c>StartupSequence</c> на кожному старті застосунку; DDL-скрипти
/// <c>01…15</c> DML не несуть — <c>02-contracts.md</c> §14).
///
/// ⚠ Старий стан засівається СПРАВЖНІМ <see cref="CalculationResultStore"/>:
/// прогін без періоду й досі пишеться в <c>run.PeriodKey ?? 0</c>, тобто рівно так,
/// як до фіксу 44c952c1 писав нічний перерахунок.
///
/// Мутація: прибрати <c>run.PeriodKey IS NULL</c> з умови UPDATE — поперіодний прогін
/// із рядком у періоді 0 стає <c>Superseded</c>, тест «не чіпає» червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class Period0SupersedeSeedTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Старий_річний_прогін_з_результатами_періоду_0_стає_Superseded()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        await using var db = chain.CreateContext();
        var (versionId, unitId) = await MethodologyAsync(db);
        var store = new CalculationResultStore(db, new TestClock(Now));

        // Старий стан: річний прогін нічного перерахунку, `Current`, два числа в періоді 0.
        var yearRun = await CurrentRunAsync(db, document.ProjectId, periodKey: null);
        await WriteAsync(db, store, yearRun, document.DocumentId, versionId, unitId, "Y-1", "Y-2");

        // І звичайний поперіодний прогін січня.
        var januaryRun = await CurrentRunAsync(db, document.ProjectId, document.PeriodKey.Value);
        await WriteAsync(db, store, januaryRun, document.DocumentId, versionId, unitId, "J-1");

        Assert.Equal(
            2,
            await db.CalculationResults.AsNoTracking().CountAsync(r => r.CalculationRunId == yearRun && r.PeriodKey == 0));

        var messages = await RunSeedAsync();

        // Скрипт звітує кількість у журнал; щонайменше наш прогін.
        var printed = Assert.Single(messages, m => m.StartsWith("period0-supersede: ", StringComparison.Ordinal));
        Assert.True(RowsOf(printed) >= 1, printed);

        Assert.Equal(CalculationRun.SupersededStatus, await StatusAsync(yearRun));
        Assert.Equal(CalculationRun.CurrentStatus, await StatusAsync(januaryRun));

        // ⛔ Не видалено нічого: історія лишається (рішення HSE301).
        Assert.Equal(
            2,
            await db.CalculationResults.AsNoTracking().CountAsync(r => r.CalculationRunId == yearRun));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Повторний_запуск_скрипта_не_змінює_нічого()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        await using var db = chain.CreateContext();
        var (versionId, unitId) = await MethodologyAsync(db);
        var store = new CalculationResultStore(db, new TestClock(Now));

        var yearRun = await CurrentRunAsync(db, document.ProjectId, periodKey: null);
        await WriteAsync(db, store, yearRun, document.DocumentId, versionId, unitId, "Y-1");
        var januaryRun = await CurrentRunAsync(db, document.ProjectId, document.PeriodKey.Value);
        await WriteAsync(db, store, januaryRun, document.DocumentId, versionId, unitId, "J-1");

        await RunSeedAsync();
        var first = (await StatusAsync(yearRun), await StatusAsync(januaryRun));

        var messages = await RunSeedAsync();

        // Другий запуск: нуль змінених рядків, стани ті самі.
        Assert.Equal("period0-supersede: 0 rows", Assert.Single(
            messages, m => m.StartsWith("period0-supersede: ", StringComparison.Ordinal)));
        Assert.Equal(first, (await StatusAsync(yearRun), await StatusAsync(januaryRun)));
        Assert.Equal((CalculationRun.SupersededStatus, CalculationRun.CurrentStatus), first);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Скрипт_не_чіпає_поперіодні_й_нові_порожні_річні_прогони()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        await using var db = chain.CreateContext();
        var (versionId, unitId) = await MethodologyAsync(db);
        var store = new CalculationResultStore(db, new TestClock(Now));

        // Поперіодний прогін — стан після фіксу 44c952c1.
        var januaryRun = await CurrentRunAsync(db, document.ProjectId, document.PeriodKey.Value);
        await WriteAsync(db, store, januaryRun, document.DocumentId, versionId, unitId, "J-1", "J-2");

        // ⛔ Поперіодний прогін, у якого з будь-якої причини Є рядок у періоді 0.
        // Скрипт знімає актуальність ЛИШЕ з річних прогонів (`PeriodKey IS NULL`);
        // саме цей рядок і робить ту умову перевіреною, а не декоративною.
        var februaryRun = await CurrentRunAsync(db, document.ProjectId, 202602);
        await StrayPeriodZeroRowAsync(db, februaryRun, document.DocumentId, versionId, unitId);

        // Річний прогін після фіксу: «рік без періодів у скоупі» — без результатів.
        var emptyYearRun = await CurrentRunAsync(db, document.ProjectId, periodKey: null);

        await RunSeedAsync();

        Assert.Equal(CalculationRun.CurrentStatus, await StatusAsync(januaryRun));
        Assert.Equal(CalculationRun.CurrentStatus, await StatusAsync(februaryRun));
        Assert.Equal(CalculationRun.CurrentStatus, await StatusAsync(emptyYearRun));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кількість_змінених_рядків_потрапляє_в_журнал_старту()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        await using (var arrange = chain.CreateContext())
        {
            var (versionId, unitId) = await MethodologyAsync(arrange);
            var store = new CalculationResultStore(arrange, new TestClock(Now));
            var yearRun = await CurrentRunAsync(arrange, document.ProjectId, periodKey: null);
            await WriteAsync(arrange, store, yearRun, document.DocumentId, versionId, unitId, "Y-1");
        }

        var logger = new RecordingLogger<SeedRunner>();

        await using (var db = chain.CreateContext())
        {
            await new SeedRunner(db, logger).RunAsync(CancellationToken.None);
        }

        // ⛔ Саме Information і саме текст PRINT: застосунок виконує сід на кожному
        // старті, і інакше число з одноразового виправлення даних не бачив би ніхто.
        const string Prefix = "period0-supersede: ";
        var line = Assert.Single(
            logger.OfLevel(Microsoft.Extensions.Logging.LogLevel.Information),
            r => r.Message.Contains(Prefix, StringComparison.Ordinal)).Message;
        Assert.True(RowsOf(line[line.IndexOf(Prefix, StringComparison.Ordinal)..]) >= 1, line);
    }

    /// <summary>Виконує seed — той самий шлях, що й старт застосунку; повертає PRINT-и.</summary>
    private async Task<List<string>> RunSeedAsync()
    {
        var messages = new List<string>();

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);

        await new SeedRunner(db).RunAsync(CancellationToken.None);

        return messages;
    }

    private static int RowsOf(string message)
        => int.Parse(
            message["period0-supersede: ".Length..].Split(' ')[0],
            System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> StatusAsync(long runId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.CalculationRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
    }

    private static async Task<long> CurrentRunAsync(EcrDbContext db, int projectId, int? periodKey)
    {
        var run = new CalculationRun(projectId, periodKey, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now, "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        return run.Id;
    }

    private static async Task WriteAsync(
        EcrDbContext db, CalculationResultStore store, long runId, long documentId,
        int versionId, int unitId, params string[] rowKeys)
    {
        await store.WriteResultsAsync(
            runId,
            [.. rowKeys.Select(key => new CalculationOutput(
                documentId, key, [new CalculationOutputValue(versionId, null, "tons", 1.5m, unitId)], []))],
            CancellationToken.None);
        await db.SaveChangesAsync();
    }

    /// <summary>Рядок у періоді 0 для прогону з КОНКРЕТНИМ періодом — повз сховище.</summary>
    private static Task<int> StrayPeriodZeroRowAsync(
        EcrDbContext db, long runId, long documentId, int versionId, int unitId)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT calc.CalculationResult
                (PeriodKey, Id, CalculationRunId, MethodologyVersionId, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            VALUES
                (0, NEXT VALUE FOR calc.CalculationResultSeq, {runId}, {versionId}, {documentId}, N'F-0', N'tons', 2.5, {unitId});
            """);

    private static async Task<(int VersionId, int UnitId)> MethodologyAsync(EcrDbContext db)
    {
        var methodology = new Methodology(
            EcrCode.Create($"P0S_{Guid.NewGuid().ToString("N")[..8]}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        return (version.Id, unit);
    }
}
