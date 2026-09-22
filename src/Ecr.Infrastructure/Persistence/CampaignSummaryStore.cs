using System.Text.Json;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICampaignSummaryStore"/> (<c>BE-22</c>).</summary>
/// <remarks>
/// ⚠ Перелік проєктів і лічильник зрізів LINQ дає точно, а от «стан документа
/// — найгірший зі станів його аркушів» лишається агрегатом по агрегату, якого
/// SQL Server не бере без похідної таблиці (той самий привід до сирого SQL, що
/// й у <see cref="DocumentListSummaryStore"/>). Рядки йдуть по щонайбільше
/// <c>limit</c> проєктах; підсумки — одним агрегатом по всіх проєктах періоду.
/// </remarks>
public sealed class CampaignSummaryStore(EcrDbContext db) : ICampaignSummaryStore
{
    /// <summary>
    /// Лічильники етапів по проєктах; <c>/*projects*/</c> — підзапит ідентифікаторів проєктів.
    /// </summary>
    /// <remarks>
    /// ⛔ ОДИН текст для рядків і для підсумків: дві копії правила «найгірший
    /// стан аркуша» розійшлися б, і підсумок перестав би дорівнювати сумі рядків.
    ///
    /// ⚠ Аркуш складу БЕЗ рядка <c>wf.ApprovalState</c> — чернетка: рядок
    /// стану з'являється лише з першим поданням (<c>S-17</c>). Статуси —
    /// значення <c>DocumentStatus</c>: 0 Draft, 1 Submitted, 2 Approved,
    /// 3 Rejected. <c>PeriodKey</c> стоїть у предикаті партиційованої
    /// <c>wf.ApprovalState</c> (урок <c>WR-05</c>).
    /// </remarks>
    private const string ProjectStagesSql = """
        SELECT x.ProjectId,
               COUNT(*) AS Documents,
               COALESCE(SUM(CASE WHEN x.Rejected = 0 AND (x.Draft > 0 OR x.Sheets = 0) THEN 1 ELSE 0 END), 0) AS Draft,
               COALESCE(SUM(CASE WHEN x.Rejected = 0 AND x.Draft = 0 AND x.Submitted > 0 THEN 1 ELSE 0 END), 0) AS Submitted,
               COALESCE(SUM(CASE WHEN x.Sheets > 0 AND x.Approved = x.Sheets THEN 1 ELSE 0 END), 0) AS Approved,
               COALESCE(SUM(CASE WHEN x.Rejected > 0 THEN 1 ELSE 0 END), 0) AS Rejected
        FROM (
            SELECT d.Id, d.ProjectId,
                   COUNT(s.SheetDefId) AS Sheets,
                   COALESCE(SUM(CASE WHEN s.SheetDefId IS NOT NULL AND COALESCE(a.Status, 0) = 0 THEN 1 ELSE 0 END), 0) AS Draft,
                   COALESCE(SUM(CASE WHEN a.Status = 1 THEN 1 ELSE 0 END), 0) AS Submitted,
                   COALESCE(SUM(CASE WHEN a.Status = 2 THEN 1 ELSE 0 END), 0) AS Approved,
                   COALESCE(SUM(CASE WHEN a.Status = 3 THEN 1 ELSE 0 END), 0) AS Rejected
            FROM doc.Document d
            LEFT JOIN doc.DocumentSheet s
              ON s.DocumentId = d.Id AND s.IsIncluded = 1
            LEFT JOIN wf.ApprovalState a
              ON a.DocumentId = s.DocumentId AND a.SheetDefId = s.SheetDefId AND a.PeriodKey = @periodKey
            WHERE d.ProjectId IN (/*projects*/)
            GROUP BY d.Id, d.ProjectId
        ) x
        GROUP BY x.ProjectId
        """;

