// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueDocumentExclusionTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence.Migrations;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PSS-04 (аудит 20.11, <c>D-561</c>): claim лейнів із локом документа (<c>recalc</c>, <c>interactive</c>) не бере
/// задачу документа, який уже рахує <c>Running</c> цих лейнів — замість claim → секунда очікування лока → відкладення
/// на 5 с → claim … увесь чужий прогін.
/// </summary>
/// <remarks>
/// Без реального часу: стани задаються claim/Complete, жодних очікувань.
/// <para>
/// МУТАЦІЇ (локально): (1) <c>ClaimQueuedSqlFor</c> без <c>DocumentExclusion</c> — червоні
/// <c>Задача_документа_що_рахується_не_береться_ні_в_recalc_ні_в_interactive</c> і
/// <c>Виконувана_interactive_теж_тримає_документ</c>; (2) <c>IsDocumentExclusiveLane</c> → <c>true</c> для всіх лейнів —
/// червоний <c>Інші_лейни_і_задачі_без_документа_не_виключаються</c>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PSS-04")]
public sealed class DbJobQueueDocumentExclusionTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const long Doc = 9_000_001;

    private const long OtherDoc = 9_000_002;

    [Fact]
    public async Task Задача_документа_що_рахується_не_береться_ні_в_recalc_ні_в_interactive()
    {
        await using var host = NewHost();
        var running = await EnqueueToAsync(JobLanes.Recalc, Doc);
        var claim = await ClaimAsync(host, JobLanes.Recalc);
        Assert.Equal(running, claim?.Claim.JobId);

        var sameRecalc = await EnqueueToAsync(JobLanes.Recalc, Doc);
        var sameInteractive = await EnqueueToAsync(JobLanes.Interactive, Doc);
        var otherRecalc = await EnqueueToAsync(JobLanes.Recalc, OtherDoc);

        // Той самий документ — ні в recalc, ні в interactive; чужий — береться повз (FIFO лише серед дозволених).
        Assert.Null(await ClaimAsync(host, JobLanes.Interactive));
        Assert.Equal(otherRecalc, (await ClaimAsync(host, JobLanes.Recalc))?.Claim.JobId);
        Assert.Null(await ClaimAsync(host, JobLanes.Recalc));

        // Виконувана закінчилась — обидві задачі документа знову беруться (без жодного відкладення).
        Assert.True(await host.Queue.CompleteAsync(claim!.Claim, CancellationToken.None));
        var next = await ClaimAsync(host, JobLanes.Interactive);
        Assert.Equal(sameInteractive, next?.Claim.JobId);

        // А тепер документ тримає interactive: recalc того самого документа чекає.
        Assert.Null(await ClaimAsync(host, JobLanes.Recalc));
        Assert.True(await host.Queue.CompleteAsync(next!.Claim, CancellationToken.None));
        Assert.Equal(sameRecalc, (await ClaimAsync(host, JobLanes.Recalc))?.Claim.JobId);
    }

    [Fact]
    public async Task Виконувана_interactive_теж_тримає_документ()
    {
        await using var host = NewHost();
        await EnqueueToAsync(JobLanes.Interactive, Doc);
        Assert.NotNull(await ClaimAsync(host, JobLanes.Interactive));

        var recalc = await EnqueueToAsync(JobLanes.Recalc, Doc);
        Assert.Null(await ClaimAsync(host, JobLanes.Recalc));

        // Задача, що чекає, не «відкладалась» і не рахувала спроб: перший claim після звільнення — спроба 1.
        var row = await RowAsync(recalc);
        Assert.Equal("Queued", row!.State);
        Assert.Equal(0, row.Attempt ?? 0);
    }

    [Fact]
    public async Task Інші_лейни_і_задачі_без_документа_не_виключаються()
    {
        await using var host = NewHost();
        await EnqueueToAsync(JobLanes.Recalc, Doc);
        Assert.NotNull(await ClaimAsync(host, JobLanes.Recalc));

        // Лейн default (експорт тощо) лок документа не бере — виключення його не стосується.
        var inDefault = await EnqueueToAsync(JobLanes.Default, Doc);
        Assert.Equal(inDefault, (await ClaimAsync(host, JobLanes.Default))?.Claim.JobId);

        // Розклад проєкту (documentId 0) і задачі без документа — по кілька одночасно.
        var project1 = await EnqueueToAsync(JobLanes.Recalc, 0);
        Assert.Equal(project1, (await ClaimAsync(host, JobLanes.Recalc))?.Claim.JobId);
        var project2 = await EnqueueToAsync(JobLanes.Recalc, 0);
        Assert.Equal(project2, (await ClaimAsync(host, JobLanes.Recalc))?.Claim.JobId);
        var none1 = await EnqueueToAsync(JobLanes.Recalc, null);
        Assert.Equal(none1, (await ClaimAsync(host, JobLanes.Recalc))?.Claim.JobId);
        var none2 = await EnqueueToAsync(JobLanes.Recalc, null);
        Assert.Equal(none2, (await ClaimAsync(host, JobLanes.Recalc))?.Claim.JobId);
    }

    /// <summary>
    /// Міграція <c>PSS04JobQueueDocumentIndex</c>: повтор на базі, де індекс уже є, нічого не ламає; гілка без
    /// <c>ONLINE</c> (Standard/Express) будує той самий індекс.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Міграція_індексу_ідемпотентна_гілка_Express_будує_той_самий()
    {
        Assert.Contains("ONLINE = ON", PSS04JobQueueDocumentIndex.CreateIndexSql("3"), StringComparison.Ordinal);

        Assert.Equal(1, await IndexCountAsync());
        var definition = await DefinitionAsync();
        Assert.Contains("DocumentId:0:1", definition, StringComparison.Ordinal);
        Assert.Contains("Lane:1:0", definition, StringComparison.Ordinal);

        await ExecuteAsync(PSS04JobQueueDocumentIndex.CreateIndexSql(PSS04JobQueueDocumentIndex.EngineEditionExpression));
        Assert.Equal(1, await IndexCountAsync());

        await ExecuteAsync($"DROP INDEX {PSS04JobQueueDocumentIndex.IndexName} ON itg.JobProgress;");
        await ExecuteAsync(PSS04JobQueueDocumentIndex.CreateIndexSql("4"));
        Assert.Equal(definition, await DefinitionAsync());

        await ExecuteAsync($"DROP INDEX {PSS04JobQueueDocumentIndex.IndexName} ON itg.JobProgress;");
        await ExecuteAsync(PSS04JobQueueDocumentIndex.CreateIndexSql(PSS04JobQueueDocumentIndex.EngineEditionExpression));
        Assert.Equal(definition, await DefinitionAsync());

        // І claim з підказкою індексу працює на перебудованому (8622 — якби фільтр розійшовся із запитом).
        await using var host = NewHost();
        await EnqueueToAsync(JobLanes.Recalc, Doc);
        Assert.NotNull(await ClaimAsync(host, JobLanes.Recalc));
    }

    private static Task<ClaimedJob?> ClaimAsync(Host host, string lane)
        => host.Queue.ClaimAsync([lane], "test/doc-exclusion", JobQueueLimits.DefaultLease, CancellationToken.None);

    private async Task<string> EnqueueToAsync(string lane, long? documentId)
    {
        await using var host = NewHost();
        var payload = documentId is { } id ? $"{{\"documentId\":{id}}}" : "{}";
        return (await host.Queue.EnqueueAsync(
                new JobEnqueueRequest(Code, lane, payload, DocumentId: documentId), CancellationToken.None))
            .JobId;
    }

    private async Task<int> IndexCountAsync()
        => (int)(await ScalarAsync(
            $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'itg.JobProgress') AND name = N'{PSS04JobQueueDocumentIndex.IndexName}'"))!;

    private async Task<string> DefinitionAsync()
        => (string)(await ScalarAsync(
            $"""
             SELECT STRING_AGG(CONCAT(c.name, ':', ic.is_included_column, ':', ic.key_ordinal), ',')
                        WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, c.name)
                    + '|' + ISNULL(MAX(i.filter_definition), N'')
               FROM sys.indexes i
               JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
               JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE i.object_id = OBJECT_ID(N'itg.JobProgress') AND i.name = N'{PSS04JobQueueDocumentIndex.IndexName}'
             """))!;

    private async Task<object?> ScalarAsync(string statement)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }
}
