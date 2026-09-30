// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationDocumentLockFairnessTests.cs
using System.Diagnostics;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Д-3 огляду O1 (гіпотеза): N редакторів документа — N інкрементних задач (ціль включає
/// автора), що майже безперервно тримають лок <see cref="RecalculationDocumentLock"/>;
/// повний перерахунок чекає лише <see cref="RecalculationDocumentLock.BusyWait"/> і за
/// <see cref="JobDeferral.MaxDeferral"/> міг би впасти <c>Failed</c>.
/// </summary>
/// <remarks>
/// Висновок — гіпотеза НЕ підтверджується: <c>sp_getapplock</c> видає ексклюзивний лок
/// черзі очікувачів у порядку надходження (перший тест — механізм, детерміновано через
/// <c>sys.dm_tran_locks</c>), тож новий запит інкрементної задачі не «перескакує» повний,
/// що вже чекає. Другий тест — сама гіпотеза з гіршим тиском, ніж у виконавця (редактори
/// повторюють негайно, а не через <see cref="RecalculationDocumentLock.DeferDelay"/>).
/// Замір 2026-09-30: повний дістає лок зі 4-ї спроби (тричі поспіль); з роботою редактора
/// 1,5 с (довше за <c>BusyWait</c>) — з 9-ї: очікувачі-редактори теж здаються за
/// <c>BusyWait</c>, і черга обертається. Лок, що не звільняється взагалі, покриває стеля
/// (<c>JobDeferralCapTests</c>).
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.8")]
public sealed class RecalculationDocumentLockFairnessTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Лок_документа_видається_черзі_у_порядку_надходження_новий_запит_не_перескакує()
    {
        var resource = RecalculationDocumentLock.Resource(LockedDocumentJob.NewDocumentId());
        var holder = (await SqlDistributedLock.AcquireAsync(sql.ConnectionString, resource, TimeSpan.Zero, CancellationToken.None))!;

        // Повний стає в чергу першим, інкрементний — другим (обидва підтверджено як WAIT).
        var full = SqlDistributedLock.AcquireAsync(sql.ConnectionString, resource, Patience, CancellationToken.None);
        await WaitersAsync(resource, 1);
        var incremental = SqlDistributedLock.AcquireAsync(sql.ConnectionString, resource, Patience, CancellationToken.None);
        await WaitersAsync(resource, 2);

        await holder.DisposeAsync();

        // ⛔ Лок дістається тому, хто чекав першим; другий і далі чекає.
        await using var fullLock = await full.WaitAsync(Patience);
        Assert.NotNull(fullLock);
        Assert.False(incremental.IsCompleted);
        await WaitersAsync(resource, 1);

        await fullLock!.DisposeAsync();
        await using var incrementalLock = await incremental.WaitAsync(Patience);
        Assert.NotNull(incrementalLock);
    }

    [Fact]
    public async Task П_ять_редакторів_по_300_мс_повний_перерахунок_дістає_лок_до_стелі_відкладень()
    {
        var resource = RecalculationDocumentLock.Resource(LockedDocumentJob.NewDocumentId());
        var clock = new TestClock(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
        var since = clock.UtcNow;
        using var stop = new CancellationTokenSource();
        var editorRuns = 0;

        // П'ять «редакторів»: лок → 300 мс роботи → відпустити → одразу знову (гірше за
        // виконавця: той після невдачі відкладає задачу на DeferDelay).
        var editors = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await using var held = await SqlDistributedLock.AcquireAsync(
                    sql.ConnectionString, resource, RecalculationDocumentLock.BusyWait, CancellationToken.None);
                if (held is not null)
                {
                    Interlocked.Increment(ref editorRuns);
                    await Task.Delay(300);
                }
            }
        })).ToArray();

        try
        {
            // Лок уже зайнятий редакторами, перш ніж повний почне пробувати.
            var warm = Stopwatch.StartNew();
            while (Volatile.Read(ref editorRuns) < 5)
            {
                Assert.True(warm.Elapsed < Patience, "Редактори не почали роботу.");
                await Task.Delay(20);
            }

            // Повний перерахунок: BusyWait, невдача — відкладення (годинник +BusyWait+DeferDelay).
            var attempts = 0;
            while (true)
            {
                attempts++;
                await using var held = await SqlDistributedLock.AcquireAsync(
                    sql.ConnectionString, resource, RecalculationDocumentLock.BusyWait, CancellationToken.None);
                if (held is not null)
                {
                    break;
                }

                clock.Advance(RecalculationDocumentLock.BusyWait + RecalculationDocumentLock.DeferDelay);
                Assert.False(
                    JobDeferral.IsExhausted(since, clock.UtcNow, JobDeferral.MaxDeferral),
                    $"Д-3 підтверджено: повний перерахунок не дістав лок за {attempts} спроб ({JobDeferral.Format(clock.UtcNow - since)}).");
            }

            output.WriteLine($"Повний перерахунок дістав лок зі спроби {attempts}; редактори виконали {Volatile.Read(ref editorRuns)} прогонів.");
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(editors);
        }
    }

    private async Task WaitersAsync(string resource, int expected)
    {
        var watch = Stopwatch.StartNew();

        while (true)
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' " +
                "AND request_status = 'WAIT' AND resource_database_id = DB_ID() " +
                "AND resource_description LIKE '%' + @name + '%';";
            command.Parameters.AddWithValue("@name", resource);
            if ((int)(await command.ExecuteScalarAsync())! == expected)
            {
                return;
            }

            Assert.True(watch.Elapsed < Patience, $"Очікувачів лока не {expected}.");
            await Task.Delay(20);
        }
    }
}
