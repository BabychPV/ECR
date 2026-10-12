// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotSheetStatusTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Статус зрізу звітності, виведений зі станів аркушів (D-65), на РЕАЛЬНІЙ базі: кожен статус
/// погодження — <c>Draft</c>, <c>Submitted</c>, <c>Approved</c>, <c>Rejected</c> — окремо й у суміші
/// (ФВ-5.15, D-219 «Rejected як Draft»).
/// </summary>
/// <remarks>
/// ⛔ Знахідка REQ-COVERAGE-2026-10-11 (§5 п. 2): у <c>ReportSnapshotBuilder.StatusOfDataAsync</c>
/// достатньо прибрати <c>or DocumentStatus.Rejected</c> з умови «є Draft» — і зріз, у якому один аркуш
/// ПОДАНО, а другий ВІДХИЛЕНО, стає <c>Submitted</c>: <c>rpt.v_*</c> віддає регулятору дані, які від
/// автора вимагали виправити, а подання морозить такий зріз назавжди. Жоден тест цього не ловив:
/// <c>DocumentStatus.Rejected</c> у зрізі не вживав ніхто.
///
/// Мутаційний доказ (detached-worktree, не комітиться): прибрати <c>or DocumentStatus.Rejected</c> у
/// <c>StatusOfDataAsync</c> — червоніють рядки «Submitted+Rejected», «Approved+Rejected»,
/// «Submitted+Approved+Rejected» теорії, обидва «змішані» тести й
/// <see cref="Відхилений_аркуш_не_потрапляє_в_регуляторну_вʼюху_доки_його_не_подано_знову"/>.
///
/// ⚠ Статус рахується в двох місцях: <c>RefreshStatusAsync</c> (перехід робочого процесу) і
/// <c>BuildAsync</c> (побудова). Теорія й «змішані» тести ганяють перше, вʼюха — друге.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportSnapshotSheetStatusTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    private const int Author = 5;
    private const int Approver = 6;

    /// <summary>
    /// Стан кожного аркуша періоду → статус зрізу. <c>none</c> — рядка стану немає (аркуш у складі,
    /// дій не було): <c>Draft</c> за складом (W1-02).
    /// </summary>
    [Theory]
    [InlineData("none", nameof(SnapshotStatus.Draft))]
    [InlineData("Draft", nameof(SnapshotStatus.Draft))]
    [InlineData("Rejected", nameof(SnapshotStatus.Draft))]
    [InlineData("Submitted", nameof(SnapshotStatus.Submitted))]
    [InlineData("Approved", nameof(SnapshotStatus.Approved))]
    [InlineData("Approved,Approved", nameof(SnapshotStatus.Approved))]
    [InlineData("Submitted,Submitted", nameof(SnapshotStatus.Submitted))]
    [InlineData("Submitted,Approved", nameof(SnapshotStatus.Submitted))]
    [InlineData("Rejected,Rejected", nameof(SnapshotStatus.Draft))]
    [InlineData("Submitted,Rejected", nameof(SnapshotStatus.Draft))]
    [InlineData("Approved,Rejected", nameof(SnapshotStatus.Draft))]
    [InlineData("Submitted,Approved,Rejected", nameof(SnapshotStatus.Draft))]
    [InlineData("Submitted,Draft", nameof(SnapshotStatus.Draft))]
    [InlineData("Approved,Draft", nameof(SnapshotStatus.Draft))]
    [InlineData("Approved,none", nameof(SnapshotStatus.Draft))]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.15")]
    [Trait("Requirement", "ФВ-5.14")]
    [Trait("Decision", "D-219")]
    [Trait("Decision", "D-65")]
    public async Task Статус_зрізу_виводиться_зі_станів_усіх_аркушів_включно_з_Rejected(
        string sheetStates, string expectedName)
    {
        var expected = Enum.Parse<SnapshotStatus>(expectedName);
        var scenario = await ArrangeAsync(sheetStates.Split(',')).ConfigureAwait(true);

        var snapshotId = await AddSnapshotAsync(scenario, scenario.Document.PeriodKey.Value).ConfigureAwait(true);

        await using var db = scenario.Chain.CreateContext();
        var builder = Builder(db);

        var returned = await builder.RefreshStatusAsync(snapshotId, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(expected, returned);
        Assert.Equal(expected, await StoredStatusAsync(scenario, snapshotId).ConfigureAwait(true));
    }

    /// <summary>
    /// Суміш у ОДНОМУ документі: «подано» й «відхилено» — зріз чернетковий, а відхилений аркуш не
    /// «заражає» сусідній період. Річний зріз бачить усі періоди проєкту, періодний — лише свій.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.15")]
    [Trait("Decision", "D-219")]
    public async Task Відхилений_аркуш_одного_періоду_робить_річний_зріз_чернетковим_але_не_чіпає_сусідній_період()
    {
        const int secondPeriod = 202602;

        var scenario = await ArrangeAsync(["Approved"]).ConfigureAwait(true);

        // Другий період того самого проєкту: той самий єдиний аркуш у ньому — відхилено.
        await using (var db = scenario.Chain.CreateContext())
        {
            db.Periods.Add(new Period(
                scenario.Document.ProjectId, new PeriodKey(secondPeriod), 2,
                new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));

            var rejected = new ApprovalState(scenario.Document.DocumentId, scenario.Document.SheetDefId, secondPeriod);
            rejected.Submit(Author, Now);
            rejected.Reject(Approver, "Невірні значення", Now);
            db.ApprovalStates.Add(rejected);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        var annual = await AddSnapshotAsync(scenario, periodKey: null).ConfigureAwait(true);
        var approvedPeriod = await AddSnapshotAsync(scenario, scenario.Document.PeriodKey.Value).ConfigureAwait(true);
        var rejectedPeriod = await AddSnapshotAsync(scenario, secondPeriod).ConfigureAwait(true);

        await using var context = scenario.Chain.CreateContext();
        var builder = Builder(context);

        Assert.Equal(
            SnapshotStatus.Draft,
            await builder.RefreshStatusAsync(annual, CancellationToken.None).ConfigureAwait(true));
        Assert.Equal(
            SnapshotStatus.Approved,
            await builder.RefreshStatusAsync(approvedPeriod, CancellationToken.None).ConfigureAwait(true));
        Assert.Equal(
            SnapshotStatus.Draft,
            await builder.RefreshStatusAsync(rejectedPeriod, CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>
    /// Повторне подання виправленого аркуша знімає «Draft» зі зрізу: <c>Rejected</c> — не
    /// термінальний стан, і статус зрізу слідує за аркушем, а не залипає.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.15")]
    [Trait("Decision", "D-219")]
    public async Task Після_повторного_подання_відхиленого_аркуша_зріз_знову_подається()
    {
        var scenario = await ArrangeAsync(["Approved", "Rejected"]).ConfigureAwait(true);
        var snapshotId = await AddSnapshotAsync(scenario, scenario.Document.PeriodKey.Value).ConfigureAwait(true);

        await using (var before = scenario.Chain.CreateContext())
        {
            Assert.Equal(
                SnapshotStatus.Draft,
                await Builder(before).RefreshStatusAsync(snapshotId, CancellationToken.None).ConfigureAwait(true));
        }

        await ResubmitRejectedAsync(scenario).ConfigureAwait(true);

        await using var after = scenario.Chain.CreateContext();
        Assert.Equal(
            SnapshotStatus.Submitted,
            await Builder(after).RefreshStatusAsync(snapshotId, CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>
    /// Наскрізно до регулятора: зріз зі «Submitted + Rejected» будується ЧЕРНЕТКОВИМ, і його рядок
    /// не видно в <c>rpt.v_WaterReport_v1</c> (<c>Status IN (1, 2)</c>); після повторного подання
    /// відхиленого аркуша той самий рядок з'являється зі статусом <c>Submitted</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ «Контроль» у кінці обов'язковий: без нього «рядка немає» доводилося б і порожньою вʼюхою.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.15")]
    [Trait("Requirement", "ФВ-10.11")]
    [Trait("Decision", "D-219")]
    public async Task Відхилений_аркуш_не_потрапляє_в_регуляторну_вʼюху_доки_його_не_подано_знову()
    {
        var scenario = await ArrangeAsync(["Submitted", "Rejected"]).ConfigureAwait(true);
        var period = scenario.Document.PeriodKey.Value;
        var projectId = scenario.Document.ProjectId;

        // Версія звіту — під описом `WaterReport`: саме його читає регуляторна вʼюха.
        int versionId;
        await using (var db = scenario.Chain.CreateContext())
        {
            var defId = await WaterReportDefIdAsync(db).ConfigureAwait(true);

            var version = new ReportVersion(
                defId,
                $"s{Guid.NewGuid():N}"[..12],
                """[{"code":"DocumentId","kind":"number"},{"code":"Value","kind":"number"}]""",
                """{"rowSource":"CalculationResults"}""",
                Now);
            version.Publish();
            db.ReportVersions.Add(version);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);
            versionId = version.Id;
        }

        long snapshotId;
        await using (var db = scenario.Chain.CreateContext())
        {
            snapshotId = await Builder(db)
                .BuildAsync(versionId, projectId, new PeriodKey(period), parametersJson: null, CancellationToken.None)
                .ConfigureAwait(true);

            // Побудова нічого не знайшла в розрахунках; рядок додається руками, щоб було що ховати.
            var row = new ReportRow(snapshotId, rowNo: 1, columnCode: "A");
            row.SetValue(valueString: null, valueNumeric: 42m, valueDate: null);
            db.ReportRows.Add(row);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        Assert.Equal(SnapshotStatus.Draft, await StoredStatusAsync(scenario, snapshotId).ConfigureAwait(true));
        Assert.Empty(await RegulatoryRowsAsync(projectId, period).ConfigureAwait(true));

        // Контроль: аркуш виправлено й подано знову — рядок з'являється.
        await ResubmitRejectedAsync(scenario).ConfigureAwait(true);
        await using (var db = scenario.Chain.CreateContext())
        {
            Assert.Equal(
                SnapshotStatus.Submitted,
                await Builder(db).RefreshStatusAsync(snapshotId, CancellationToken.None).ConfigureAwait(true));
        }

        var visible = await RegulatoryRowsAsync(projectId, period).ConfigureAwait(true);
        var shown = Assert.Single(visible);
        Assert.Equal(42m, shown.Value);
        Assert.Equal((int)SnapshotStatus.Submitted, shown.Status);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Chain, TestDocument Document, IReadOnlyList<int> SheetDefIds, int ReportVersionId);

    private static ReportSnapshotBuilder Builder(EcrDbContext db)
        => new(db, new TestClock(Now), new MemoryCache(new MemoryCacheOptions()));

    /// <summary>
    /// Документ з N аркушами у складі й станом кожного в одному періоді. Перший аркуш — з ланцюга
    /// будівника, решта — додаткові <c>SheetDef</c> тієї самої версії шаблону.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(string[] states)
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync().ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..8];

        await using var db = chain.CreateContext();

        var sheets = new List<int> { document.SheetDefId };
        for (var i = 1; i < states.Length; i++)
        {
            var sheet = new SheetDef(
                document.TemplateVersionId,
                EcrCode.Create($"SX{i}{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = $"Extra sheet {i}" }),
                i + 1);
            db.SheetDefs.Add(sheet);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            sheets.Add(sheet.Id);
        }

        // Склад документа: без рядка складу аркуша «немає» (W1-02), і зріз чесно Draft.
        foreach (var sheetDefId in sheets)
        {
            db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, sheetDefId));
        }

        for (var i = 0; i < states.Length; i++)
        {
            var state = states[i].Trim();
            if (state == "none")
            {
                continue;
            }

            var approval = new ApprovalState(document.DocumentId, sheets[i], document.PeriodKey.Value);
            switch (state)
            {
                case "Draft":
                    break;
                case "Submitted":
                    approval.Submit(Author, Now);
                    break;
                case "Approved":
                    approval.Submit(Author, Now);
                    approval.Approve(Approver, Now);
                    break;
                case "Rejected":
                    approval.Submit(Author, Now);
                    approval.Reject(Approver, "Невірні значення", Now);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(states), state, "Невідомий стан аркуша.");
            }

            db.ApprovalStates.Add(approval);
        }

        var def = new ReportDef(
            EcrCode.Create($"RSS{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet status test" }),
            isRegulatory: true);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        var version = new ReportVersion(def.Id, "1.0", "[]", "{}", Now);
        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return new Scenario(chain, document, sheets, version.Id);
    }

    /// <summary>Поточний завершений зріз у чернетці; <c>null</c> період — річний.</summary>
    private static async Task<long> AddSnapshotAsync(Scenario scenario, int? periodKey)
    {
        await using var db = scenario.Chain.CreateContext();

        var snapshot = new ReportSnapshot(
            scenario.ReportVersionId, scenario.Document.ProjectId, periodKey, SnapshotStatus.Draft, Now, null);
        snapshot.Complete(rowCount: 0, contentHash: [1, 2, 3], calculationRunId: null, parametersJson: null);
        snapshot.MakeCurrent();
        db.ReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return snapshot.Id;
    }

    private static async Task<SnapshotStatus> StoredStatusAsync(Scenario scenario, long snapshotId)
    {
        await using var db = scenario.Chain.CreateContext();

        return await db.ReportSnapshots
            .AsNoTracking()
            .Where(s => s.Id == snapshotId)
            .Select(s => s.Status)
            .SingleAsync(CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Автор виправив відхилений аркуш і подає його знову.</summary>
    private static async Task ResubmitRejectedAsync(Scenario scenario)
    {
        await using var db = scenario.Chain.CreateContext();

        var rejected = await db.ApprovalStates
            .Where(a => a.DocumentId == scenario.Document.DocumentId && a.Status == DocumentStatus.Rejected)
            .SingleAsync(CancellationToken.None)
            .ConfigureAwait(false);

        rejected.Submit(Author, Now.AddHours(1));
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Опис <c>WaterReport</c> — той, що читає <c>rpt.v_WaterReport_v1</c>. Код унікальний у базі, і
    /// його створює також <c>ReportSnapshotBuilderTests</c>: тут «знайти або створити» в обидва боки.
    /// </summary>
    private static async Task<int> WaterReportDefIdAsync(EcrDbContext db)
    {
        var existing = await db.Database
            .SqlQuery<int>($"SELECT Id AS Value FROM rpt.ReportDef WHERE Code = N'WaterReport'")
            .ToListAsync(CancellationToken.None)
            .ConfigureAwait(false);

        if (existing.Count > 0)
        {
            return existing[0];
        }

        var def = new ReportDef(
            EcrCode.Create("WaterReport"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }),
            isRegulatory: true);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return def.Id;
    }

    private async Task<List<(decimal Value, int Status)>> RegulatoryRowsAsync(int projectId, int periodKey)
    {
        var result = new List<(decimal, int)>();

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ValueNumeric, Status FROM rpt.v_WaterReport_v1 WHERE ProjectId = @p AND PeriodKey = @k";
        command.Parameters.AddWithValue("@p", projectId);
        command.Parameters.AddWithValue("@k", periodKey);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add((reader.GetDecimal(0), Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return result;
    }
}
