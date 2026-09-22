using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>Огляд кампанії звітності за період (<c>BE-22</c>).</summary>
/// <remarks>
/// ⚠ Окремий порт, а не метод <see cref="IDocumentListSummaryStore"/>: той
/// лічить документи, які користувач БАЧИТЬ, і межа грантів у ньому обов'язкова
/// за побудовою. Огляд кампанії навпаки — свідомо без межі проєктів (рішення
/// людини <c>Q15-07</c>), і складати дві протилежні відповідальності в один
/// інтерфейс означало б лишити параметр, який у половині викликів має бути
/// <c>null</c>, а в другій — ні.
/// </remarks>
public interface ICampaignSummaryStore
{
    /// <summary>Проєкти періоду з лічильниками етапів, за кодом проєкту.</summary>
    /// <param name="periodKey">Період кампанії.</param>
    /// <param name="limit">
    /// Стеля переліку. Сховище віддає не більше ніж стільки рядків;
    /// <see cref="CampaignProjectPage.Total"/> при цьому лишається ПОВНИМ
    /// числом — інакше обрізаний перелік не відрізнити від вичерпаного.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public Task<CampaignProjectPage> ListAsync(int periodKey, int limit, CancellationToken ct);
}

/// <summary>Сторінка огляду кампанії.</summary>
/// <param name="Total">Скільки проєктів мають цей період усього.</param>
/// <param name="Projects">Не більше ніж <c>limit</c> рядків, за кодом проєкту.</param>
/// <param name="Buckets">
/// Агрегат по ВСІХ проєктах періоду (без стелі), згрупований за тим, від чого
/// залежить класифікація, — щоб підсумки рахувалися тим самим
/// <c>CampaignProgressRule</c>, а не другою його копією в SQL.
/// </param>
public sealed record CampaignProjectPage(
    int Total, IReadOnlyList<CampaignProjectFacts> Projects, IReadOnlyList<CampaignBucket> Buckets);

/// <summary>
/// Рядок проєкту зі сховища: лічильники (як у <c>CampaignProjectSummary</c>),
/// строк подання <c>Period.ComputedGraceAt</c> (<c>null</c> — не пораховано) і
/// пояс проєкту — усе, що потрібно для класифікації.
/// </summary>
public sealed record CampaignProjectFacts(
    int ProjectId,
    string ProjectCode,
    LocalizedText NameL10n,
    int Documents,
    int Draft,
    int Submitted,
    int Approved,
    int Rejected,
    int Snapshots,
    DateTime? SubmissionDeadlineUtc,
    string TimeZoneId);

/// <summary>Група проєктів з однаковими строком, поясом і готовністю; лічильники — суми по групі.</summary>
public sealed record CampaignBucket(
    DateTime? SubmissionDeadlineUtc,
    string TimeZoneId,
    bool AllApproved,
    bool HasSnapshot,
    int Projects,
    int Documents,
    int Draft,
    int Submitted,
    int Approved,
    int Rejected,
    int Snapshots);
