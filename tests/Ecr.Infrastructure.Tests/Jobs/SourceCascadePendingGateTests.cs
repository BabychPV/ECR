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
/// Мутації (локально, не комітяться): <c>IsPendingAsync</c> → <c>false</c> (ворота вимкнено) — червоні «чекає»;
/// <c>Pends</c> → <c>true</c> (місяць ігнорується) — червоні «інший місяць», «крок завершився», «між кроками».
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

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Задача_у_вікні_злиття_тримає_свої_місяці_й_пізніші_а_ранніші_ні()
    {
        var doc = NextDocument();
        await InsertAsync(doc, Target(doc), "Queued", Payload(doc, cascade: [202603, 202605]));

        Assert.True(await PendingAsync(doc, 202603));
        Assert.True(await PendingAsync(doc, 202605));

        // План ще не ухвалено: пізніші місяці можуть читати обчислені числа (D-566 п. 2) — чекають.
        Assert.True(await PendingAsync(doc, 202604));
        Assert.True(await PendingAsync(doc, 202612));

        // Раніший місяць того самого документа в план не потрапить.
        Assert.False(await PendingAsync(doc, 202602));

        // Інший рік і інший документ — не зачеплені.
        Assert.False(await PendingAsync(doc, 202703));
        Assert.False(await PendingAsync(NextDocument(), 202603));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Ланцюжок_тримає_лише_решту_плану_крок_що_завершився_звільняє_місяць()
    {
        var doc = NextDocument();

        // Крок 202605 виконується; 202603 уже зроблено; cascadePeriods продовження несе перші періоди далі.
        await InsertAsync(
            doc, Target(doc), "Running",
            Payload(doc, cascade: [202603], chainAfter: 202603, chainPlan: [202605, 202607]));

        Assert.False(await PendingAsync(doc, 202603)); // крок завершився
        Assert.False(await PendingAsync(doc, 202604)); // раніший за крок, що йде, не в плані
        Assert.True(await PendingAsync(doc, 202605));  // крок, що виконується
        Assert.True(await PendingAsync(doc, 202606));  // крок може дописати пізнішого читача
        Assert.True(await PendingAsync(doc, 202607));

        // Між кроками (Queued, наступний — 202607): 202605 звільнено, 202606 не в плані.
        await SetAsync(doc, "Queued", Payload(doc, cascade: [202603], chainAfter: 202605, chainPlan: [202607]));
        Assert.False(await PendingAsync(doc, 202605));
        Assert.False(await PendingAsync(doc, 202606));
        Assert.True(await PendingAsync(doc, 202607));

        // У ланцюжок злилася нова правка (cascadeAppend 202604): дописується завжди, і пізніші — до нового плану.
        await SetAsync(
            doc, "Queued",
            Payload(doc, cascade: [202603], chainAfter: 202605, chainPlan: [202607], append: [202604]));
        Assert.True(await PendingAsync(doc, 202604));
        Assert.True(await PendingAsync(doc, 202605));
        Assert.False(await PendingAsync(doc, 202603));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Поперіодна_задача_залежних_тримає_свій_період_з_затвердженими_не_тримає()
    {
        var doc = NextDocument();
        await InsertAsync(
            doc, Key(CalculationTrigger.DependentsTargetOf(doc, new PeriodKey(202604))), "Queued",
            $$"""{"projectId":0,"documentId":{{doc}},"periodKey":202604}""");
        await InsertAsync(
            doc, Key(CalculationTrigger.DependentsApprovedTargetOf(doc, new PeriodKey(202608))), "Queued",
            $$"""{"projectId":0,"documentId":{{doc}},"periodKey":202608,"includeApproved":true}""");

        Assert.True(await PendingAsync(doc, 202604));
        Assert.False(await PendingAsync(doc, 202605));

        // «З затвердженими» переписує й затверджені — затвердження нею не блокується.
        Assert.False(await PendingAsync(doc, 202608));
    }

    [Theory]
    [InlineData(null, 202603, true)]
    [InlineData("не json", 202603, true)]
    [InlineData("""{"documentId":1,"year":2026}""", 202601, true)]
    [InlineData("""{"documentId":1,"year":2026,"cascadePeriods":[],"cascadeSeeds":[202604]}""", 202604, false)]
    [InlineData("""{"documentId":1,"year":2026,"cascadePeriods":[],"cascadeSeeds":[202604]}""", 202605, true)]
    [InlineData("""{"documentId":1,"year":2026,"cascadePeriods":[202603],"chainAfter":202603}""", 202604, true)]
    [InlineData("""{"documentId":1,"year":2026,"cascadePeriods":[202603],"chainAfter":202603}""", 202603, false)]
    [Trait("Requirement", "F5")]
    public void Pends_консервативний_до_плану_і_на_чужій_формі(string? payload, int periodKey, bool expected)
        => Assert.Equal(expected, SourceCascadePendingGate.Pends(running: false, payload, periodKey));

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
