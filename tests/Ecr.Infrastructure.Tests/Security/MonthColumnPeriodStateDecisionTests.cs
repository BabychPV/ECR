// tests/Ecr.Infrastructure.Tests/Security/MonthColumnPeriodStateDecisionTests.cs
using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// P2-A (слово людини 26.10: «заборона лише для поданих/погоджених/закритих»), рівень служби доступу на справжній
/// базі: колонка місяця k таблиці <c>MonthsInColumns</c> підлягає стану СВОГО періоду k, а не лише періоду
/// екземпляра таблиці (202610).
/// </summary>
/// <remarks>
/// ⛔ На старому коді: усі колонки-місяці отримували рішення за станом періоду екземпляра (Open) — закритий M3,
/// поданий M9 і погоджений M7 були редаговними. Мутаційні докази (detached-worktree, не комітяться): (1) у
/// <c>Decide</c> прибрати підміну <c>shared = monthContext.Context</c> — червоніють M3/M7/M9/M8; (2) у
/// <c>MonthStatesAsync</c> прибрати <c>Scheduled → Open</c> — червоніє M12 (внесення наперед); (3) прибрати
/// <c>!profile.IsIntegrationWriter</c> зі <c>SliceContextsAsync</c> — червоніє «писач інтеграції не зачеплений».
/// </remarks>
[Collection("SqlServer")]
public sealed class MonthColumnPeriodStateDecisionTests(SqlServerFixture sql) : IDisposable
{
    private const int Oct = 202610;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    public void Dispose() => _memory.Dispose();

    /// <summary>Колонки: 0 — звичайна; 1..7 — місяці 3, 7, 8, 9, 10, 11, 12 (див. <see cref="ArrangeAsync"/>).</summary>
    private sealed record World(TestDocumentBuilder Builder, TestDocument Doc)
    {
        public CellAddress Plain => Cell(0);

        public CellAddress M3Closed => Cell(1);

        public CellAddress M7Approved => Cell(2);

        public CellAddress M8Grace => Cell(3);

        public CellAddress M9Submitted => Cell(4);

        public CellAddress M10Own => Cell(5);

        public CellAddress M11Open => Cell(6);

        public CellAddress M12Scheduled => Cell(7);

        public CellAddress[] All => [Plain, M3Closed, M7Approved, M8Grace, M9Submitted, M10Own, M11Open, M12Scheduled];

        private CellAddress Cell(int index) => new(Doc.PeriodKey, Doc.RowIds[0], Doc.ColumnDefIds[index]);
    }

