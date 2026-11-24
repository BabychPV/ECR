// tests/Ecr.Infrastructure.Tests/Jobs/SourceCascadePendingGateTests.cs
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// F5 (рішення людини «Заборонити, поки йде перерахунок»): <see cref="SourceCascadePendingGate"/> на справжній
/// <c>itg.JobProgress</c> — який місяць документа-споживача ще чекає каскаду від зміни джерела (<c>D-566</c>).
/// </summary>
/// <remarks>
/// Мутації (локально, не комітяться): <c>IsPendingAsync</c> → <c>false</c> (ворота вимкнено) — червоні «чекає».
/// </remarks>
[Collection("SqlServer")]
public sealed class SourceCascadePendingGateTests(SqlServerFixture sql)
{
    private const int Year = 2026;

    private static long _doc = 9_100_000_000L + (Environment.ProcessId % 100_000 * 1000L);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Без_живої_задачі_каскаду_нічого_не_чекає()
    {
        var doc = NextDocument();
        Assert.False(await PendingAsync(doc, 202603));

        // Задача каскаду, що закінчилась, — уже не чекає.
        await InsertAsync(doc, Target(doc), "Succeeded", Payload(doc, cascade: [202603]));
        Assert.False(await PendingAsync(doc, 202603));
    }

    /// <summary>
    /// ✎ Огляд b11 (HIGH-1): задача каскаду року тримає ВЕСЬ рік документа — і раніші за її періоди місяці (якір із річними
    /// підсумками, читачі <c>[Period:#k]</c>, яких будить прогін місяця). Інший рік і інший документ — ні.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Задача_каскаду_тримає_весь_рік_документа()
    {
        var doc = NextDocument();
        await InsertAsync(doc, Target(doc), "Queued", Payload(doc, cascade: [202603, 202605]));

        foreach (var period in new[] { 202601, 202602, 202603, 202604, 202605, 202612 })
        {
            Assert.True(await PendingAsync(doc, period), $"період {period}");
        }

        Assert.False(await PendingAsync(doc, 202703));
        Assert.False(await PendingAsync(NextDocument(), 202603));
    }

    /// <summary>
    /// ✎ Огляд b11 (HIGH-1): ланцюжок тримає рік, поки живий, — і зроблені кроки (прогін наступного кроку будить раніших читачів).
    /// </summary>
    /// <remarks>Мутація: повернути «лише решта плану» — червоний січень/березень.</remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Ланцюжок_тримає_весь_рік_поки_живий()
    {
        var doc = NextDocument();
        await InsertAsync(
            doc, Target(doc), "Running",
            Payload(doc, cascade: [202603], chainAfter: 202603, chainPlan: [202605, 202607]));
        foreach (var period in new[] { 202601, 202603, 202604, 202605, 202607, 202612 })
        {
            Assert.True(await PendingAsync(doc, period), $"Running, період {period}");
        }

        await SetAsync(doc, "Queued", Payload(doc, cascade: [202603], chainAfter: 202605, chainPlan: [202607]));
        Assert.True(await PendingAsync(doc, 202601));
        Assert.True(await PendingAsync(doc, 202605));

        await SetAsync(doc, "Succeeded", Payload(doc, cascade: [202603], chainAfter: 202607, chainPlan: []));
        Assert.False(await PendingAsync(doc, 202601));
        Assert.False(await PendingAsync(doc, 202607));
    }

    /// <summary>
    /// Поперіодна задача залежних тримає ВЕСЬ рік документа; ✎ огляд b11-2 (A): «з затвердженими» — теж (її прогін будить читачів
    /// в інших періодах, а прохід читача пропускає затверджені аркуші).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Поперіодна_задача_залежних_і_з_затвердженими_тримають_рік()
    {
        var doc = NextDocument();
        await InsertAsync(
            doc, Key(CalculationTrigger.DependentsTargetOf(doc, new PeriodKey(202604))), "Queued",
            $$"""{"projectId":0,"documentId":{{doc}},"periodKey":202604}""");
        Assert.True(await PendingAsync(doc, 202604));
        Assert.True(await PendingAsync(doc, 202601));
        Assert.True(await PendingAsync(doc, 202605));
        Assert.False(await PendingAsync(doc, 202704));

        var approvedOnly = NextDocument();
        await InsertAsync(
            approvedOnly, Key(CalculationTrigger.DependentsApprovedTargetOf(approvedOnly, new PeriodKey(202608))), "Queued",
            $$"""{"projectId":0,"documentId":{{approvedOnly}},"periodKey":202608,"includeApproved":true}""");
        Assert.True(await PendingAsync(approvedOnly, 202608));
        Assert.True(await PendingAsync(approvedOnly, 202601));
        Assert.False(await PendingAsync(approvedOnly, 202701));
    }

