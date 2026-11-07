namespace Ecr.Application.Documents.Dto;

/// <summary>Смуга лічильників над переліком документів (<c>BE-09</c>).</summary>
/// <param name="Draft">
/// ✎ UAT-recalc-ux: документи, де ЖОДЕН видимий аркуш не поданий і не затверджений, і жодного відхиленого (раніше
/// сюди входили й частково подані — тепер вони в <paramref name="PartlySubmitted"/>).
/// </param>
/// <param name="Submitted">Усі аркуші подано або затверджено, і хоч один ще не затверджено.</param>
/// <param name="Approved">Усі аркуші затверджено.</param>
/// <param name="Rejected">Хоч один аркуш відхилено.</param>
/// <param name="WithIssues">
/// Документи, у яких ОСТАННІЙ збережений підсумок перевірки має помилки.
/// Неперевірений документ сюди не входить — і «без зауважень» він теж не є.
/// </param>
/// <param name="SheetsApproved">
/// Скільки аркушів складу затверджено — у сумі по документах переліку й ЛИШЕ по аркушах, які
/// читач бачить (UI-18: «23 із 60 затверджено»). Схований аркуш не рахується ні тут, ні в
/// <paramref name="SheetsTotal"/>: інакше різниця двох чисел розкривала б, скільки їх.
/// </param>
/// <param name="SheetsTotal">Скільки аркушів складу бачить читач у цих документах (без схованих).</param>
/// <param name="StaleResultsCount">
/// Документи зі застарілими результатами методологій за період (після останнього прогону правили вхід).
/// ⛔ Проєкти, де читач не бачить хоч щось нижче проєкту, не рахуються (як <paramref name="WithIssues"/>):
/// число по всьому документу розкрило б активність схованого. Те саме число дає фільтр <c>resultsStale=true</c>.
/// </param>
/// <param name="StaleResultsMineCount">Те саме, але лише через правки поточного користувача (<c>staleBy=me</c>).</param>
/// <param name="PartlySubmitted">
/// ✎ UAT-recalc-ux: частина видимих аркушів подана чи затверджена, частина — ні (чернетка або без стану), і жодного
/// відхиленого.
/// </param>
/// <remarks>
/// ⚠ Стан документа — за аркушами складу, які читач бачить (`02-contracts.md`, «Стан погодження документа»):
/// <c>Rejected</c> (хоч один повернуто) → <c>Draft</c> (жодного поданого) → <c>PartlySubmitted</c> →
/// <c>Submitted</c> → <c>Approved</c>. П'ять чисел у сумі дають кількість видимих документів;
/// <paramref name="WithIssues"/> — окремий вимір і з ними не складається.
/// </remarks>
public sealed record DocumentListSummaryResponse(
    int Draft, int Submitted, int Approved, int Rejected, int WithIssues,
    int SheetsApproved = 0, int SheetsTotal = 0, int StaleResultsCount = 0, int StaleResultsMineCount = 0,
    int PartlySubmitted = 0);