    private AccessDecisionService Service(EcrDbContext db)
        => new(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));

    private static AccessProfile Writer(World w)
        => new AccessBuilder { UserId = 9001 }.Grant(ResourceKind.Project, w.Doc.ProjectId, GrantLevel.Write).Build();

    /// <summary>
    /// Документ у жовтні (Open); періоди: 03 Closed, 07 Open + Approved, 08 Grace, 09 Open + Submitted, 11 Open,
    /// 12 Scheduled. <paramref name="monthsInColumns"/> = <c>false</c> — та сама схема, але таблиця НЕ
    /// <c>MonthsInColumns</c> (колонка місяця там не є колонкою періоду).
    /// </summary>
    private async Task<World> ArrangeAsync(bool monthsInColumns = true)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(Oct, columnCount: 8, rowCount: 1, ct: CancellationToken.None);

        byte[] months = [3, 7, 8, 9, 10, 11, 12];
        for (var i = 0; i < months.Length; i++)
        {
            await Execute(
                "UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = @m WHERE Id = @id",
                ("@id", doc.ColumnDefIds[i + 1]), ("@m", months[i]));
        }

        await Execute(
            "UPDATE cfg.TableDef SET LayoutKind = @kind WHERE Id = @id",
            ("@id", doc.TableDefId),
            ("@kind", (byte)(monthsInColumns ? TableLayoutKind.MonthsInColumns : TableLayoutKind.PerPeriodInstance)));

        await using var db = builder.CreateContext();

        (await db.Periods.FirstAsync(
            p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Oct, CancellationToken.None))
            .AdvanceTo(PeriodState.Open, Now);

        foreach (var (month, state) in new (byte, PeriodState)[]
                 {
                     (3, PeriodState.Closed), (7, PeriodState.Open), (8, PeriodState.Grace), (9, PeriodState.Open),
                     (11, PeriodState.Open), (12, PeriodState.Scheduled),
                 })
        {
            var period = new Period(
                doc.ProjectId, new PeriodKey(2026 * 100 + month), month,
                new DateOnly(2026, month, 1), new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)));
            period.AdvanceTo(state, Now);
            db.Periods.Add(period);
        }

        var approved = new ApprovalState(doc.DocumentId, doc.SheetDefId, 202607);
        approved.Submit(userId: 1, Now);
        approved.Approve(userId: 2, Now);
        var submitted = new ApprovalState(doc.DocumentId, doc.SheetDefId, 202609);
        submitted.Submit(userId: 1, Now);
        db.ApprovalStates.AddRange(approved, submitted);

        await db.SaveChangesAsync(CancellationToken.None);

        return new World(builder, doc);
    }

    private async Task Execute(string sqlText, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyDictionary<CellAddress, EditDecision>> BatchAsync(World w, AccessProfile profile)
    {
        await using var db = w.Builder.CreateContext();
        return await Service(db).CanEditCellsAsync(
            profile, w.Doc.TableInstanceId, w.Doc.PeriodKey, w.All, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Колонка_місяця_підлягає_стану_свого_періоду_закритий_поданий_погоджений_відмова_решта_дозвіл()
    {
        var w = await ArrangeAsync();

        var decisions = await BatchAsync(w, Writer(w));

        // Закритий період колонки — PeriodClosed (той самий код, що й запис у закритий період).
        Assert.False(decisions[w.M3Closed].IsAllowed);
        Assert.Equal(EditDenyReason.PeriodClosed, decisions[w.M3Closed].Reason);

        // Поданий і погоджений — DocumentSubmitted / DocumentApproved (як запис у той період).
        Assert.False(decisions[w.M9Submitted].IsAllowed);
        Assert.Equal(EditDenyReason.DocumentSubmitted, decisions[w.M9Submitted].Reason);
        Assert.False(decisions[w.M7Approved].IsAllowed);
        Assert.Equal(EditDenyReason.DocumentApproved, decisions[w.M7Approved].Reason);

        // Grace — дозволено (запис ставить IsLateEdit за станом періоду колонки: див. MonthColumnPeriodStatePatchTests).
        Assert.True(decisions[w.M8Grace].IsAllowed);

        // Open колонки — як зараз.
        Assert.True(decisions[w.M11Open].IsAllowed);

        // Scheduled (майбутній місяць) — внесення наперед ДОЗВОЛЕНО.
        Assert.True(decisions[w.M12Scheduled].IsAllowed);

        // Власний місяць екземпляра і звичайна колонка — період екземпляра, без підміни.
        Assert.True(decisions[w.M10Own].IsAllowed);
        Assert.True(decisions[w.Plain].IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Зріз_і_пакет_дають_однакові_рішення_сітка_сіра_рівно_там_де_запис_відмовить()
    {
        var w = await ArrangeAsync();
        var profile = Writer(w);

        var batch = await BatchAsync(w, profile);

        await using var db = w.Builder.CreateContext();
        var slice = await Service(db).CanEditSliceAsync(profile, w.Doc.TableInstanceId, CancellationToken.None);

        foreach (var address in w.All)
        {
            Assert.Equal(batch[address], slice[address]);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Погоджувач_не_править_поданий_місяць_іншого_періоду_через_екземпляр()
    {
        var w = await ArrangeAsync();
        var approver = new AccessBuilder { UserId = 9002 }
            .Grant(ResourceKind.Project, w.Doc.ProjectId, GrantLevel.Approve)
            .Build();

        var decisions = await BatchAsync(w, approver);

        // D-442 (правка погоджувача) діє на періоді екземпляра; на колонку іншого місяця — ні.
        Assert.Equal(EditDenyReason.DocumentSubmitted, decisions[w.M9Submitted].Reason);
        Assert.Equal(EditDenyReason.DocumentApproved, decisions[w.M7Approved].Reason);
        Assert.False(decisions[w.M9Submitted].ByApprover);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Писач_інтеграції_не_зачеплений_закритий_і_поданий_місяць_як_і_раніше_за_періодом_екземпляра()
    {
        var w = await ArrangeAsync();
        // Як `AccessDecisionService.IntegrationWriter`: порожні гранти й ролі, права визначає контекст задачі.
        var integration = new AccessProfile
        {
            CacheKey = "u9003:s1|integration-job",
            UserId = 9003,
            SecurityStamp = "s1",
            Permissions = new HashSet<string>(StringComparer.Ordinal),
            Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
            Denies = new HashSet<string>(StringComparer.Ordinal),
            RoleIds = new HashSet<int>(),
            IsIntegrationWriter = true,
        };

        var decisions = await BatchAsync(w, integration);

        // Рішення ті самі, що були б без підміни: за станом періоду екземпляра (Open) і аркуша 202610 (Draft).
        Assert.True(decisions[w.M3Closed].IsAllowed);
        Assert.True(decisions[w.M9Submitted].IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Таблиця_не_MonthsInColumns_колонка_місяця_не_є_колонкою_періоду()
    {
        var w = await ArrangeAsync(monthsInColumns: false);

        var decisions = await BatchAsync(w, Writer(w));

        foreach (var address in w.All)
        {
            Assert.True(decisions[address].IsAllowed, $"{address}: {decisions[address].Reason}");
        }
    }
}
