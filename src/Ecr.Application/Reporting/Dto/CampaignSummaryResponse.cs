using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Reporting.Dto;

/// <summary>Огляд кампанії звітності за один період (<c>BE-22</c>).</summary>
/// <param name="PeriodKey">Період кампанії — той самий для всіх рядків.</param>
/// <param name="TotalProjects">
/// Скільки проєктів мають цей період УСЬОГО. Більше за довжину
/// <paramref name="Projects"/> — перелік обрізано стелею
/// <see cref="GetCampaignSummaryHandler.MaxProjects"/>, і кампанія бачиться не
/// цілком.
/// </param>
/// <param name="Projects">Проєкти за кодом; лише лічильники, без значень.</param>
/// <remarks>
/// ⛔ Відповідь НЕ обмежена проєктами, на які в користувача є грант — це пряме
/// рішення людини на <c>Q15-07</c>: «окреме право <c>Report.ViewCampaign</c>,
/// видається явно; лише лічильники станів, без значень». Питання «хто затримує
/// кампанію» без чужих проєктів не має відповіді за побудовою.
/// </remarks>
/// <param name="Totals">
/// Підсумки по ВСІХ проєктах періоду, без стелі переліку. ⛔ Не сума
/// <paramref name="Projects"/>: сума по обрізаній підмножині читалася б як
/// стан кампанії й брехала б саме тоді, коли проєктів більше за стелю.
/// </param>
public sealed record CampaignSummaryResponse(
    int PeriodKey,
    int TotalProjects,
    IReadOnlyList<CampaignProjectSummary> Projects,
    CampaignTotals Totals);

/// <summary>Підсумки кампанії по всіх проєктах періоду.</summary>
/// <param name="Projects">Проєктів періоду; дорівнює <c>TotalProjects</c>.</param>
/// <param name="Documents">Документів у цих проєктах.</param>
/// <param name="Draft">Сума <c>Draft</c> по всіх проєктах.</param>
/// <param name="Submitted">Сума <c>Submitted</c>.</param>
/// <param name="Approved">Сума <c>Approved</c>.</param>
/// <param name="Rejected">Сума <c>Rejected</c>.</param>
/// <param name="Snapshots">Сума поточних зрізів.</param>
/// <param name="Done">Проєктів у стані <see cref="CampaignProgress.Done"/>.</param>
/// <param name="Overdue">Проєктів у стані <see cref="CampaignProgress.Overdue"/>.</param>
/// <param name="AtRisk">Проєктів у стані <see cref="CampaignProgress.AtRisk"/>.</param>
/// <param name="InProgress">Проєктів у стані <see cref="CampaignProgress.InProgress"/>.</param>
public sealed record CampaignTotals(
    int Projects,
    int Documents,
    int Draft,
    int Submitted,
    int Approved,
    int Rejected,
    int Snapshots,
    int Done,
    int Overdue,
    int AtRisk,
    int InProgress);

/// <summary>
/// Де проєкт у кампанії відносно строку подання — відповідь на «хто затримує».
/// Правило — <see cref="CampaignProgressRule"/>.
/// </summary>
public enum CampaignProgress
{
    /// <summary>Не готово, до строку більше ніж <c>Campaign:AtRiskDays</c> днів (або строку ще не пораховано).</summary>
    InProgress = 0,

    /// <summary>Не готово, і до останнього дня подання лишилося не більше <c>Campaign:AtRiskDays</c> днів.</summary>
    AtRisk = 1,

    /// <summary>Не готово, а строк подання вже минув.</summary>
    Overdue = 2,

    /// <summary>Усі документи затверджено і є хоча б один поточний зріз.</summary>
    Done = 3,
}

/// <summary>Один проєкт у огляді кампанії.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="ProjectCode">Код проєкту; ним перелік і впорядковано.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="Documents">
/// Скільки документів у проєкті. <c>0</c> означає «кампанія тут ще не
/// починалася» — і це окрема відповідь, а не «все в чернетках».
/// </param>
/// <param name="Draft">Документи, де є аркуш у чернетці (або ще без стану) і жодного відхиленого.</param>
/// <param name="Submitted">Усі аркуші подано або затверджено, і хоч один ще не затверджено.</param>
/// <param name="Approved">Усі аркуші затверджено.</param>
/// <param name="Rejected">Хоч один аркуш відхилено.</param>
/// <param name="Snapshots">
/// Скільки ПОТОЧНИХ зрізів звітності побудовано за цей період
/// (<c>rpt.ReportSnapshot.IsCurrent</c>). Останній етап кампанії: документи
/// затверджено, але доки зрізу немає — регулятор не отримав нічого.
/// </param>
/// <remarks>
/// ⚠ Перші чотири лічильники в сумі дають <paramref name="Documents"/>: стан
/// документа — найгірший зі станів його аркушів
/// (<c>Rejected &gt; Draft &gt; Submitted &gt; Approved</c>), той самий
/// агрегат, що й у смузі переліку документів (<c>BE-09</c>).
/// </remarks>
/// <param name="Progress">Класифікація проєкту в кампанії (<see cref="CampaignProgressRule"/>).</param>
/// <param name="SubmissionDeadline">
/// Строк подання в поясі проєкту, ВИКЛЮЧНО: це момент переходу періоду
/// <c>Open → Grace</c> (<c>Period.ComputedGraceAt</c>, опівніч
/// <c>PeriodEnd + GraceOffsetDays</c>), після якого правки позначаються
/// пізніми (<c>D-70</c>). Останній день подання — доба ПЕРЕД цим моментом.
/// <c>null</c> — межі періоду ще не пораховано.
/// </param>
public sealed record CampaignProjectSummary(
    int ProjectId,
    string ProjectCode,
    LocalizedText NameL10n,
    int Documents,
    int Draft,
    int Submitted,
    int Approved,
    int Rejected,
    int Snapshots,
    CampaignProgress Progress,
    DateTimeOffset? SubmissionDeadline);
