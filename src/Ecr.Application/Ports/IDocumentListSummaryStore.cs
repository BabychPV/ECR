using Ecr.Application.Documents.Dto;

namespace Ecr.Application.Ports;

/// <summary>Зведення переліку документів за період (<c>BE-09</c>).</summary>
/// <remarks>
/// ⚠ Окремий порт, а не метод <see cref="IDocumentStore"/>: нова функція — у
/// новому файлі, і тринадцять тестових підробок сховища документів не мусять
/// знати про смугу лічильників.
/// </remarks>
public interface IDocumentListSummaryStore
{
    /// <summary>Лічильники ОДНИМ агрегованим запитом, без завантаження переліку.</summary>
    /// <param name="projectId">Фільтр за проєктом; <c>null</c> — усі видимі.</param>
    /// <param name="periodKey">Період: стан аркушів і підсумок перевірки існують лише в ньому.</param>
    /// <param name="visibleProjectIds">
    /// Проєкти з грантом читання — ТА САМА межа, що в
    /// <see cref="IDocumentStore.ListAsync"/>; <c>null</c> — без межі (лише
    /// для перевірок самого запиту).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public Task<DocumentListSummaryResponse> SummarizeAsync(
        int? projectId, int periodKey, IReadOnlyCollection<int>? visibleProjectIds, CancellationToken ct);
}
