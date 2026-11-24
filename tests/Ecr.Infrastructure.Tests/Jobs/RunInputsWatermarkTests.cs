// tests/Ecr.Infrastructure.Tests/Jobs/RunInputsWatermarkTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ W15z M7 (огляд b11 №3): межа входів прогону документа (<c>calc.CalculationRun.InputsCellChangeWatermark</c>) — закомічений
/// журнал комірок на старті прогону; прогін, що не перерахував затверджене (оригінал чи копію), межі не піднімає.
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально, scratchpad\m11): (1) знімок без табличного блокування (<c>IDENT_CURRENT</c> одразу) — червона «правка в
/// польоті»; (2) <c>EffectiveInputsWatermark</c> ігнорує <c>partial</c> — червоні «затверджений оригінал/копія»;
/// (3) <c>ApprovedStatePeriodsAsync</c> лише оригінали — червона «копія»; (4) межа попереднього не читається (<c>null</c>) — червоні
/// «затверджений …» (очікують W1).
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Decision", "D-570")]
public sealed class RunInputsWatermarkTests(SqlServerFixture sql)
{
    private static readonly PeriodKey January = new(202601);
    private static readonly DateTime At = new(2061, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// (c): правка, вставлена, але ще не закомічена (мітка — початок її транзакції), до межі не входить: знімок чекає її коміту
    /// (або «невідомо» за таймаутом), а не бере <c>IDENT_CURRENT</c>, що вже її видав. 20/20 локально.
    /// </summary>
    [Fact]
    public async Task Межа_не_бачить_правки_в_польоті_і_чекає_її_коміту()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: January.Value);
        await using var writer = new SqlConnection(sql.ConnectionString);
        await writer.OpenAsync();
        await using var tx = (SqlTransaction)await writer.BeginTransactionAsync();
        long inFlight;
        await using (var insert = writer.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                                            ChangedByUserId, Origin)
                OUTPUT INSERTED.Id
                VALUES (@at, @period, @doc, @row, N'R1', @column, N'1', N'2', 1, N'UserEdit');
                """;
            insert.Parameters.AddWithValue("@at", At);
            insert.Parameters.AddWithValue("@period", January.Value);
            insert.Parameters.AddWithValue("@doc", document.DocumentId);
            insert.Parameters.AddWithValue("@row", document.RowIds[0]);
            insert.Parameters.AddWithValue("@column", document.ColumnDefIds[1]);
            inFlight = (long)(await insert.ExecuteScalarAsync())!;
        }

        await using (var db = sql.CreateContext())
        {
            Assert.Null(await CellChangeWatermarks.CommittedAsync(db, CancellationToken.None, lockTimeoutMs: 300));
        }

        await using var waiting = sql.CreateContext();
        var capture = CellChangeWatermarks.CommittedAsync(waiting, CancellationToken.None, lockTimeoutMs: 30_000);
        await Task.Delay(300);
        Assert.False(capture.IsCompleted);

        await tx.CommitAsync();
        var watermark = await capture;
        Assert.NotNull(watermark);
        Assert.True(watermark >= inFlight, $"межа {watermark} < закомічена правка {inFlight}");
    }

    [Fact]
    public async Task Повний_прогін_документа_ставить_межу_не_нижчу_за_закомічену_правку()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: January.Value);
        var edit = await CellChangeAsync(document);

        await RunAsync(document, At);

        var watermark = Assert.Single(await WatermarksAsync(document));
        Assert.NotNull(watermark);
        Assert.True(watermark >= edit, $"межа {watermark} < правка {edit}");
    }

    /// <summary>
    /// Прогін після затвердження оригіналу аркуша чи КОПІЇ (затверджені числа він не переписує) бере межу попереднього
    /// актуального прогону: правка між прогонами в межу не входить. Без попереднього прогону — межі немає.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Затверджений_оригінал_чи_копія_не_піднімає_межу(bool copy, bool previousRun)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: January.Value);
        if (previousRun)
        {
            await RunAsync(document, At);
        }

        var edit = await CellChangeAsync(document);
        await ApproveAsync(builder, document, copy);

        await RunAsync(document, At.AddHours(1));

        var watermarks = await WatermarksAsync(document);
        Assert.Equal(previousRun ? 2 : 1, watermarks.Count);
        if (previousRun)
        {
            Assert.NotNull(watermarks[0]);
            Assert.Equal(watermarks[0], watermarks[1]);
            Assert.True(watermarks[1] < edit, $"межа {watermarks[1]} ≥ правка {edit} після затвердження");
        }
        else
        {
            Assert.Null(watermarks[0]);
        }
    }

    [Theory]
    [InlineData(10L, false, 3L, 10L)]
    [InlineData(10L, true, 3L, 3L)]
    [InlineData(2L, true, 3L, 2L)]
    public void Ефективна_межа_неповного_прогону_не_вища_за_попередню(long start, bool partial, long previous, long expected)
        => Assert.Equal(expected, RecalculationJob.EffectiveInputsWatermark(start, partial, previous));

    [Fact]
    public void Невідома_межа_дає_невідому()
    {
        Assert.Null(RecalculationJob.EffectiveInputsWatermark(null, partial: false, previous: 5));
        Assert.Null(RecalculationJob.EffectiveInputsWatermark(5, partial: true, previous: null));
        Assert.Equal(5, RecalculationJob.EffectiveInputsWatermark(5, partial: false, previous: null));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private async Task RunAsync(TestDocument document, DateTime at)
    {
        await using var db = sql.CreateContext();
        await NightlyJobParts.Job(db, new NoopRunner(), Substitute.For<IBackgroundJobScheduler>(), at)
            .ExecuteAsync(
                new RecalculationRequest(document.ProjectId, document.DocumentId, January.Value, TriggeredByUserId: null),
                Substitute.For<IJobProgress>(),
                CancellationToken.None);
    }

    private async Task<long> CellChangeAsync(TestDocument document)
    {
        await using var db = sql.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync(
            [new CellChangeRecord(At, new CellAddress(January, document.RowIds[0], document.ColumnDefIds[1]), document.DocumentId, "R1", "1", "2", 5, "UserEdit", false, null)],
            CancellationToken.None);
        return (await db.Database
            .SqlQuery<long>($"SELECT MAX(Id) AS Value FROM aud.CellChange WHERE DocumentId = {document.DocumentId}")
            .ToListAsync()).Single();
    }

    private static async Task ApproveAsync(TestDocumentBuilder builder, TestDocument document, bool copy)
    {
        // Оригінал: увесь затверджений період прогін відмовляє рахувати (ФВ-9.17), тож затверджено ІНШИЙ аркуш періоду (аркуш
        // іншого шаблону — будівник дає документ з одним аркушем); прогін далі рахує свій, а затверджений пропускає.
        var otherSheet = copy ? 0 : (await builder.BuildAsync(periodKey: January.Value)).SheetDefId;
        await using var db = builder.CreateContext();
        long? copyId = null;
        if (copy)
        {
            if (!await db.DocumentSheets.AnyAsync(s => s.DocumentId == document.DocumentId && s.CopyOfId == null))
            {
                db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));
                await db.SaveChangesAsync();
            }

            copyId = (await new DocumentSheetCopyStore(db).AddCopyAsync(document.DocumentId, document.SheetDefId, default)).SheetCopyId;
        }

        // Оригінал: увесь затверджений період прогін відмовляє рахувати (ФВ-9.17), тож затверджено ІНШИЙ аркуш періоду (аркуш
        // іншого шаблону — будівник дає документ з одним аркушем); прогін документа далі рахує свій, а затверджений пропускає.
        var state = new ApprovalState(document.DocumentId, copy ? document.SheetDefId : otherSheet, January.Value, copyId);
        state.Submit(userId: 5, At.AddMinutes(10));
        state.Approve(userId: 6, At.AddMinutes(20));
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
    }

    private async Task<List<long?>> WatermarksAsync(TestDocument document)
    {
        await using var db = sql.CreateContext();
        return await db.CalculationRuns.AsNoTracking()
            .Where(r => r.DocumentId == document.DocumentId && r.PeriodKey == January.Value)
            .OrderBy(r => r.Id)
            .Select(r => r.InputsCellChangeWatermark)
            .ToListAsync();
    }

    /// <summary>Фейк оркестратора: нічого не пише.</summary>
    private sealed class NoopRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }
}
