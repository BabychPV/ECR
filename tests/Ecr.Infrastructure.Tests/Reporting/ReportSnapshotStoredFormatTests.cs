// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotStoredFormatTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Формат суми зрізу ЗБЕРІГАЄТЬСЯ в <c>rpt.ReportSnapshot.HashFormat</c> (рішення
/// 2026-09-21): його пишуть звірка й нічна задача, а перелік лише читає.
/// </summary>
/// <remarks>
/// ⚠ Засіяні тут зрізи — «старі»: <c>SeedAsync</c> пише суму повз побудову, тож
/// колонка лишається <c>NULL</c>, як у зрізів до міграції.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportSnapshotStoredFormatTests(SqlServerFixture sql)
{
    private const int Viewer = 9;
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Звірка_старого_зрізу_записує_legacy_у_колонку()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var id = await SeedLegacyAsync(chain);

        var response = await VerifyAsync(chain, id);

        Assert.Equal("legacy", response.MatchedFormat);
        Assert.Equal("legacy", await StoredAsync(chain, id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Звірка_не_перезаписує_вже_відомий_формат()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var id = await SeedLegacyAsync(chain);
        await SetStoredAsync(chain, id, "current");

        var response = await VerifyAsync(chain, id);

        // Відповідь звірки — правда цього перерахунку; колонку вона не переписує.
        Assert.Equal("legacy", response.MatchedFormat);
        Assert.Equal("current", await StoredAsync(chain, id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нічна_задача_класифікує_старі_зрізи_а_зріз_без_суми_лишає_NULL()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var legacy = await SeedLegacyAsync(chain);
        var current = await ReportSnapshotVerifyTests.SeedAsync(chain);
        var noHash = await ReportSnapshotVerifyTests.SeedAsync(chain, ReportSnapshotVerifyTests.LegacyRows, _ => null);

        await using (var db = chain.CreateContext())
        {
            var job = new ReportSnapshotFormatJob(db, new ReportSnapshotBuilder(db, new TestClock(Now)), new TestClock(Now));
            await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        Assert.Equal("legacy", await StoredAsync(chain, legacy));
        Assert.Equal("current", await StoredAsync(chain, current));
        Assert.Null(await StoredAsync(chain, noHash));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_читає_колонку_одним_запитом_і_нічого_не_перераховує()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);

        // Звірка цього зрізу дала б `legacy`; перелік, що перераховує, показав би те саме.
        var id = await SeedLegacyAsync(chain);

        var (unknown, unknownQueries) = await FormatInListAsync(id);
        Assert.Equal("unknown", unknown);
        Assert.Null(await StoredAsync(chain, id));

        await SetStoredAsync(chain, id, "legacy");
        var (stored, storedQueries) = await FormatInListAsync(id);
        Assert.Equal("legacy", stored);

        // Рівно один SELECT rpt.ReportSnapshot на запит переліку, жодного читання рядків.
        Assert.Equal(1, unknownQueries.Total);
        Assert.Equal(1, storedQueries.Total);
        Assert.Equal(0, unknownQueries["SELECT rpt.ReportRow"]);
    }

    private static Task<long> SeedLegacyAsync(TestDocumentBuilder chain)
        => ReportSnapshotVerifyTests.SeedAsync(
            chain, ReportSnapshotVerifyTests.LegacyRows, ReportSnapshotVerifyTests.HashBeforeBe17);

    private static async Task<SnapshotVerifyResponse> VerifyAsync(TestDocumentBuilder chain, long id)
    {
        await using var db = chain.CreateContext();
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));
        var (access, user) = await ViewerAsync(builder, id);

        return await new VerifyReportSnapshotHandler(builder, access, user).HandleAsync(id, CancellationToken.None);
    }

    /// <summary>Позначка зрізу в переліку його проєкту і скільки запитів до бази зробив перелік.</summary>
    private async Task<(string Format, CommandTallySnapshot Queries)> FormatInListAsync(long id)
    {
        var counter = new DbCommandCounter();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(counter)
            .Options);

        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));
        var (access, user) = await ViewerAsync(builder, id);
        var projectId = (await builder.FindProjectIdAsync(id, CancellationToken.None))!.Value;

        counter.Tally.Reset();
        var list = await new ListReportSnapshotsHandler(builder, access, user)
            .HandleAsync(projectId, periodKey: null, CancellationToken.None);

        return (Assert.Single(list, s => s.Id == id).HashFormat, counter.Tally.Snapshot());
    }

    /// <summary>Користувач із правом перегляду й грантом на проєкт зрізу.</summary>
    private static async Task<(IAccessDecisionService, ICurrentUser)> ViewerAsync(ReportSnapshotBuilder builder, long id)
    {
        var projectId = (await builder.FindProjectIdAsync(id, CancellationToken.None))!.Value;

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Viewer);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Viewer, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p",
            UserId = Viewer,
            SecurityStamp = "s",
            Permissions = new HashSet<string>([ListReportSnapshotsHandler.Permission], StringComparer.Ordinal),
            Grants = new Dictionary<string, GrantLevel> { [$"{ResourceKind.Project}:{projectId}"] = GrantLevel.Read },
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
        });

        return (access, user);
    }

    private static async Task<string?> StoredAsync(TestDocumentBuilder chain, long id)
    {
        await using var db = chain.CreateContext();
        return await db.ReportSnapshots.AsNoTracking().Where(s => s.Id == id).Select(s => s.HashFormat).SingleAsync();
    }

    private static async Task SetStoredAsync(TestDocumentBuilder chain, long id, string format)
    {
        await using var db = chain.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE rpt.ReportSnapshot SET HashFormat = {format} WHERE Id = {id}");
    }
}
