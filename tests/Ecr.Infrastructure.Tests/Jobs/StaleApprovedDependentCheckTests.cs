// tests/Ecr.Infrastructure.Tests/Jobs/StaleApprovedDependentCheckTests.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Tests.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B80 (N4, R3.2): правило узгодженості <c>STALE_APPROVED_DEPENDENT</c> — затверджений місяць Land, чиє джерело HSE400
/// після затвердження повернули в роботу (<c>notApproved</c>) або затвердили пізніше (<c>reapproved</c>).
/// </summary>
/// <remarks>
/// ⚠ Спільна тестова база: інші тести лишають зв'язки <c>ApprovedOnly</c> і затверджені стани, тож перевірки — лише за
/// власним документом світу (<see cref="SourceIssueWorld"/>).
/// <para>
/// Мутації (див. звіт N4): прибрати виклик <c>StaleApprovedDependentsAsync</c> з <c>ConsistencyCheckJob.CheckAsync</c> —
/// червоніє наскрізний тест задачі; фільтр <c>Kind == SourceChangedAfterApproval</c> → без нього — червоніє «Land не
/// затверджено».
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B80")]
public sealed class StaleApprovedDependentCheckTests(SqlServerFixture sql)
{
    private const int March = SourceIssueWorld.March;
    private static readonly DateTime T0 = new(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Пара: джерело повернуто в роботу після затвердження Land — за <c>Any</c> знахідки немає, за <c>ApprovedOnly</c> є.</summary>
    [Theory]
    [InlineData(DocumentSourceState.Any, false)]
    [InlineData(DocumentSourceState.ApprovedOnly, true)]
    public async Task Джерело_повернуто_в_роботу_Any_нічого_ApprovedOnly_знахідка(DocumentSourceState state, bool found)
    {
        // ⚠ Чужа зв'язка ApprovedOnly є завжди — рядок Any перевіряє фільтр зв'язок, а не лише загальну перевірку.
        await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.ApprovedOnly);
        var world = await SourceIssueWorld.ArrangeAsync(sql, state);
        var source = await SourceIssueWorld.AddStateAsync(sql, world.Source.DocumentId, world.Source.SheetDefId, March, T0, T0);
        var land = await SourceIssueWorld.AddStateAsync(sql, world.Land.DocumentId, world.Land.SheetDefId, March, T1, T1);
        await SourceIssueWorld.ReopenAsync(sql, source, T2);

        var stale = await OwnAsync(world.Land.DocumentId);

        if (!found)
        {
            Assert.Empty(stale);
            return;
        }

        var finding = Assert.Single(stale);
        Assert.Equal(land, finding.StateId);
        Assert.Equal((March, world.Source.DocumentId, March), (finding.PeriodKey, finding.SourceDocumentId, finding.SourcePeriodKey));
        Assert.Equal(DocumentSourceIssues.ReasonNotApproved, finding.Reason);
    }

    [Fact]
    public async Task Джерело_затверджено_пізніше_за_Land_знахідка_reapproved()
    {
        var world = await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.ApprovedOnly);
        await SourceIssueWorld.AddStateAsync(sql, world.Land.DocumentId, world.Land.SheetDefId, March, T0, T1);
        await SourceIssueWorld.AddStateAsync(sql, world.Source.DocumentId, world.Source.SheetDefId, March, T0, T2);