    /// <summary>
    /// ✎ Огляд b11-2 (A, урок 498): БУДЬ-ЯКА жива задача перерахунку чи формул документа тримає рік своєї цілі — власний перерахунок
    /// (<c>-auto</c>), натискання «Перерахувати» місяця, задача формул людини (<c>-formula-u{id}</c>); інший рік — ні.
    /// </summary>
    /// <remarks>Мутація: <c>HoldsYear</c> повертає «не тримає» для <c>-auto</c>/натискання/формул людини — червоні рядки.</remarks>
    [Theory]
    [InlineData("auto")]
    [InlineData("press")]
    [InlineData("user-formula")]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Будь_яка_задача_документа_тримає_рік(string kind)
    {
        var doc = NextDocument();
        var (target, payload) = kind switch
        {
            "auto" => (Key($"doc{doc}-p202604-auto"), $$"""{"projectId":0,"documentId":{{doc}},"periodKey":202604}"""),
            "press" => (Key($"doc{doc}-p202604"), $$"""{"projectId":0,"documentId":{{doc}},"periodKey":202604,"triggeredByUserId":5}"""),
            _ => (DbBackgroundJobScheduler.TargetKeyOf<IFormulaRecalculationJob>(FormulaRecalculationTarget.OfDocument(doc, 5)),
                $$"""{"documentId":{{doc}},"tableInstanceId":0,"periodKey":202604,"cells":[{"rowId":1,"columnDefId":1,"tableDefId":1,"periodKey":202604}]}"""),
        };
        await InsertAsync(doc, target, "Queued", payload);

        Assert.True(await PendingAsync(doc, 202601));
        Assert.True(await PendingAsync(doc, 202612));
        Assert.False(await PendingAsync(doc, 202701));
    }

