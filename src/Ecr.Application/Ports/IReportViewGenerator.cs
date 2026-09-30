// src/Ecr.Application/Ports/IReportViewGenerator.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Генерує пласкі вʼюхи <c>rpt.v_&lt;Звіт&gt;_v&lt;Версія&gt;</c> для SSRS з
/// опублікованих версій звітів (<c>ФВ-10.2</c>, <c>ФВ-10.4</c>).
/// </summary>
/// <remarks>
/// ⛔ Застосунок DDL не виконує (<c>D-14</c>, <c>D-66</c>): реалізація лише
/// ВИКЛИКАЄ процедуру <c>rpt.usp_GenerateReportViews</c>, а вʼюху створює
/// вона — від імені власника. Виклик ідемпотентний: незмінна версія дає
/// побайтно ту саму вʼюху, і повторний виклик її не чіпає.
/// </remarks>
public interface IReportViewGenerator
{
    /// <summary>Створює або оновлює вʼюхи опублікованих версій.</summary>
    /// <param name="reportDefId">Опис звіту; <c>null</c> — усі звіти.</param>
    /// <param name="ct">Скасування.</param>
    public Task GenerateAsync(int? reportDefId, CancellationToken ct);
}
