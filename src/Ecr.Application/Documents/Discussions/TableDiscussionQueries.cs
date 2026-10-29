// src/Ecr.Application/Documents/Discussions/TableDiscussionQueries.cs
namespace Ecr.Application.Documents.Discussions;

/// <summary>Читання чатів таблиць: підсумок документа, сторінка повідомлень, кандидати у згадки, мої згадки.</summary>
/// <remarks>
/// CONTRACT-ONLY (перший коміт лінії lane/integ-tablechat-server): сигнатури й форми відповідей остаточні, реалізація
/// (доступ, сховище, ETag) — у наступних комітах тієї самої лінії. Лінію не зводити, доки тіла кидають
/// <see cref="NotImplementedException"/>.
/// </remarks>
public sealed class TableDiscussionQueries
{
    /// <summary>Підсумок чатів усіх таблиць документа (один запит).</summary>
    public Task<TableDiscussionSummaryDto> GetSummaryAsync(long documentId, CancellationToken ct)
        => throw new NotImplementedException("table chat: contract commit");

    /// <summary>
    /// Сторінка повідомлень чату таблиці. <paramref name="ifNoneMatch"/> збігся з ревізією — <c>Page = null</c> (304).
    /// </summary>
    public Task<(TableDiscussionPageDto? Page, string ETag)> ListMessagesAsync(
        long documentId, string tableCode, long? before, long? after, int? limit, int? periodKey,
        string? ifNoneMatch, CancellationToken ct)
        => throw new NotImplementedException("table chat: contract commit");

    /// <summary>Кандидати у згадки: лише користувачі, які бачать таблицю.</summary>
    public Task<IReadOnlyList<TableDiscussionMentionCandidateDto>> MentionCandidatesAsync(
        long documentId, string tableCode, string? q, int? periodKey, CancellationToken ct)
        => throw new NotImplementedException("table chat: contract commit");

    /// <summary>Непрочитані згадки поточного користувача в таблицях, які він досі бачить.</summary>
    public Task<TableDiscussionMentionTasksDto> MyMentionsAsync(int? limit, CancellationToken ct)
        => throw new NotImplementedException("table chat: contract commit");
}
