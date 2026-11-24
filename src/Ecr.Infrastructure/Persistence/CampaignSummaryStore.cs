using System.Text.Json;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICampaignSummaryStore"/> (<c>BE-22</c>).</summary>
/// <remarks>
/// ⚠ Перелік проєктів LINQ дає точно, а от «стан документа
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
    ///
    /// ✎ B144 (B75): аркуш документа, чия версія шаблону за період — без подання й затвердження
    /// (<c>cfg.TemplateVersion.WorkflowMode = 1</c>, None), ФІНАЛЬНИЙ
    /// (B138): рахується затвердженим, а не чернеткою, і в
    /// <c>NotSubmittedSheets</c> не входить; старий стан (перенос версії) не враховується. Версія — за правилом
    /// <c>SheetEditGate</c>: без шаблону — <c>doc.fn_VersionOf</c>, із шаблоном — <c>doc.fn_TemplateVersionOf</c>.
    /// Приєднання <c>nv</c> — у тому самому запиті (щонайбільше один рядок на документ: <c>Id</c> — ключ).
    /// </remarks>
    private const string ProjectStagesSql = """
        SELECT x.ProjectId,
               COUNT(*) AS Documents,
               COALESCE(SUM(CASE WHEN x.Rejected = 0 AND (x.Draft > 0 OR x.Sheets = 0) THEN 1 ELSE 0 END), 0) AS Draft,
               COALESCE(SUM(CASE WHEN x.Rejected = 0 AND x.Draft = 0 AND x.Submitted > 0 THEN 1 ELSE 0 END), 0) AS Submitted,
               COALESCE(SUM(CASE WHEN x.Sheets > 0 AND x.Approved = x.Sheets THEN 1 ELSE 0 END), 0) AS Approved,
               COALESCE(SUM(CASE WHEN x.Rejected > 0 THEN 1 ELSE 0 END), 0) AS Rejected,
               COALESCE(SUM(x.Sheets), 0) AS Sheets,
               COALESCE(SUM(x.Draft + x.Rejected), 0) AS NotSubmittedSheets
        FROM (
            SELECT d.Id, d.ProjectId,
                   COUNT(s.SheetDefId) AS Sheets,
                   COALESCE(SUM(CASE WHEN s.SheetDefId IS NOT NULL AND nv.Id IS NULL AND COALESCE(a.Status, 0) = 0 THEN 1 ELSE 0 END), 0) AS Draft,
                   COALESCE(SUM(CASE WHEN nv.Id IS NULL AND a.Status = 1 THEN 1 ELSE 0 END), 0) AS Submitted,
                   COALESCE(SUM(CASE WHEN s.SheetDefId IS NOT NULL AND (nv.Id IS NOT NULL OR a.Status = 2) THEN 1 ELSE 0 END), 0) AS Approved,
                   COALESCE(SUM(CASE WHEN nv.Id IS NULL AND a.Status = 3 THEN 1 ELSE 0 END), 0) AS Rejected
            FROM doc.Document d
            LEFT JOIN cfg.TemplateVersion nv   -- B144: версія документа за період без подання й затвердження (None) — аркуші фінальні
              ON nv.WorkflowMode = 1
             AND nv.Id = CASE WHEN d.TemplateId IS NULL THEN doc.fn_VersionOf(d.ProjectId, @periodKey)
                              ELSE doc.fn_TemplateVersionOf(d.ProjectId, d.TemplateId, @periodKey) END
            LEFT JOIN doc.DocumentSheet s
              ON s.DocumentId = d.Id AND s.IsIncluded = 1
            LEFT JOIN wf.ApprovalState a
              ON a.DocumentId = s.DocumentId AND a.SheetDefId = s.SheetDefId AND a.PeriodKey = @periodKey
             AND (a.SheetCopyId = s.Id OR (a.SheetCopyId IS NULL AND s.CopyOfId IS NULL))   -- RS: стан СВОГО рядка складу (копія — окремий аркуш)
            WHERE d.ProjectId IN (/*projects*/)
            GROUP BY d.Id, d.ProjectId
        ) x
        GROUP BY x.ProjectId
        """;

    /// <summary>
    /// Підсумки по ВСІХ проєктах періоду, згруповані за строком, поясом і готовністю.
    /// </summary>
    /// <remarks>
    /// ✎ P-1124-3 (D-NEW): лічильник зрізів звітності і умову «є зріз» прибрано разом зі зрізами —
    /// «готово» = усі документи затверджено.
    ///
    /// ⚠ Групи, а не одне число на кожен клас: «прострочено/під ризиком»
    /// залежить від поясу проєкту й поточного моменту, і рахує їх
    /// <c>CampaignProgressRule</c> у застосунку. Груп стільки, скільки різних
    /// строків і поясів, — одиниці, а не тисячі рядків.
    /// </remarks>
    private const string BucketsSql = """
        SELECT b.DeadlineUtc, b.TimeZoneId, b.AllApproved,
               COUNT(*) AS Projects,
               SUM(b.Documents) AS Documents, SUM(b.Draft) AS Draft, SUM(b.Submitted) AS Submitted,
               SUM(b.Approved) AS Approved, SUM(b.Rejected) AS Rejected,
               SUM(b.Sheets) AS Sheets, SUM(b.NotSubmittedSheets) AS NotSubmittedSheets
        FROM (
            SELECT pe.ComputedGraceAt AS DeadlineUtc, pr.TimeZoneId,
                   COALESCE(st.Documents, 0) AS Documents, COALESCE(st.Draft, 0) AS Draft,
                   COALESCE(st.Submitted, 0) AS Submitted, COALESCE(st.Approved, 0) AS Approved,
                   COALESCE(st.Rejected, 0) AS Rejected,
                   COALESCE(st.Sheets, 0) AS Sheets, COALESCE(st.NotSubmittedSheets, 0) AS NotSubmittedSheets,
                   CAST(CASE WHEN st.Documents > 0 AND st.Approved = st.Documents THEN 1 ELSE 0 END AS bit) AS AllApproved
            FROM doc.Period pe
            JOIN doc.Project pr ON pr.Id = pe.ProjectId
            LEFT JOIN (/*stages*/) st ON st.ProjectId = pe.ProjectId
            WHERE pe.PeriodKey = @periodKey
        ) b
        GROUP BY b.DeadlineUtc, b.TimeZoneId, b.AllApproved
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
                Deadline(p.Deadline),
                p.TimeZoneId,
                stage?.Sheets ?? 0,
                stage?.NotSubmittedSheets ?? 0);
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

    /// <summary>Не подані аркуші проєкту по періодах — один агрегат (<c>UI-33</c>, D1).</summary>
    /// <remarks>
    /// ⚠ <c>PeriodKey</c> стоїть у предикаті приєднання партиційованої <c>wf.ApprovalState</c> (урок <c>WR-05</c>);
    /// «подано» — статуси 1 (Submitted) і 2 (Approved), решта (немає рядка, 0, 3) — не подано.
    /// ✎ B144 (B75): аркуш версії без подання й затвердження (None, версія — ЗА ЦЕЙ період) не подається взагалі,
    /// тож «не поданим» не є — те саме правило й те саме приєднання <c>nv</c>, що в <see cref="ProjectStagesSql"/>.
    /// </remarks>
    private const string NotSubmittedByPeriodSql = """
        SELECT p.PeriodKey AS PeriodKey,
               CAST(COUNT(s.SheetDefId) - COUNT(CASE WHEN nv.Id IS NOT NULL OR a.Status IN (1, 2) THEN 1 END) AS int) AS NotSubmitted
        FROM doc.Period p
        JOIN doc.Document d ON d.ProjectId = p.ProjectId
        LEFT JOIN cfg.TemplateVersion nv   -- B144: версія документа за період без подання й затвердження (None)
          ON nv.WorkflowMode = 1
         AND nv.Id = CASE WHEN d.TemplateId IS NULL THEN doc.fn_VersionOf(d.ProjectId, p.PeriodKey)
                          ELSE doc.fn_TemplateVersionOf(d.ProjectId, d.TemplateId, p.PeriodKey) END
        JOIN doc.DocumentSheet s ON s.DocumentId = d.Id AND s.IsIncluded = 1
        LEFT JOIN wf.ApprovalState a
          ON a.DocumentId = s.DocumentId AND a.SheetDefId = s.SheetDefId AND a.PeriodKey = p.PeriodKey
         AND (a.SheetCopyId = s.Id OR (a.SheetCopyId IS NULL AND s.CopyOfId IS NULL))   -- RS: стан СВОГО рядка складу
        WHERE p.ProjectId = @projectId
        GROUP BY p.PeriodKey
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> NotSubmittedSheetsByPeriodAsync(int projectId, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQueryRaw<PeriodSheetRow>(NotSubmittedByPeriodSql, new SqlParameter("@projectId", projectId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.PeriodKey, r => r.NotSubmitted);
    }

    /// <summary>Рядок агрегату не поданих аркушів; імена колонок — імена властивостей.</summary>
    public sealed record PeriodSheetRow(int PeriodKey, int NotSubmitted);

    /// <summary>Підсумкові групи по всіх проєктах періоду — один агрегатний запит без стелі.</summary>
    private async Task<IReadOnlyList<CampaignBucket>> BucketsAsync(int periodKey, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQueryRaw<BucketRow>(TotalsSql, new SqlParameter("@periodKey", periodKey))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(r => new CampaignBucket(
            Deadline(r.DeadlineUtc), r.TimeZoneId, r.AllApproved, r.Projects,
            r.Documents, r.Draft, r.Submitted, r.Approved, r.Rejected, r.Sheets, r.NotSubmittedSheets));
    }

    /// <summary>Рядок агрегату етапів; імена колонок — імена властивостей.</summary>
    public sealed record StageRow(
        int ProjectId, int Documents, int Draft, int Submitted, int Approved, int Rejected, int Sheets, int NotSubmittedSheets);

    /// <summary>Рядок підсумкового агрегату; імена колонок — імена властивостей.</summary>
    public sealed record BucketRow(
        DateTime DeadlineUtc, string TimeZoneId, bool AllApproved, int Projects,
        int Documents, int Draft, int Submitted, int Approved, int Rejected, int Sheets, int NotSubmittedSheets);
}
