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
    /// <param name="restrictions">
    /// Аркуші, схованих від читача: їхній стан не рахується; документи проєктів зі звуженням
    /// не потрапляють у «З зауваженнями» (збережений підсумок — по всьому документу).
    /// <c>null</c> — читач без обмежень, лічильники як є.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public Task<DocumentListSummaryResponse> SummarizeAsync(
        int? projectId, int periodKey, IReadOnlyCollection<int>? visibleProjectIds,
        SummaryRestrictions? restrictions, CancellationToken ct);

    /// <summary>По одному документу кожного з проєктів — щоб побудувати межі читання проєкту.</summary>
    /// <param name="projectIds">Проєкти.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyDictionary<int, long>> SampleDocumentPerProjectAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct);
}

/// <summary>Обмеження читача для зведення: приховані аркуші й проєкти зі звуженням.</summary>
/// <param name="HiddenSheets">Пари «проєкт, код аркуша», яких читач не бачить.</param>
/// <param name="NarrowedProjects">Проєкти, де читач не бачить хоч щось (аркуш, таблицю чи колонку).</param>
public sealed record SummaryRestrictions(
    IReadOnlyCollection<(int ProjectId, string SheetCode)> HiddenSheets,
    IReadOnlyCollection<int> NarrowedProjects);
