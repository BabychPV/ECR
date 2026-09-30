// src/Ecr.Application/Ports/IReportViewGenerator.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Генерує «широкі» вʼюхи сирих даних <c>rpt.v_&lt;Шаблон&gt;_&lt;Аркуш&gt;_&lt;Таблиця&gt;_v&lt;Версія&gt;</c>
/// для SSRS — по одній на таблицю опублікованої версії шаблону
/// (<c>ФВ-10.2</c>, <c>ФВ-10.4</c>; рішення людини 2026-09-30: SSRS отримує
/// сирі дані й будує звіти сам).
/// </summary>
/// <remarks>
/// ⛔ Застосунок DDL не виконує (<c>D-14</c>, <c>D-66</c>): реалізація лише
/// ВИКЛИКАЄ процедуру <c>rpt.usp_GenerateTemplateViews</c>, а вʼюху створює
/// вона — від імені власника. Виклик ідемпотентний: незмінна версія дає
/// побайтно ту саму вʼюху, і повторний виклик її не чіпає.
/// </remarks>
public interface IReportViewGenerator
{
    /// <summary>Створює або оновлює вʼюхи таблиць опублікованих версій шаблонів.</summary>
    /// <param name="templateVersionId">Версія шаблону; <c>null</c> — усі опубліковані.</param>
    /// <param name="ct">Скасування.</param>
    public Task GenerateAsync(int? templateVersionId, CancellationToken ct);
}
