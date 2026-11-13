// src/Ecr.Application/Ports/IReportViewsJob.cs
namespace Ecr.Application.Ports;

/// <summary>
/// ✎ P-4b (D-479): перегенерація вʼюх SSRS <c>rpt.v_*</c> шаблону ПІСЛЯ публікації його версії — фоновою задачею, а не
/// в HTTP-запиті публікації (<c>ReportViewsJob</c>, <c>Ecr.Infrastructure</c>).
/// </summary>
/// <remarks>
/// ⛔ На реальному шаблоні Land (20 аркушів, 86 таблиць, 6 230 колонок) публікація тривала ~131 с, і майже все — генерація
/// вʼюх: людина чекала на те, що SSRS-шару потрібно, а публікації — ні. Ставиться ЗЛИТТЯМ на шаблон
/// (<see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/>, <see cref="ReportViewsTarget"/>): кілька публікацій
/// одного шаблону поспіль дають один прогін, а не чергу однакових.
/// ✎ P-4b P2 (F13 на великому бекапі: 25 версій — 42 хв): задача будує <c>_vN</c> лише версій, що чекають
/// (<see cref="IReportViewStatus.Pending(int, int)"/> — набір переживає злиття, тож payload, який злиття замінило, не губить
/// своєї версії) і версії з payload; набір порожній (рестарт) — лише поточна опублікована. Стабільні вʼюхи шаблону — один
/// раз на прогін.
/// </remarks>
public interface IReportViewsJob : IBackgroundJob;

/// <summary>Завдання <see cref="IReportViewsJob"/>: шаблон, чиї вʼюхи перегенерувати.</summary>
/// <param name="TemplateId">Шаблон.</param>
/// <param name="TemplateVersionId">
/// ✎ P-4b P2: опублікована версія, що поставила задачу; <c>null</c> — немає (старий payload без поля теж дає <c>null</c>).
/// </param>
public sealed record ReportViewsTask(int TemplateId, int? TemplateVersionId = null);

/// <summary>Ціль злиття задач <see cref="IReportViewsJob"/> — шаблон.</summary>
public static class ReportViewsTarget
{
    /// <summary>Ключ цілі для шаблону <paramref name="templateId"/>.</summary>
    /// <param name="templateId">Шаблон.</param>
    public static string Of(int templateId)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"template{templateId}");
}
