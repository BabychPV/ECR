// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotCurrencyTests.cs
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
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
