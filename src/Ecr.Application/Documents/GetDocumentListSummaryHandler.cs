using Ecr.Application.Common;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Зведення переліку документів за період (<c>BE-09</c>). Право <c>Document.View</c>.</summary>
public sealed class GetDocumentListSummaryHandler(
    IDocumentListSummaryStore summary, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Лічильники по документах, які користувач БАЧИТЬ.</summary>
    /// <param name="projectId">Фільтр за проєктом; <c>null</c> — усі видимі.</param>
    /// <param name="periodKey">Період; без нього стан документа не визначений (<c>D-93</c>).</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<DocumentListSummaryResponse> HandleAsync(
        int? projectId, int periodKey, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListDocumentsHandler.Permission, ct)
            .ConfigureAwait(false);

        // ⛔ Межа видимості — ТА САМА функція, що й у переліку, і йде вона в
        // ЗАПИТ: смуга, порахована по всіх проєктах, розійшлася б із таблицею
        // під нею і розкрила б, скільки документів у чужих проєктах (§3.3).
        var visibleProjects = ListDocumentsHandler.ReadableProjects(profile);

        return await summary
            .SummarizeAsync(projectId, PeriodKey.Parse(periodKey).Value, visibleProjects, ct)
            .ConfigureAwait(false);
    }
}
