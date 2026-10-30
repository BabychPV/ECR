// src/Ecr.Application/Ports/ITableDiscussionStore.cs
using Ecr.Domain.Entities.Documents;

namespace Ecr.Application.Ports;

/// <summary>Рядок підсумку чату таблиці.</summary>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="Total">Живих повідомлень.</param>
/// <param name="Unread">Чужих живих після позначки «прочитано».</param>
/// <param name="LastAt">Час останнього живого.</param>
/// <param name="LastMessageId">Найбільший id чату.</param>
/// <param name="MentionedMe">Є непрочитана згадка читача.</param>
/// <param name="Revision">Ревізія чату (<c>doc.TableDiscussion.Revision</c>): росте на кожне нове, правлене й видалене.</param>
public sealed record TableDiscussionSummaryRow(
    string TableCode, int Total, int Unread, DateTime? LastAt, long LastMessageId, bool MentionedMe, long Revision);

/// <summary>Повідомлення з іменем автора для відповіді.</summary>
public sealed record TableDiscussionMessageRow(
    long Id, string TableCode, int PeriodKey, int AuthorUserId, string AuthorName, string Body,
    DateTime CreatedAt, DateTime? EditedAt, DateTime? DeletedAt);

/// <summary>Ревізія й межі чату для відповіді та ETag.</summary>
/// <param name="Revision">Ревізія (0 — чат ще не існує).</param>
/// <param name="LastMessageId">Найбільший id чату.</param>
public sealed record TableDiscussionStateRow(long Revision, long LastMessageId);

/// <summary>Згадка для «Моїх задач».</summary>
public sealed record TableDiscussionMentionRow(
    long MessageId, long DocumentId, string TableCode, int PeriodKey, int AuthorUserId, string AuthorName,
    string Body, DateTime CreatedAt);

/// <summary>
/// Сховище чатів таблиць. Читання — без відстеження; запис іде в транзакції викликача (<see cref="IUnitOfWork"/>).
/// </summary>
/// <remarks>
/// ⛔ Порт нічого не знає про права: усі рішення про доступ приймає обробник ДО звернення сюди. Порт не торкається
/// комірок, розрахунків і станів — лише <c>doc.TableDiscussion*</c>.
/// </remarks>
public interface ITableDiscussionStore
{
    /// <summary>
    /// Підсумок чатів документа — ОДНА команда до бази: таблиці <paramref name="fullCodes"/> читаються зі зведень
    /// (всі періоди), таблиці <paramref name="periodRestricted"/> (код → дозволені періоди) — з повідомлень дозволених періодів.
    /// </summary>
    public Task<IReadOnlyList<TableDiscussionSummaryRow>> SummaryAsync(
        long documentId, IReadOnlyCollection<string> fullCodes,
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> periodRestricted, int userId, CancellationToken ct);

    /// <summary>Періоди документа (за його екземплярами таблиць) — для ролей, звужених періодами.</summary>
    public Task<IReadOnlyList<int>> DocumentPeriodsAsync(long documentId, CancellationToken ct);

    /// <summary>Ревізія чату (один пошук за ключем); немає — ревізія 0.</summary>
    public Task<TableDiscussionStateRow> GetStateAsync(long documentId, string tableCode, CancellationToken ct);

    /// <summary>Позначка «прочитано до» користувача (0 — не було).</summary>
    public Task<long> GetLastReadAsync(int userId, long documentId, string tableCode, CancellationToken ct);

    /// <summary>
    /// Повідомлення: <paramref name="after"/> — за зростанням id; інакше найновіші (або старіші за <paramref name="before"/>)
    /// за спаданням. Береться <paramref name="take"/> рядків.
    /// </summary>
    public Task<IReadOnlyList<TableDiscussionMessageRow>> ListAsync(
        long documentId, string tableCode, long? before, long? after, int? periodKey,
        IReadOnlyCollection<int>? allowedPeriods, int take, CancellationToken ct);

    /// <summary>Повідомлення автора з цим <c>clientMessageId</c> у цьому чаті (повтор POST); <c>null</c> — немає.</summary>
    public Task<TableDiscussionMessageRow?> FindByClientIdAsync(
        long documentId, string tableCode, int authorUserId, Guid clientMessageId, CancellationToken ct);

    /// <summary>Найбільший id повідомлення чату, видимий читачеві (у дозволених періодах; <c>null</c> — усі).</summary>
    public Task<long> MaxVisibleIdAsync(
        long documentId, string tableCode, IReadOnlyCollection<int>? allowedPeriods, CancellationToken ct);

    /// <summary>Згадані користувачі за повідомленнями (id → користувачі).</summary>
    public Task<IReadOnlyDictionary<long, IReadOnlyList<int>>> MentionsAsync(
        IReadOnlyCollection<long> messageIds, CancellationToken ct);

    /// <summary>Імена активних і неактивних користувачів за id.</summary>
    public Task<IReadOnlyDictionary<int, string>> UserNamesAsync(IReadOnlyCollection<int> userIds, CancellationToken ct);

    /// <summary>Непрочитані згадки користувача, найновіші першими (до <paramref name="take"/>).</summary>
    public Task<IReadOnlyList<TableDiscussionMentionRow>> MyMentionsAsync(int userId, int take, CancellationToken ct);

    // ── запис (у транзакції викликача) ─────────────────────────────────────────────

    /// <summary>Зведення чату під блокуванням оновлення; створює, якщо немає.</summary>
    public Task<TableDiscussionState> LockStateAsync(long documentId, string tableCode, CancellationToken ct);

    /// <summary>Повідомлення під блокуванням оновлення; <c>null</c> — немає в цьому чаті.</summary>
    public Task<TableDiscussionMessage?> FindMessageForUpdateAsync(
        long documentId, string tableCode, long messageId, CancellationToken ct);

    /// <summary>Додає нове повідомлення до контексту (id з'явиться після <c>SaveChanges</c>).</summary>
    public void Add(TableDiscussionMessage message);

    /// <summary>Замінює згадки повідомлення (старі прибираються, нові додаються); повертає ЩОЙНО додані користувачі.</summary>
    public Task<IReadOnlyList<int>> ReplaceMentionsAsync(
        TableDiscussionMessage message, IReadOnlyCollection<int> userIds, CancellationToken ct);

    /// <summary>Прибирає всі згадки повідомлення (видалення).</summary>
    public Task RemoveMentionsAsync(long messageId, CancellationToken ct);

    /// <summary>Час останнього живого повідомлення чату (після зміни); <c>null</c> — живих немає.</summary>
    public Task<DateTime?> LastLiveAtAsync(long documentId, string tableCode, CancellationToken ct);

    /// <summary>Піднімає «прочитано до» й знімає згадки користувача до цього повідомлення.</summary>
    public Task MarkReadAsync(int userId, long documentId, string tableCode, long upTo, CancellationToken ct);
}
