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
    /// <param name="currentUserId">Користувач для <c>StaleResultsMineCount</c> (правки цього користувача); <c>null</c> — «моїх» немає.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<DocumentListSummaryResponse> SummarizeAsync(
        int? projectId, int periodKey, IReadOnlyCollection<int>? visibleProjectIds,
        SummaryRestrictions? restrictions, int? currentUserId, CancellationToken ct);

    /// <summary>По одному документу кожного з проєктів — щоб побудувати межі читання проєкту.</summary>
    /// <param name="projectIds">Проєкти.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyDictionary<int, long>> SampleDocumentPerProjectAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct);
}

/// <summary>Обмеження читача для зведення: приховані аркуші й проєкти зі звуженням.</summary>
/// <param name="HiddenSheets">Пари «проєкт, код аркуша», яких читач не бачить.</param>
/// <param name="NarrowedProjects">Проєкти, де читач не бачить хоч щось (аркуш, таблицю чи колонку).</param>
/// <param name="Participants">
/// D-444 (AN-131): документи, відкриті читачеві наданням учасника, — ДОДАТКОВО до проєктів; <c>null</c> — немає.
/// </param>
public sealed record SummaryRestrictions(
    IReadOnlyCollection<(int ProjectId, string SheetCode)> HiddenSheets,
    IReadOnlyCollection<int> NarrowedProjects,
    ParticipantSummaryScope? Participants = null);

/// <summary>
/// D-444 (AN-131): документи учасника в зведенні — та сама множина, що додає перелік (<c>DocumentParticipationFilter</c>).
/// </summary>
/// <param name="DocumentIds">Документи з діючим наданням учасника.</param>
/// <param name="DeniedProjectIds">Проєкти з забороною ролі: їхніх документів жодне надання не відкриває.</param>
/// <param name="HiddenSheets">
/// Пари «документ, код аркуша», яких учасник у ЦЬОМУ документі не бачить: межі документа-надання власні (не проєкту).
/// </param>
/// <param name="WholeDocumentIds">
/// ✎ D-446: документи-надання, які учасник бачить ЦІЛКОМ (жодного схованого аркуша, таблиці чи колонки), — лише вони
/// входять у лічильники застарілих результатів (та сама множина, що в переліку під фільтром <c>resultsStale</c>);
/// <c>null</c> — жодного.
/// </param>
/// <param name="IssueDocumentIds">
/// ✎ P45: документи-надання, у межах яких учасник не має жодної схованої таблиці чи колонки, — кандидати в «З
/// зауваженнями»; запит додатково відкидає документ, де схований хоч один аркуш його СКЛАДУ. Разом це рівно умова, за
/// якої рядок переліку показує <c>errorCount</c> (<c>DocumentSheetVisibility.For</c>: <c>IsNarrowed</c> по аркушах
/// складу); <c>null</c> — жодного.
/// </param>
/// <remarks>
/// ✎ P45 (борг D-446): документ-надання входить у «З зауваженнями» за тим самим правилом, що й лічильник помилок у його
/// рядку: видно учасникові цілком у межах складу — рахується; схована таблиця, колонка чи аркуш складу — ні (збережений
/// підсумок — по ВСЬОМУ документу, і рядок теж показує <c>null</c>). Документ-надання рахується за власними межами й
/// тоді, коли його проєкт видимий за роллю: рядок учасника бере межі документа, а не проєкту.
/// ✎ D-446: застарілість — теж по всьому документу, тож у лічильник застарілих документ-надання входить лише тоді, коли
/// учасник бачить його цілком (<paramref name="WholeDocumentIds"/>); частково схований — ні тут, ні у фільтрі переліку
/// <c>resultsStale</c> (рядок — <c>null</c>). Лічильника пізніх правок смуга не має.
/// </remarks>
public sealed record ParticipantSummaryScope(
    IReadOnlyCollection<long> DocumentIds,
    IReadOnlyCollection<int> DeniedProjectIds,
    IReadOnlyCollection<(long DocumentId, string SheetCode)> HiddenSheets,
    IReadOnlyCollection<long>? WholeDocumentIds = null,
    IReadOnlyCollection<long>? IssueDocumentIds = null);
