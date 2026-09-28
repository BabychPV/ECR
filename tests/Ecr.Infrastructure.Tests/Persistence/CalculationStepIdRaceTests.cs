// tests/Ecr.Infrastructure.Tests/Persistence/CalculationStepIdRaceTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Ідентифікатори кроків трейсу (<c>calc.CalculationStep.Id</c>) на РЕАЛЬНОМУ
/// SQL Server — без гонки <c>MAX(Id)+1</c>.
/// </summary>
/// <remarks>
/// ⛔ <c>CalculationOrchestrator.RunAsync</c> виконує методології одного
/// пакета ПАРАЛЕЛЬНО (<c>Parallel.ForEachAsync</c>, до 4 гілок), кожна —
/// зі своїм DI-scope і своїм <c>EcrDbContext</c> (Q-249), і всі пишуть трейс
/// того самого прогону в той самий <c>PeriodKey</c>. Доти <c>WriteTraceAsync</c>
/// брав наступний Id як <c>MAX(Id)+1</c> без жодного блокування, а вставка
/// відбувалась аж на <c>SaveChanges</c> у <c>CalculationOutputWriter</c>: між
/// читанням MAX і комітом гілки бачили той самий MAX, і друга падала на
/// <c>PK_CalculationStep (PeriodKey, Id)</c> — разом з усіма РЕЗУЛЬТАТАМИ своєї
/// методології, бо вони в тому самому <c>SaveChanges</c>.
///
/// ⚠ Відтворюється ДЕТЕРМІНОВАНО: усі записувачі спершу резервують Id
/// (<c>Task.WhenAll</c> — бар'єр: жоден не зберігає, доки не зарезервували
/// всі), потім паралельно зберігають. Саме це вікно «прочитав MAX — ще не
/// закомітив» і є в продукті; планувальник ОС тут ні до чого.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationStepIdRaceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    private const int Writers = 8;
    private const int StepsPerWriter = 5;
    private const int Rounds = 3;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "CalcStepIdRace")]
    public async Task Паралельні_записувачі_трейсу_не_зіштовхуються_на_Id()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        var periodKey = document.PeriodKey.Value;

        long runId;
        await using (var seed = chain.CreateContext())
        {
            var run = new CalculationRun(document.ProjectId, periodKey, triggeredByUserId: null, Now);
            seed.CalculationRuns.Add(run);
            await seed.SaveChangesAsync(CancellationToken.None);
            runId = run.Id;
        }

        for (var round = 0; round < Rounds; round++)
        {
            var contexts = Enumerable.Range(0, Writers).Select(_ => chain.CreateContext()).ToList();
            try
            {
                // Фаза 1 — резервування Id: усі гілки пакета одночасно.
                await Task.WhenAll(contexts.Select((db, i) =>
                    new CalculationResultStore(db, new TestClock(Now)).WriteTraceAsync(
                        runId, [Output(document.DocumentId, $"r{round}-w{i}")], TraceLevel.Full,
                        CancellationToken.None)));

                // Фаза 2 — коміт: одночасно, як SaveChanges гілок у CalculationOutputWriter.
                var saves = contexts.Select(db => SaveCapturingAsync(db)).ToList();
                var failures = (await Task.WhenAll(saves)).OfType<Exception>().ToList();

                Assert.True(
                    failures.Count == 0,
                    $"Раунд {round}: {failures.Count} із {Writers} записувачів упали: "
                    + string.Join(" | ", failures.Select(f => f.GetBaseException().Message)));
            }
            finally
            {
                foreach (var db in contexts)
                {
                    await db.DisposeAsync();
                }
            }

            await using var check = chain.CreateContext();
            var ids = await check.CalculationSteps.AsNoTracking()
                .Where(s => s.CalculationRunId == runId)
                .Select(s => s.Id)
                .ToListAsync(CancellationToken.None);

            var expected = (round + 1) * Writers * StepsPerWriter;
            Assert.Equal(expected, ids.Count);
            Assert.Equal(expected, ids.Distinct().Count());
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "CalcStepIdRace")]
    public async Task Id_з_послідовності_оминає_кроки_записані_старим_MAX_плюс_один()
    {
        // ⚠ Кроки, записані ДО фіксу, мають Id від 1 через MAX+1, а послідовність
        // спільна з результатами й про них не знає. Якщо вона відстає від
        // старого MAX, перший же діапазон ліг би на наявні Id того самого
        // періоду — тобто фікс сам народив би дубль ключа. Моделюємо це:
        // старі кроки стоять РІВНО на тих значеннях, які послідовність видасть
        // наступними.
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        var periodKey = document.PeriodKey.Value;

        await using var db = chain.CreateContext();
        var run = new CalculationRun(document.ProjectId, periodKey, triggeredByUserId: null, Now);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        var peek = await db.Database.SqlQuery<long>($"""
            DECLARE @first sql_variant;
            EXEC sys.sp_sequence_get_range
                @sequence_name = N'calc.CalculationResultSeq',
                @range_size = 1,
                @range_first_value = @first OUTPUT;
            SELECT CONVERT(bigint, @first) AS Value;
            """).ToListAsync(CancellationToken.None);

        for (var k = 1; k <= StepsPerWriter; k++)
        {
            var legacy = new CalculationStep(run.Id, periodKey, k, "LEGACY");
            SetId(legacy, peek[0] + k);
            db.CalculationSteps.Add(legacy);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        await using var writer = chain.CreateContext();
        await new CalculationResultStore(writer, new TestClock(Now)).WriteTraceAsync(
            run.Id, [Output(document.DocumentId, "fresh")], TraceLevel.Full, CancellationToken.None);
        await writer.SaveChangesAsync(CancellationToken.None);

        await using var check = chain.CreateContext();
        var count = await check.CalculationSteps.AsNoTracking()
            .CountAsync(s => s.CalculationRunId == run.Id, CancellationToken.None);
        Assert.Equal(2 * StepsPerWriter, count);
    }

    private static CalculationOutput Output(long documentId, string rowKey) => new(
        documentId,
        rowKey,
        [],
        Enumerable.Range(1, StepsPerWriter)
            .Select(k => new CalculationTraceStep(k, $"S{k}", "a+b", k, TraceJson: null))
            .ToList());

    private static async Task<Exception?> SaveCapturingAsync(EcrDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
            return null;
        }
        catch (DbUpdateException ex)
        {
            return ex;
        }
    }

    private static void SetId(CalculationStep step, long id) =>
        typeof(Domain.Abstractions.Entity<long>)
            .GetProperty(nameof(Domain.Abstractions.Entity<long>.Id))!
            .SetValue(step, id);
}
