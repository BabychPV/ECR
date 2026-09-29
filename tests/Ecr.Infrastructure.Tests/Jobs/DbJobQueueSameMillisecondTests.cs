// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueSameMillisecondTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Постановка й claim в одну мілісекунду: задача з <c>AvailableAt</c> = «зараз»
/// має бути видна claim'у одразу, без паузи.
/// </summary>
/// <remarks>
/// ⚠ <c>AvailableAt</c> — <c>datetime2(3)</c>, записаний із <c>SYSUTCDATETIME()</c>
/// (точність 100 нс) з ОКРУГЛЕННЯМ: до 0,5 мс угору. Порівняння з голим
/// <c>SYSUTCDATETIME()</c> у claim давало «задача ще в майбутньому» приблизно в
/// половині пар «Enqueue → Claim» у ту саму мілісекунду (10174 із 20000). Тест
/// ганяє пари без паузи, доки не набере <see cref="Iterations"/>, і падає на
/// першому null. Мутація: прибрати <c>CAST(… AS datetime2(3))</c> у
/// <c>DbJobQueue.ClaimQueuedSql</c> — тест червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class DbJobQueueSameMillisecondTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Iterations = 2000;

    [Fact]
    public async Task Enqueue_і_Claim_в_одну_мілісекунду_claim_ніколи_не_повертає_null()
    {
        await using var host = NewHost();

        for (var i = 0; i < Iterations; i++)
        {
            var jobId = (await host.Queue.EnqueueAsync(Request(), CancellationToken.None)).JobId;
            var claimed = await host.ClaimAsync();

            Assert.True(
                claimed is not null,
                $"ітерація {i}: claim повернув null для щойно поставленої задачі {jobId} з AvailableAt = зараз");
            Assert.Equal(jobId, claimed!.Claim.JobId);
        }
    }
}
