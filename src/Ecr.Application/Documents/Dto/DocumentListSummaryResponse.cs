namespace Ecr.Application.Documents.Dto;

/// <summary>Смуга лічильників над переліком документів (<c>BE-09</c>).</summary>
/// <param name="Draft">Документи, де є аркуш у чернетці (або ще без стану) і жодного відхиленого.</param>
/// <param name="Submitted">Усі аркуші подано або затверджено, і хоч один ще не затверджено.</param>
/// <param name="Approved">Усі аркуші затверджено.</param>
/// <param name="Rejected">Хоч один аркуш відхилено.</param>
/// <param name="WithIssues">
/// Документи, у яких ОСТАННІЙ збережений підсумок перевірки має помилки.
/// Неперевірений документ сюди не входить — і «без зауважень» він теж не є.
/// </param>
/// <remarks>
/// ⚠ Стан документа — найгірший зі станів аркушів складу:
/// <c>Rejected &gt; Draft &gt; Submitted &gt; Approved</c>. Перші чотири числа в
/// сумі дають кількість видимих документів; <paramref name="WithIssues"/> —
/// окремий вимір і з ними не складається.
/// </remarks>
public sealed record DocumentListSummaryResponse(
    int Draft, int Submitted, int Approved, int Rejected, int WithIssues);
