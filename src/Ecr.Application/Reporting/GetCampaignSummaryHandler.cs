using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Reporting.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Reporting;

/// <summary>
/// Огляд кампанії звітності: усі проєкти періоду в одному переліку
/// (<c>BE-22</c>). Право <c>Report.ViewCampaign</c>.
/// </summary>
/// <remarks>
/// ⛔ Тут НЕМАЄ межі видимості проєктів — і це не пропуск. Пряме рішення
/// людини на <c>Q15-07</c>: «окреме право <c>Report.ViewCampaign</c>,
/// видається явно; лише лічильники станів, без значень». Керівник кампанії має
/// бачити, хто саме її затримує; з предикатом грантів огляд відповідав би на
/// питання «як просуваються МОЇ проєкти», тобто на те саме, що вже відповідає
/// смуга переліку документів (<c>BE-09</c>).
///
/// ⚠ Ціна рішення названа прямо: право відкриває КОДИ Й НАЗВИ всіх проєктів
/// разом із тим, скільки в кожного документів. Саме тому воно окреме й
/// небезпечне в каталозі (<c>IsDangerous = 1</c>): шаблон <c>Report.%</c>
/// складеної ролі <c>Approver</c> його не роздає, адміністратор видає свідомо,
/// і в журналі безпеки лишається слід.
///
/// ⛔ Значень тут немає жодного: ані комірок, ані сум зрізу — лише лічильники.
/// Огляд, що показував би числа чужого проєкту, був би обходом грантів, а не
/// правом на огляд.
/// </remarks>
public sealed class GetCampaignSummaryHandler(
    ICampaignSummaryStore store,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    CampaignProgressPolicy policy)
{
    /// <summary>Право огляду кампанії по всіх проєктах.</summary>
    public const string Permission = "Report.ViewCampaign";

    /// <summary>
    /// Стеля переліку проєктів у одній відповіді.
    /// </summary>
    /// <remarks>
    /// ⚠ Огляд — ОДИН запит без пагінації: сторінка по кампанії, яку читають,
    /// щоб знайти відстаючого, розкладає роботу на гортання. Стеля лишається
    /// обов'язковою, бо проєктів у системі не обмежує ніщо, а
    /// <see cref="CampaignSummaryResponse.TotalProjects"/> чесно каже, скільки
    /// їх насправді — обрізаний перелік не прикидається повним.
    /// </remarks>
    public const int MaxProjects = 200;

    /// <summary>Зведення кампанії за період.</summary>
    /// <param name="periodKey">Період; поза періодом стан документа не визначений (<c>D-93</c>).</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<CampaignSummaryResponse> HandleAsync(int periodKey, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var key = PeriodKey.Parse(periodKey);
        var page = await store.ListAsync(key.Value, MaxProjects, ct).ConfigureAwait(false);

        var now = clock.UtcNow;
        var zones = new Dictionary<string, TimeZoneInfo>(StringComparer.Ordinal);
        TimeZoneInfo Zone(string id)
            => zones.TryGetValue(id, out var zone) ? zone : zones[id] = TimeZoneInfo.FindSystemTimeZoneById(id);

        var rows = page.Projects.Select(p =>
        {
            var zone = Zone(p.TimeZoneId);
            var progress = CampaignProgressRule.Classify(
                CampaignProgressRule.AllApproved(p.Documents, p.Approved), p.Snapshots > 0,
                p.SubmissionDeadlineUtc, zone, now, policy.AtRiskDays);

            return new CampaignProjectSummary(
                p.ProjectId, p.ProjectCode, p.NameL10n,
                p.Documents, p.Draft, p.Submitted, p.Approved, p.Rejected, p.Snapshots,
                progress,
                p.SubmissionDeadlineUtc is { } deadline ? ToSite(deadline, zone) : null);
        }).ToList();

        // ⛔ Підсумки — з агрегату по ВСІХ проєктах періоду, а не з `rows`:
        // сума по обрізаному переліку читалася б як стан кампанії.
        var progressCounts = new Dictionary<CampaignProgress, int>();
        foreach (var bucket in page.Buckets)
        {
            var progress = CampaignProgressRule.Classify(
                bucket.AllApproved, bucket.HasSnapshot, bucket.SubmissionDeadlineUtc,
                Zone(bucket.TimeZoneId), now, policy.AtRiskDays);
            progressCounts[progress] = progressCounts.GetValueOrDefault(progress) + bucket.Projects;
        }

        var totals = new CampaignTotals(
            page.Buckets.Sum(b => b.Projects),
            page.Buckets.Sum(b => b.Documents),
            page.Buckets.Sum(b => b.Draft),
            page.Buckets.Sum(b => b.Submitted),
            page.Buckets.Sum(b => b.Approved),
            page.Buckets.Sum(b => b.Rejected),
            page.Buckets.Sum(b => b.Snapshots),
            progressCounts.GetValueOrDefault(CampaignProgress.Done),
            progressCounts.GetValueOrDefault(CampaignProgress.Overdue),
            progressCounts.GetValueOrDefault(CampaignProgress.AtRisk),
            progressCounts.GetValueOrDefault(CampaignProgress.InProgress));

        return new CampaignSummaryResponse(key.Value, page.Total, rows, totals);
    }

    /// <summary>UTC → момент у поясі проєкту зі збереженим зсувом (як у календарі періодів).</summary>
    private static DateTimeOffset ToSite(DateTime utc, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), zone);
}