    /// <summary>
    /// Підсумки по ВСІХ проєктах періоду, згруповані за строком, поясом і готовністю.
    /// </summary>
    /// <remarks>
    /// ⚠ Групи, а не одне число на кожен клас: «прострочено/під ризиком»
    /// залежить від поясу проєкту й поточного моменту, і рахує їх
    /// <c>CampaignProgressRule</c> у застосунку. Груп стільки, скільки різних
    /// строків і поясів, — одиниці, а не тисячі рядків.
    /// </remarks>
    private const string BucketsSql = """
        SELECT b.DeadlineUtc, b.TimeZoneId, b.AllApproved, b.HasSnapshot,
               COUNT(*) AS Projects,
               SUM(b.Documents) AS Documents, SUM(b.Draft) AS Draft, SUM(b.Submitted) AS Submitted,
               SUM(b.Approved) AS Approved, SUM(b.Rejected) AS Rejected, SUM(b.Snapshots) AS Snapshots
        FROM (
            SELECT pe.ComputedGraceAt AS DeadlineUtc, pr.TimeZoneId,
                   COALESCE(st.Documents, 0) AS Documents, COALESCE(st.Draft, 0) AS Draft,
                   COALESCE(st.Submitted, 0) AS Submitted, COALESCE(st.Approved, 0) AS Approved,
                   COALESCE(st.Rejected, 0) AS Rejected, COALESCE(sn.Snapshots, 0) AS Snapshots,
                   CAST(CASE WHEN st.Documents > 0 AND st.Approved = st.Documents THEN 1 ELSE 0 END AS bit) AS AllApproved,
                   CAST(CASE WHEN sn.Snapshots > 0 THEN 1 ELSE 0 END AS bit) AS HasSnapshot
            FROM doc.Period pe
            JOIN doc.Project pr ON pr.Id = pe.ProjectId
            LEFT JOIN (/*stages*/) st ON st.ProjectId = pe.ProjectId
            LEFT JOIN (
                SELECT r.ProjectId, COUNT(*) AS Snapshots
                FROM rpt.ReportSnapshot r
                WHERE r.PeriodKey = @periodKey AND r.IsCurrent = 1
                GROUP BY r.ProjectId
            ) sn ON sn.ProjectId = pe.ProjectId
            WHERE pe.PeriodKey = @periodKey
        ) b
        GROUP BY b.DeadlineUtc, b.TimeZoneId, b.AllApproved, b.HasSnapshot
        """;

    /// <summary>Етапи проєктів переліку (ідентифікатори — JSON-параметром).</summary>
    private static readonly string RowsSql = ProjectStagesSql.Replace(
        "/*projects*/", "SELECT CAST(j.value AS int) FROM OPENJSON(@projects) j", StringComparison.Ordinal);

