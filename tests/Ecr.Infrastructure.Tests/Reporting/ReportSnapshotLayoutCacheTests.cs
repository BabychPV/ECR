// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotLayoutCacheTests.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// P4 (перф-аудит): сторінка зрізу з макетом (<c>R8</c>) не перечитує весь
/// зріз — розкладений зріз лежить у спільному кеші процесу. І кеш не змішує
/// ні зрізів між собою, ні доступу між користувачами.
/// </summary>
/// <remarks>
/// Кожен «запит» — окремий <see cref="EcrDbContext"/> і окремий будівник поверх
/// ОДНОГО <see cref="IMemoryCache"/>: так живе застосунок (будівник Scoped, кеш
/// Singleton). Читання рахує <see cref="DbCommandCounter"/> на контексті — він
/// бачить лише свій контекст, тож чужі тести в процесі число не псують.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportSnapshotLayoutCacheTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Категорія читання комірок зрізу в лічильнику.</summary>
    private const string ReadCells = "SELECT rpt.ReportRow";

    private static readonly ReportColumnCommand[] Columns =
    [
        new("OutputCode", "text", new Dictionary<string, string> { ["en"] = "Output", ["ru"] = "Выброс" }),
        new("Value", "number"),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P4")]
    public async Task Друга_сторінка_того_самого_зрізу_не_читає_комірок_із_бази()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var (snapshotId, _) = await BuildTwoAsync(chain);
        using var memory = new MemoryCache(new MemoryCacheOptions());

        var first = await CountedAsync(memory, b => b.RowsAsync(snapshotId, 0, 1, "en", CancellationToken.None));
        var second = await CountedAsync(
            memory, b => b.RowsAsync(snapshotId, first.Page!.NextCursor!.Value, 1, "en", CancellationToken.None));

        // Холодний кеш читає зріз рівно раз — інакше «0» нижче нічого б не довів.
        Assert.True(first.Seen[ReadCells] == 1, first.Seen.Format());

        // ⛔ Храповик P4: друга сторінка — жодного читання комірок. До P4 тут
        // було повне читання зрізу (до MaxRows рядків) на КОЖНУ сторінку.
        Assert.True(second.Seen[ReadCells] == 0, second.Seen.Format());
        Assert.True(second.Seen.Total > 0, "Друга сторінка мусить хоч раз сходити до бази — по опис версії й існування зрізу.");

        // І відповідь та сама, що й без кешу: порядок груп, підсумок по всьому зрізу.
        Assert.Equal("E_CO2", Assert.Single(first.Page!.Rows).Cells["OutputCode"]);
        Assert.Equal("E_NOX", Assert.Single(second.Page!.Rows).Cells["OutputCode"]);
        Assert.Null(second.Page.NextCursor);
        Assert.Equal(17.5m, Assert.Single(second.Page.Totals!).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P4")]
    public async Task Два_зрізи_в_одному_кеші_не_змішуються()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);

        // Ті самі дані, різні макети: групування за кодом ставить E_CO2 першим,
        // групування за значенням — E_NOX (5 < 12.5).
        var (byCode, byValue) = await BuildTwoAsync(chain);
        using var memory = new MemoryCache(new MemoryCacheOptions());

        await CountedAsync(memory, b => b.RowsAsync(byCode, 0, 10, "en", CancellationToken.None));
        await CountedAsync(memory, b => b.RowsAsync(byValue, 0, 10, "en", CancellationToken.None));

        var code = await CountedAsync(memory, b => b.RowsAsync(byCode, 0, 10, "en", CancellationToken.None));
        var value = await CountedAsync(memory, b => b.RowsAsync(byValue, 0, 10, "en", CancellationToken.None));

        // Обидва з кешу — і кожен свій.
        Assert.Equal(0, code.Seen[ReadCells]);
        Assert.Equal(0, value.Seen[ReadCells]);
        Assert.Equal(["E_CO2", "E_NOX"], code.Page!.Rows.Select(r => r.Cells["OutputCode"]));
        Assert.Equal(["E_NOX", "E_CO2"], value.Page!.Rows.Select(r => r.Cells["OutputCode"]));
        Assert.Equal(["OutputCode"], code.Page.Groups!.Select(g => g.Column).Distinct());
        Assert.Equal(["Value"], value.Page.Groups!.Select(g => g.Column).Distinct());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P4")]
    public async Task Кеш_прогрітий_одним_користувачем_не_відкриває_зріз_іншому_і_не_переносить_мову()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var (snapshotId, _) = await BuildTwoAsync(chain);
        int projectId;

        await using (var db = chain.CreateContext())
        {
            projectId = await db.ReportSnapshots.AsNoTracking()
                .Where(s => s.Id == snapshotId).Select(s => s.ProjectId).SingleAsync();
        }

        using var memory = new MemoryCache(new MemoryCacheOptions());
        var granted = new Dictionary<string, GrantLevel> { [$"{ResourceKind.Project}:{projectId}"] = GrantLevel.Read };

        // Користувач із грантом прогріває кеш російською.
        var warm = await HandledAsync(memory, userId: 9, "ru", granted, snapshotId);
        Assert.Equal(["Выброс", "Value"], warm.Page!.Columns.Select(c => c.Name));

        // ⛔ Користувач без гранта на проєкт — той самий 404, що й без кешу, і
        // до комірок зрізу він не доходить узагалі: доступ перевіряє обробник
        // ДО будівника, а в кеші немає нічого, що б цю перевірку обходило.
        var denied = await HandledAsync(memory, userId: 10, "ru", grants: [], snapshotId);
        Assert.IsType<NotFoundException>(denied.Error);
        Assert.Equal(0, denied.Seen[ReadCells]);

        // ⛔ Рішення людини 2026-09-29: грант Read і перелік (`Report.ViewRegulatory`)
        // без права на вміст — 403, і прогрітий кеш вмісту не віддає: перевірка
        // права стоїть в обробнику ДО будівника.
        var noContent = await HandledAsync(
            memory, userId: 12, "ru", granted, snapshotId, [ListReportSnapshotsHandler.Permission]);
        var refused = Assert.IsType<AccessDeniedException>(noContent.Error);
        Assert.Equal(GetSnapshotRowsHandler.ContentPermission, refused.Details!["permission"]);
        Assert.Null(noContent.Page);
        Assert.Equal(0, noContent.Seen[ReadCells]);

        // Інший користувач із грантом отримує вміст із кешу, але СВОЄЮ мовою:
        // мова й сторінка застосовуються на кожен запит, поза кешем.
        var other = await HandledAsync(memory, userId: 11, "en", granted, snapshotId);
        Assert.Equal(0, other.Seen[ReadCells]);
        Assert.Equal(["Output", "Value"], other.Page!.Columns.Select(c => c.Name));
    }

    private async Task<(SnapshotRowsPage? Page, CommandTallySnapshot Seen)> CountedAsync(
        IMemoryCache memory, Func<ReportSnapshotBuilder, Task<SnapshotRowsPage?>> act)
    {
        var counter = new DbCommandCounter();
        await using var db = Counted(counter);

        var page = await act(new ReportSnapshotBuilder(db, new TestClock(Now), memory));

        return (page, counter.Tally.Snapshot());
    }

    private async Task<(SnapshotRowsPage? Page, Exception? Error, CommandTallySnapshot Seen)> HandledAsync(
        IMemoryCache memory, int userId, string language, Dictionary<string, GrantLevel> grants, long snapshotId,
        string[]? permissions = null)
    {
        var counter = new DbCommandCounter();
        await using var db = Counted(counter);

        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();

        user.UserId.Returns(userId);
        user.Language.Returns(language);
        access.BuildProfileAsync(userId, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = $"u{userId}",
            UserId = userId,
            SecurityStamp = "s",
            Permissions = new HashSet<string>(
                permissions ?? [ListReportSnapshotsHandler.Permission, GetSnapshotRowsHandler.ContentPermission],
                StringComparer.Ordinal),
            Grants = grants,
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
        });

        var handler = new GetSnapshotRowsHandler(new ReportSnapshotBuilder(db, new TestClock(Now), memory), access, user);

        try
        {
            return (await handler.HandleAsync(snapshotId, null, null, CancellationToken.None), null, counter.Tally.Snapshot());
        }
        catch (NotFoundException error)
        {
            return (null, error, counter.Tally.Snapshot());
        }
        catch (AccessDeniedException error)
        {
            return (null, error, counter.Tally.Snapshot());
        }
    }

    private EcrDbContext Counted(DbCommandCounter counter)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(counter)
            .Options);

    /// <summary>Два зрізи тих самих даних: групування за кодом і за значенням.</summary>
    private static async Task<(long ByCode, long ByValue)> BuildTwoAsync(TestDocumentBuilder chain)
    {
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db);
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now), new MemoryCache(new MemoryCacheOptions()));

        var byCode = await PublishedAsync(db, new("OutputCode", [new("Value", "sum")], ShowGroupHeader: true));
        var byValue = await PublishedAsync(db, new("Value", [new("Value", "sum")]));

        return (
            await builder.BuildAsync(byCode.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None),
            await builder.BuildAsync(byValue.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None));
    }

    private sealed record Seeded(int ProjectId, PeriodKey PeriodKey);

    /// <summary>Чинний прогін із двома результатами (як у <c>ReportSnapshotLayoutTests</c>).</summary>
    private static async Task<Seeded> SeedResultsAsync(TestDocumentBuilder chain, EcrDbContext db)
    {
        var document = await chain.BuildAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var methodology = new Methodology(
            EcrCode.Create($"RPTC_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var methodologyVersion = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(methodologyVersion);

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now, "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).FirstAsync();

        foreach (var (rowKey, output, value) in new[] { ("row-1", "E_CO2", 12.5m), ("row-2", "E_NOX", 5m) })
        {
            var text = value.ToString(CultureInfo.InvariantCulture);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO calc.CalculationResult
                    (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
                VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {methodologyVersion.Id},
                        {document.PeriodKey.Value}, {document.DocumentId}, {rowKey}, {output},
                        CAST({text} AS decimal(34,16)), {unit.Id})
                """);
        }

        return new Seeded(document.ProjectId, document.PeriodKey);
    }

    private static async Task<ReportVersion> PublishedAsync(EcrDbContext db, ReportLayoutCommand layout)
    {
        var def = new ReportDef(
            EcrCode.Create($"RPC{Guid.NewGuid().ToString("N")[..8]}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Layout cache test" }),
            isRegulatory: true);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync();

        var version = new ReportVersion(
            def.Id,
            "1.0",
            ReportDefinitionSpec.ColumnsJson(Columns),
            ReportDefinitionSpec.RulesJson(new("CalculationResults", Layout: layout), Columns),
            Now);
        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync();

        return version;
    }
}
