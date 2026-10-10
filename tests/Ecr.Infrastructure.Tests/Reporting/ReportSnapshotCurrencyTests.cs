// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotCurrencyTests.cs
using System.Data.Common;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Поточність зрізу (<c>IsCurrent</c>) — одна на опис звіту × проєкт × період (R6-X7).
/// </summary>
/// <remarks>
/// Тести CI, локально не запускались.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportSnapshotCurrencyTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// X7-02. Мутація: повернути фільтр <c>s.ReportVersionId == snapshot.ReportVersionId</c>
    /// у <c>SwitchCurrentAsync</c> — зріз версії 1.0 лишається поточним поруч зі зрізом 1.1,
    /// і <c>rpt.v_*</c> (фільтр лише за кодом опису) подвоює рядки.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Нова_версія_опису_знімає_поточність_зі_зрізу_попередньої()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        var (defId, first) = await VersionAsync(chain, reportDefId: null, "1.0");

        await using var db = chain.CreateContext();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now), memory);

        var older = await builder.BuildAsync(
            first, document.ProjectId, document.PeriodKey, parametersJson: null, CancellationToken.None);

        // Методист публікує нову версію того самого опису — побудова бере її.
        var (_, second) = await VersionAsync(chain, defId, "1.1");
        var newer = await builder.BuildAsync(
            second, document.ProjectId, document.PeriodKey, parametersJson: null, CancellationToken.None);

        await using var read = chain.CreateContext();
        var current = await read.ReportSnapshots.AsNoTracking()
            .Where(s => s.ProjectId == document.ProjectId
                        && s.PeriodKey == document.PeriodKey.Value
                        && s.IsCurrent
                        && read.ReportVersions.Any(v => v.Id == s.ReportVersionId && v.ReportDefId == defId))
            .Select(s => s.Id)
            .ToListAsync();

        Assert.Equal([newer], current);
        Assert.False(
            (await read.ReportSnapshots.AsNoTracking().SingleAsync(s => s.Id == older)).IsCurrent,
            "Зріз попередньої версії лишився поточним — rpt.v_* подвоїла б рядки.");
    }

    /// <summary>
    /// X7-03. Мутація: прибрати обидві перевірки <c>FreshFrozenAsync</c> у <c>BuildAsync</c> — друга побудова
    /// знімає поточність із поданого зрізу.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Побудова_після_подання_відмовляє_і_поданий_зріз_лишається_поточним()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        await ReportSnapshotBuildMomentTests.SubmitPeriodAsync(chain, document);
        var (_, version) = await VersionAsync(chain, reportDefId: null, "1.0");

        await using var db = chain.CreateContext();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now), memory);

        var submitted = await builder.BuildAsync(
            version, document.ProjectId, document.PeriodKey, parametersJson: null, CancellationToken.None);

        // ⛔ R7-Y8 / Y8-01: доменна відмова з кодом «зріз подано», а не InvalidOperationException —
        // інакше черга тричі повторює вердикт і клієнт бачить ECR-SYS-0500.
        var refused = await Assert.ThrowsAsync<DomainException>(() => builder.BuildAsync(
            version, document.ProjectId, document.PeriodKey, parametersJson: null, CancellationToken.None));
        Assert.Equal(ErrorCodes.ReportImmutable, refused.ErrorCode);
        Assert.Equal("err.ECR-RPT-0409.periodSubmittedRebuild", refused.Details?["messageKey"]);

        // ⛔ R7-Y7 / Y7-01: синхронна перевірка обробника запиту бачить той самий поданий зріз
        // і відмовляє 409 ще до постановки задачі.
        Assert.Equal(
            submitted,
            await builder.FindFreshFrozenCurrentAsync(
                version, document.ProjectId, document.PeriodKey, CancellationToken.None));

        var all = await SnapshotsAsync(chain, document);
        var only = Assert.Single(all);
        Assert.Equal(submitted, only.Id);
        Assert.True(only.IsCurrent);
        Assert.Equal(SnapshotStatus.Submitted, only.Status);
    }

    /// <summary>
    /// X7-03, гонка: побудова почалася ДО подання останнього аркуша, а подання (із
    /// заморожуванням поточного зрізу) закомітилося, поки вона читала джерело.
    /// Мутація: прибрати повторну перевірку під замком слоту в <c>BuildAsync</c>
    /// (лишити лише ранню) — нова побудова знімає поточність із щойно замороженого зрізу.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Подання_під_час_побудови_не_втрачає_поточності_замороженого_зрізу()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        var (_, version) = await VersionAsync(chain, reportDefId: null, "1.0");

        long draft;
        using var memory = new MemoryCache(new MemoryCacheOptions());
        await using (var first = chain.CreateContext())
        {
            draft = await new ReportSnapshotBuilder(first, new TestClock(Now), memory).BuildAsync(
                version, document.ProjectId, document.PeriodKey, parametersJson: null, CancellationToken.None);
        }

        var submitDuringRead = new OnAggregate(async () =>
        {
            await ReportSnapshotBuildMomentTests.SubmitPeriodAsync(chain, document);
            await using var other = chain.CreateContext();
            var frozen = await other.ReportSnapshots.SingleAsync(s => s.Id == draft);
            frozen.MarkSubmitted(userId: 5);
            await other.SaveChangesAsync();
        });

        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(submitDuringRead)
            .Options);

        var refused = await Assert.ThrowsAsync<DomainException>(() => new ReportSnapshotBuilder(db, new TestClock(Now), memory)
            .BuildAsync(version, document.ProjectId, document.PeriodKey, parametersJson: null, CancellationToken.None));
        Assert.Equal(ErrorCodes.ReportImmutable, refused.ErrorCode);

        Assert.True(submitDuringRead.Fired);
        var only = Assert.Single(await SnapshotsAsync(chain, document));
        Assert.Equal(draft, only.Id);
        Assert.True(only.IsCurrent, "Заморожений під час побудови зріз втратив поточність.");
        Assert.Equal(SnapshotStatus.Submitted, only.Status);
    }

    /// <summary>X7-03: після повернення даних у роботу (статус даних <c>Draft</c>) нова побудова дозволена.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Після_повернення_в_роботу_нова_побудова_дозволена()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        await ReportSnapshotBuildMomentTests.SubmitPeriodAsync(chain, document);
        var (_, version) = await VersionAsync(chain, reportDefId: null, "1.0");

        await using var db = chain.CreateContext();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now), memory);

        var submitted = await builder.BuildAsync(
            version, document.ProjectId, document.PeriodKey, parametersJson: null, CancellationToken.None);

        // Аркуш повернуто в роботу (відкликано): статус даних періоду — `Draft`.
        await using (var other = chain.CreateContext())
        {
            var state = await other.ApprovalStates.SingleAsync(
                a => a.DocumentId == document.DocumentId
                     && a.SheetDefId == document.SheetDefId
                     && a.PeriodKey == document.PeriodKey.Value);
            state.Recall("X7-03", firstStepId: null);
            await other.SaveChangesAsync();
        }

        // Після повернення в роботу синхронна перевірка обробника вже не відмовляє.
        Assert.Null(await builder.FindFreshFrozenCurrentAsync(
            version, document.ProjectId, document.PeriodKey, CancellationToken.None));

        var rebuilt = await builder.BuildAsync(
            version, document.ProjectId, document.PeriodKey, parametersJson: null, CancellationToken.None);

        var all = (await SnapshotsAsync(chain, document)).ToDictionary(s => s.Id);
        Assert.False(all[submitted].IsCurrent);
        Assert.Equal(SnapshotStatus.Submitted, all[submitted].Status);
        Assert.True(all[rebuilt].IsCurrent);
        Assert.Equal(SnapshotStatus.Draft, all[rebuilt].Status);
    }

    /// <summary>
    /// X7-03 × X7-01: поданий, але ЗАСТАРІЛИЙ зріз (після нього став актуальним прогін)
    /// перебудовується, і новий зріз за поданим періодом народжується <c>Submitted</c>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Застарілий_поданий_зріз_перебудовується()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        await ReportSnapshotBuildMomentTests.SubmitPeriodAsync(chain, document);
        var (_, version) = await VersionAsync(chain, reportDefId: null, "1.0");

        await using var db = chain.CreateContext();
        using var memory = new MemoryCache(new MemoryCacheOptions());

        var submitted = await new ReportSnapshotBuilder(db, new TestClock(Now), memory).BuildAsync(
            version, document.ProjectId, document.PeriodKey, parametersJson: null, CancellationToken.None);

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT calc.CalculationRun (ProjectId, PeriodKey, Status, StartedAt, FinishedAt, ErrorMessage, DocumentId)
                VALUES (@p, @k, N'Current', @started, @finished, NULL, NULL);
                """;
            command.Parameters.AddWithValue("@p", document.ProjectId);
            command.Parameters.AddWithValue("@k", document.PeriodKey.Value);
            command.Parameters.AddWithValue("@started", Now);
            command.Parameters.AddWithValue("@finished", Now.AddMinutes(1));
            await command.ExecuteNonQueryAsync();
        }

        var rebuilt = await new ReportSnapshotBuilder(db, new TestClock(Now.AddHours(1)), memory).BuildAsync(
            version, document.ProjectId, document.PeriodKey, parametersJson: null, CancellationToken.None);

        var all = (await SnapshotsAsync(chain, document)).ToDictionary(s => s.Id);
        Assert.False(all[submitted].IsCurrent);
        Assert.True(all[rebuilt].IsCurrent);
        Assert.Equal(SnapshotStatus.Submitted, all[rebuilt].Status);
    }

    private static async Task<List<ReportSnapshot>> SnapshotsAsync(TestDocumentBuilder chain, TestDocument document)
    {
        await using var db = chain.CreateContext();
        return await db.ReportSnapshots.AsNoTracking()
            .Where(s => s.ProjectId == document.ProjectId && s.PeriodKey == document.PeriodKey.Value)
            .OrderBy(s => s.Id)
            .ToListAsync();
    }

    /// <summary>Виконує дію (закомічену окремим контекстом) на ПЕРШОМУ читанні джерела зрізу.</summary>
    private sealed class OnAggregate(Func<Task> action) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains(ReportSnapshotBuilder.AggregateTag, StringComparison.Ordinal))
            {
                Fired = true;
                await action();
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Опублікована версія опису; <paramref name="reportDefId"/> <c>null</c> — новий опис.</summary>
    internal static async Task<(int DefId, int VersionId)> VersionAsync(
        TestDocumentBuilder chain, int? reportDefId, string number)
    {
        await using var db = chain.CreateContext();

        if (reportDefId is null)
        {
            var def = new ReportDef(
                EcrCode.Create($"RCU{Guid.NewGuid().ToString("N")[..8]}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Currency test" }),
                isRegulatory: true);
            db.ReportDefs.Add(def);
            await db.SaveChangesAsync();
            reportDefId = def.Id;
        }

        var version = new ReportVersion(
            reportDefId.Value,
            number,
            """[{"code":"DocumentId","kind":"number"},{"code":"Value","kind":"number"}]""",
            """{"rowSource":"CalculationResults"}""",
            Now);
        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync();
        return (reportDefId.Value, version.Id);
    }
}
