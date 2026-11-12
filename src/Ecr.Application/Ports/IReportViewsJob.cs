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
/// одного шаблону поспіль дають один прогін, а не чергу однакових. Тому payload визначається ціллю (шаблон), а задача
/// сама бере ВСІ опубліковані версії шаблону — злиття не губить жодної.
/// </remarks>
public interface IReportViewsJob : IBackgroundJob;

/// <summary>Завдання <see cref="IReportViewsJob"/>: шаблон, чиї вʼюхи перегенерувати.</summary>
/// <param name="TemplateId">Шаблон.</param>
public sealed record ReportViewsTask(int TemplateId);

/// <summary>Ціль злиття задач <see cref="IReportViewsJob"/> — шаблон.</summary>
public static class ReportViewsTarget
{
    /// <summary>Ключ цілі для шаблону <paramref name="templateId"/>.</summary>
    /// <param name="templateId">Шаблон.</param>
    public static string Of(int templateId)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"template{templateId}");
}
