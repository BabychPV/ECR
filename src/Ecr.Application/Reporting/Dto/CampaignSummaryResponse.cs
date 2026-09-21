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
public sealed record CampaignSummaryResponse(
    int PeriodKey,
    int TotalProjects,
    IReadOnlyList<CampaignProjectSummary> Projects);

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
public sealed record CampaignProjectSummary(
    int ProjectId,
    string ProjectCode,
    LocalizedText NameL10n,
    int Documents,
    int Draft,
    int Submitted,
    int Approved,
    int Rejected,
    int Snapshots);