    /// <summary>
    /// ✎ Огляд b11 (HIGH-2): системна задача формул документа (читачі <c>CrossPeriodFanOut</c> після прогону власника) тримає рік
    /// своїх комірок — пізніший місяць і якір; комірки іншого року — ні; ✎ b11-2: задача формул людини — теж тримає.
    /// </summary>
    /// <remarks>Мутація: <c>CascadeSql</c> без цілі задачі формул — червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Задача_формул_читачів_тримає_рік_своїх_комірок()
    {
        var doc = NextDocument();
        var system = DbBackgroundJobScheduler.TargetKeyOf<IFormulaRecalculationJob>(FormulaRecalculationTarget.OfDocument(doc, null));
        await InsertAsync(
            doc, system, "Queued",
            $$"""{"documentId":{{doc}},"tableInstanceId":0,"periodKey":202611,"cells":[{"rowId":0,"columnDefId":1,"tableDefId":1,"periodKey":202611}]}""");
        Assert.True(await PendingAsync(doc, 202611));
        Assert.True(await PendingAsync(doc, 202601));
        Assert.False(await PendingAsync(doc, 202701));

        var other = NextDocument();
        await InsertAsync(
            other, DbBackgroundJobScheduler.TargetKeyOf<IFormulaRecalculationJob>(FormulaRecalculationTarget.OfDocument(other, null)), "Queued",
            $$"""{"documentId":{{other}},"tableInstanceId":0,"periodKey":202711,"cells":[{"rowId":0,"columnDefId":1,"tableDefId":1,"periodKey":202711}]}""");
        Assert.False(await PendingAsync(other, 202611));
        Assert.True(await PendingAsync(other, 202711));

        // ✎ Огляд b11-2 (A): задача формул ЛЮДИНИ теж тримає рік своїх комірок.
        var user = NextDocument();
        await InsertAsync(
            user, DbBackgroundJobScheduler.TargetKeyOf<IFormulaRecalculationJob>(FormulaRecalculationTarget.OfDocument(user, 5)), "Queued",
            $$"""{"documentId":{{user}},"tableInstanceId":0,"periodKey":202611,"cells":[{"rowId":1,"columnDefId":1,"tableDefId":1,"periodKey":202611}]}""");
        Assert.True(await PendingAsync(user, 202611));
        Assert.True(await PendingAsync(user, 202601));
    }
    /// <summary>
    /// ✎ b11 (огляд координатора 24.11): причина для тексту відмови. Каскад залежних документа (рік і поперіодний) — каскад від
    /// джерела (ключ D-567); власний перерахунок (<c>-auto</c>, натискання, формули людини й системні, SHEETNAME) —
    /// <see cref="CascadePendingReason.OwnRecalculation"/>; обидва разом — каскад джерела.
    /// </summary>
    /// <remarks>Мутація: <c>IsDependentsCascade</c> → <c>false</c> — червоні рядки каскаду; → <c>true</c> — червоні власні.</remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Причина_каскад_джерела_чи_власний_перерахунок()
    {
        var cascade = NextDocument();
        await InsertAsync(cascade, Target(cascade), "Queued", Payload(cascade, cascade: [202603]));
        Assert.Equal(CascadePendingReason.SourceCascade, await ReasonAsync(cascade, 202603));

        var perPeriod = NextDocument();
        await InsertAsync(
            perPeriod, Key(CalculationTrigger.DependentsTargetOf(perPeriod, new PeriodKey(202604))), "Running",
            $$"""{"projectId":0,"documentId":{{perPeriod}},"periodKey":202604}""");
        Assert.Equal(CascadePendingReason.SourceCascade, await ReasonAsync(perPeriod, 202604));

        var auto = NextDocument();
        await InsertAsync(auto, Key($"doc{auto}-p202604-auto"), "Queued", $$"""{"projectId":0,"documentId":{{auto}},"periodKey":202604}""");
        Assert.Equal(CascadePendingReason.OwnRecalculation, await ReasonAsync(auto, 202604));

        var press = NextDocument();
        await InsertAsync(press, Key($"doc{press}-p202611"), "Running", $$"""{"projectId":0,"documentId":{{press}},"periodKey":202611,"triggeredByUserId":5}""");
        Assert.Equal(CascadePendingReason.OwnRecalculation, await ReasonAsync(press, 202611));

        var formula = NextDocument();
        await InsertAsync(
            formula, DbBackgroundJobScheduler.TargetKeyOf<IFormulaRecalculationJob>(FormulaRecalculationTarget.OfDocument(formula, null)), "Queued",
            $$"""{"documentId":{{formula}},"tableInstanceId":0,"periodKey":202611,"cells":[{"rowId":0,"columnDefId":1,"tableDefId":1,"periodKey":202611}]}""");
        Assert.Equal(CascadePendingReason.OwnRecalculation, await ReasonAsync(formula, 202611));

        // Каскад і власний разом — текст про джерело (саме каскад закріпив би застарілі числа джерела).
        await InsertAsync(auto, Target(auto), "Queued", Payload(auto, cascade: [202604]));
        Assert.Equal(CascadePendingReason.SourceCascade, await ReasonAsync(auto, 202604));

        Assert.Equal(CascadePendingReason.None, await ReasonAsync(NextDocument(), 202604));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static long NextDocument() => Interlocked.Increment(ref _doc);

    private static string Target(long doc) => Key(CalculationTrigger.SourceCascadeTargetOf(doc, Year));

    /// <summary>Ціль так, як її пише черга в базі (`IRecalculationJob~…`, огляд b9).</summary>
    private static string Key(string target) => DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>(target);

    private static string Payload(
        long doc, int[] cascade, int? chainAfter = null, int[]? chainPlan = null, int[]? append = null)
    {
        static string Arr(int[] a) => "[" + string.Join(',', a.Select(x => x.ToString(CultureInfo.InvariantCulture))) + "]";
        var parts = new List<string>
        {
            "\"projectId\":0",
            $"\"documentId\":{doc.ToString(CultureInfo.InvariantCulture)}",
            $"\"year\":{Year.ToString(CultureInfo.InvariantCulture)}",
            "\"recalculateSubmitted\":true",
            $"\"cascadePeriods\":{Arr(cascade)}",
        };
        if (chainAfter is { } after)
        {
            parts.Add($"\"chainAfter\":{after.ToString(CultureInfo.InvariantCulture)}");
        }

        if (chainPlan is not null)
        {
            parts.Add($"\"chainPlan\":{Arr(chainPlan)}");
        }

        if (append is not null)
        {
            parts.Add($"\"cascadeAppend\":{Arr(append)}");
        }

        return "{" + string.Join(',', parts) + "}";
    }

    private async Task<bool> PendingAsync(long doc, int periodKey)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        return await new SourceCascadePendingGate(db).IsPendingAsync(doc, periodKey, CancellationToken.None);
    }

    private async Task<CascadePendingReason> ReasonAsync(long doc, int periodKey)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        return await new SourceCascadePendingGate(db).GetPendingReasonAsync(doc, periodKey, CancellationToken.None);
    }

    private async Task InsertAsync(long doc, string target, string state, string payload)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO itg.JobProgress
                (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt, DocumentId, TargetKey, Payload)
            VALUES (@id, N'Ecr.Test.F5Cascade', @state, 0, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(),
                    @doc, @target, @payload);
            """;
        command.Parameters.AddWithValue("@id", $"f5-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@doc", doc);
        command.Parameters.AddWithValue("@target", target);
        command.Parameters.AddWithValue("@payload", payload);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetAsync(long doc, string state, string payload)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE itg.JobProgress SET [State] = @state, Payload = @payload WHERE DocumentId = @doc AND TargetKey = @target;";
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@doc", doc);
        command.Parameters.AddWithValue("@target", Target(doc));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