    /// <summary>Підсумки: ті самі етапи, але по всіх проєктах періоду.</summary>
    private static readonly string TotalsSql = BucketsSql.Replace(
        "/*stages*/",
        ProjectStagesSql.Replace(
            "/*projects*/", "SELECT p.ProjectId FROM doc.Period p WHERE p.PeriodKey = @periodKey", StringComparison.Ordinal),
        StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task<CampaignProjectPage> ListAsync(int periodKey, int limit, CancellationToken ct)
    {
        // ⛔ Кампанія — це проєкти, у яких цей період ВЗАГАЛІ Є. Перелік усіх
        // проєктів системи показав би торішні разом із наступними, і питання
        // «хто затримує» потонуло б у рядках, яких воно не стосується.
        var inCampaign = db.Periods
            .Where(p => p.PeriodKeyValue == periodKey)
            .Select(p => p.ProjectId)
            .Distinct();

        var total = await inCampaign.CountAsync(ct).ConfigureAwait(false);
        var buckets = await BucketsAsync(periodKey, ct).ConfigureAwait(false);

        var projects = await db.Projects
            .AsNoTracking()
            .Where(p => inCampaign.Contains(p.Id))
            .OrderBy(p => p.Code)
            .Take(limit)
            .Select(p => new
            {
                p.Id,
                p.Code,
                p.NameL10n,
                p.TimeZoneId,
                Deadline = db.Periods
                    .Where(x => x.ProjectId == p.Id && x.PeriodKeyValue == periodKey)
                    .Select(x => x.ComputedGraceAt)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (projects.Count == 0)
        {
            return new CampaignProjectPage(total, [], buckets);
        }

        var ids = projects.ConvertAll(p => p.Id);
        var stages = await StagesAsync(periodKey, ids, ct).ConfigureAwait(false);

        // ⚠ Лише ПОТОЧНІ зрізи: повторна побудова створює новий рядок, а не
        // переписує старий, і без `IsCurrent` проєкт, зріз якого перебудували
        // тричі, виглядав би втричі готовішим за сусіда.
        var snapshots = await db.ReportSnapshots
            .AsNoTracking()
            .Where(s => ids.Contains(s.ProjectId) && s.PeriodKey == periodKey && s.IsCurrent)
            .GroupBy(s => s.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, ct)
            .ConfigureAwait(false);

        var rows = projects.ConvertAll(p =>
        {
            // Проєкт без жодного документа не має рядка в агрегаті — і це не
            // те саме, що нулі «все в чернетках»: кампанія тут не починалася.
            var stage = stages.GetValueOrDefault(p.Id);

            return new CampaignProjectFacts(
                p.Id,
                p.Code,
                p.NameL10n,
                stage?.Documents ?? 0,
                stage?.Draft ?? 0,
                stage?.Submitted ?? 0,
                stage?.Approved ?? 0,
                stage?.Rejected ?? 0,
                snapshots.GetValueOrDefault(p.Id),
                Deadline(p.Deadline),
                p.TimeZoneId);
        });

        return new CampaignProjectPage(total, rows, buckets);
    }

    /// <summary>
    /// Строк із колонки; незаповнена межа (<c>0001-01-01</c>) — «ще не пораховано».
    /// </summary>
    /// <remarks>
    /// ⚠ Не «прострочено з першого року нашої ери»: межі пише
    /// <c>RecomputeBoundaries</c> при побудові календаря, і період, для якого
    /// цього ще не сталося, строку просто не має.
    /// </remarks>
    private static DateTime? Deadline(DateTime value)
        => value == default ? null : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Лічильники етапів по кожному проєкту переліку.</summary>
    private async Task<Dictionary<int, StageRow>> StagesAsync(
        int periodKey, IReadOnlyList<int> projectIds, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQueryRaw<StageRow>(
                RowsSql,
                new SqlParameter("@periodKey", periodKey),
                new SqlParameter("@projects", JsonSerializer.Serialize(projectIds)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.ProjectId);
    }

    /// <summary>Підсумкові групи по всіх проєктах періоду — один агрегатний запит без стелі.</summary>
    private async Task<IReadOnlyList<CampaignBucket>> BucketsAsync(int periodKey, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQueryRaw<BucketRow>(TotalsSql, new SqlParameter("@periodKey", periodKey))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(r => new CampaignBucket(
            Deadline(r.DeadlineUtc), r.TimeZoneId, r.AllApproved, r.HasSnapshot, r.Projects,
            r.Documents, r.Draft, r.Submitted, r.Approved, r.Rejected, r.Snapshots));
    }

    /// <summary>Рядок агрегату етапів; імена колонок — імена властивостей.</summary>
    public sealed record StageRow(
        int ProjectId, int Documents, int Draft, int Submitted, int Approved, int Rejected);

    /// <summary>Рядок підсумкового агрегату; імена колонок — імена властивостей.</summary>
    public sealed record BucketRow(
        DateTime DeadlineUtc, string TimeZoneId, bool AllApproved, bool HasSnapshot, int Projects,
        int Documents, int Draft, int Submitted, int Approved, int Rejected, int Snapshots);
}
