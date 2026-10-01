// src/Ecr.Application/Ports/IReportViewStatus.cs
namespace Ecr.Application.Ports;

/// <summary>Збій генерації вʼюх: що саме не створено і чому.</summary>
/// <param name="TemplateVersionId">Версія, для якої йшов виклик; <c>null</c> — прогін по всіх.</param>
/// <param name="Code">Номер помилки SQL: 50422 (таблиця &gt; 250 колонок), 50409 (зіткнення імен), інший.</param>
/// <param name="Message">Повідомлення процедури: містить шаблон, версію, аркуш і таблицю.</param>
public sealed record ReportViewFailure(int? TemplateVersionId, int Code, string Message);

/// <summary>
/// Останній стан генерації вʼюх <c>rpt.v_*</c> для <c>/health/ready</c> (картка
/// <c>reportviews</c>). Живе в памʼяті: старт завжди перезапускає генерацію по всіх
/// версіях, тож стан після рестарту відновлюється сам — без таблиці й міграції.
/// </summary>
public interface IReportViewStatus
{
    /// <summary>Фіксує збій; той самий ключ (версія) перезаписується.</summary>
    /// <param name="failure">Збій.</param>
    public void Failed(ReportViewFailure failure);

    /// <summary>
    /// Успіх: прогін по всіх (<c>null</c>) скидає всі збої, по одній версії — лише її.
    /// </summary>
    /// <param name="templateVersionId">Версія виклику або <c>null</c>.</param>
    public void Succeeded(int? templateVersionId);

    /// <summary>Поточні незакриті збої.</summary>
    /// <returns>Знімок.</returns>
    public IReadOnlyList<ReportViewFailure> Snapshot();
}
