// src/Ecr.Domain/Entities/Documents/TableDiscussionState.cs
namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Зведення чату таблиці (<c>doc.TableDiscussion</c>, ключ «документ × код таблиці»): ревізія, лічильник живих повідомлень,
/// останнє повідомлення. Підтримується в тій самій транзакції, що й кожен запис у чат.
/// </summary>
/// <remarks>
/// Навіщо окремий рядок: <b>підсумок по всіх таблицях документа</b> читає лише ці рядки (за префіксом ключа), а
/// <b>опитування</b> (ETag/304 кожні 15 с) — один пошук за ключем замість проходу всім чатом. Без нього довелося б
/// рахувати <c>COUNT/MAX</c> по всіх повідомленнях таблиці на кожен запит.
/// ⚠ <see cref="Revision"/> зростає на КОЖНУ зміну чату (нове, правка, видалення) — тому клієнт бачить і правки старих
/// повідомлень, а не лише нові.
/// </remarks>
public sealed class TableDiscussionState
{
    private TableDiscussionState() { }

    /// <summary>Створює зведення порожнього чату.</summary>
    public TableDiscussionState(long documentId, string tableDefCode)
    {
        DocumentId = documentId;
        TableDefCode = tableDefCode;
    }

    /// <summary>Документ.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Код таблиці.</summary>
    public string TableDefCode { get; private set; } = null!;

    /// <summary>Лічильник змін чату; ETag опитування.</summary>
    public long Revision { get; private set; }

    /// <summary>Скільки живих (не видалених) повідомлень.</summary>
    public int MessageCount { get; private set; }

    /// <summary>Найбільший ідентифікатор повідомлення чату (включно з видаленими).</summary>
    public long LastMessageId { get; private set; }

    /// <summary>Коли написано останнє живе повідомлення (UTC); <c>null</c> — живих немає.</summary>
    public DateTime? LastMessageAt { get; private set; }

    /// <summary>Нове повідомлення.</summary>
    public void OnPosted(long messageId, DateTime at)
    {
        Revision++;
        MessageCount++;
        LastMessageId = Math.Max(LastMessageId, messageId);
        LastMessageAt = at;
    }

    /// <summary>Правка наявного.</summary>
    public void OnEdited() => Revision++;

    /// <summary>Видалення живого; <paramref name="newLastAt"/> — час нового останнього живого (або <c>null</c>).</summary>
    public void OnDeleted(DateTime? newLastAt)
    {
        Revision++;
        MessageCount = Math.Max(0, MessageCount - 1);
        LastMessageAt = newLastAt;
    }
}