        var finding = Assert.Single(await OwnAsync(world.Land.DocumentId));
        Assert.Equal(DocumentSourceIssues.ReasonReapproved, finding.Reason);
    }

    [Fact]
    public async Task Land_затверджено_після_джерела_знахідки_немає()
    {
        var world = await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.ApprovedOnly);
        await SourceIssueWorld.AddStateAsync(sql, world.Source.DocumentId, world.Source.SheetDefId, March, T0, T1);
        await SourceIssueWorld.AddStateAsync(sql, world.Land.DocumentId, world.Land.SheetDefId, March, T1, T2);

        Assert.Empty(await OwnAsync(world.Land.DocumentId));
    }

    /// <summary>Land лише подано (не затверджено), джерело не затверджено — це не знахідка правила (це панель і блок A3).</summary>
    [Fact]
    public async Task Land_не_затверджено_знахідки_немає()
    {
        var world = await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.ApprovedOnly);
        await SourceIssueWorld.AddStateAsync(sql, world.Land.DocumentId, world.Land.SheetDefId, March, T1);

        Assert.Empty(await OwnAsync(world.Land.DocumentId));
    }

    /// <summary>
    /// B182 (K4): затверджено лише КОПІЮ аркуша Land (оригінал лише подано), джерело не затверджено — знахідки немає:
    /// копії аркушів не враховуються.
    /// <para>
    /// ⚠ Мутація K4 (без <c>SheetCopyId == null</c> у кандидатах) цим тестом НЕ червоніє і не може: кандидат лише з
    /// копією далі читається <see cref="DocumentSourceIssues"/> за оригіналами, а оригінал не затверджено — вид
    /// <c>SourceMonthNotApproved</c>, який правило відкидає. Мутація еквівалентна за результатом (лише зайвий кандидат).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Затверджено_лише_копію_аркуша_Land_знахідки_немає()
    {
        var world = await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.ApprovedOnly);
        await SourceIssueWorld.AddStateAsync(sql, world.Source.DocumentId, world.Source.SheetDefId, March, T0);
        await SourceIssueWorld.AddStateAsync(sql, world.Land.DocumentId, world.Land.SheetDefId, March, T1);
        await SourceIssueWorld.AddCopyStateAsync(sql, world.Land.DocumentId, world.Land.SheetDefId, March, T1, T1);

        Assert.Empty(await OwnAsync(world.Land.DocumentId));
    }

    /// <summary>B182 (K5): стеля знахідок — рівно <c>limit</c>, не <c>limit + 1</c> (межа 1 і 2 за ≥ 2 знахідок у базі).</summary>
    [Fact]
    public async Task Стеля_знахідок_рівно_limit()
    {
        for (var i = 0; i < 2; i++)
        {
            var world = await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.ApprovedOnly);
            await SourceIssueWorld.AddStateAsync(sql, world.Land.DocumentId, world.Land.SheetDefId, March, T0, T1);
            await SourceIssueWorld.AddStateAsync(sql, world.Source.DocumentId, world.Source.SheetDefId, March, T0, T2);
        }

        await using var db = sql.CreateContext();
        Assert.Single(await new StaleApprovedDependentCheck(db, readOverride: null, DependencyGraphTestKit.Provider(db)).RunAsync(1, CancellationToken.None));
        Assert.Equal(2, (await new StaleApprovedDependentCheck(db, readOverride: null, DependencyGraphTestKit.Provider(db)).RunAsync(2, CancellationToken.None)).Count);
    }

    /// <summary>Наскрізно: нічна задача пише знахідку в <c>aud.ConsistencyIssue</c> конвертом каталогу з деталями.</summary>
    [Fact]
    public async Task Задача_узгодженості_пише_знахідку_STALE_APPROVED_DEPENDENT()
    {
        var world = await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.ApprovedOnly);
        await SourceIssueWorld.AddStateAsync(sql, world.Land.DocumentId, world.Land.SheetDefId, March, T0, T1);
        await SourceIssueWorld.AddStateAsync(sql, world.Source.DocumentId, world.Source.SheetDefId, March, T0, T2);
        var land = await LandStateAsync(world.Land.DocumentId);

        await RunJobAsync();

        var finding = await FindingAsync(land);
        Assert.NotNull(finding);
        Assert.Equal(2, finding.Value.Severity);
        Assert.Equal(March, finding.Value.PeriodKey);
        Assert.True(JobProgressMessageCodec.TryDecode(finding.Value.Message, out var envelope), finding.Value.Message);
        Assert.Equal(ConsistencyCheckJob.FindingKeyPrefix + ConsistencyCheckJob.StaleApprovedDependentRule, envelope.Key);
        Assert.Equal(world.Land.DocumentId.ToString(CultureInfo.InvariantCulture), envelope.Params!["documentId"]);
        Assert.Equal(March.ToString(CultureInfo.InvariantCulture), envelope.Params["periodKey"]);
        Assert.Equal(world.Source.DocumentId.ToString(CultureInfo.InvariantCulture), envelope.Params["sourceDocumentId"]);
        Assert.Equal(March.ToString(CultureInfo.InvariantCulture), envelope.Params["sourcePeriodKey"]);
        Assert.Equal(DocumentSourceIssues.ReasonReapproved, envelope.Params["reason"]);
    }

    private async Task<List<StaleApprovedDependent>> OwnAsync(long documentId)
    {
        await using var db = sql.CreateContext();
        var all = await new StaleApprovedDependentCheck(db, readOverride: null, DependencyGraphTestKit.Provider(db)).RunAsync(int.MaxValue, CancellationToken.None);
        return [.. all.Where(s => s.DocumentId == documentId)];
    }

    private async Task<long> LandStateAsync(long documentId)
    {
        await using var db = sql.CreateContext();
        return await db.ApprovalStates.Where(s => s.DocumentId == documentId && s.SheetCopyId == null).MinAsync(s => s.Id);
    }

    private async Task RunJobAsync()
    {
        await using var db = sql.CreateContext();
        var job = new ConsistencyCheckJob(
            db, Substitute.For<IOrphanScanner>(), new TestClock(new DateTime(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IConsistencyMetrics>(), dependencyGraph: DependencyGraphTestKit.Provider(db));

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    private async Task<(byte Severity, string Message, int? PeriodKey)?> FindingAsync(long stateId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) Severity, Message, PeriodKey FROM aud.ConsistencyIssue
             WHERE RuleCode = @rule AND EntityType = @type AND EntityId = @id;
            """;
        command.Parameters.AddWithValue("@rule", ConsistencyCheckJob.StaleApprovedDependentRule);
        command.Parameters.AddWithValue("@type", StaleApprovedDependentCheck.EntityType);
        command.Parameters.AddWithValue("@id", stateId);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None))
        {
            return null;
        }

        return (reader.GetByte(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2));
    }
}
