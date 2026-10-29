// src/Ecr.Domain/Entities/Documents/TableDiscussionMention.cs
namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Згадка користувача в повідомленні чату (<c>doc.TableDiscussionMention</c>): рядок живить «Мої задачі» і зникає, коли
/// користувач прочитав чат до цього повідомлення (або повідомлення видалено).
/// </summary>
/// <remarks>
/// ⛔ Згадка створюється ЛИШЕ для користувача, який сам бачить цю таблицю (перевіряє сервер, не клієнт): інакше згадка
/// виказала б існування таблиці й розмови тому, кому вони приховані.
/// </remarks>
public sealed class TableDiscussionMention
{
    private TableDiscussionMention() { }

    /// <summary>Створює згадку; документ і код — копія з повідомлення, щоб «Мої задачі» читали без з'єднання.</summary>
    public TableDiscussionMention(long messageId, int userId, long documentId, string tableDefCode)
    {
        MessageId = messageId;
        UserId = userId;
        DocumentId = documentId;
        TableDefCode = tableDefCode;
    }

    /// <summary>Повідомлення.</summary>
    public long MessageId { get; private set; }

    /// <summary>Згаданий користувач.</summary>
    public int UserId { get; private set; }

    /// <summary>Документ повідомлення.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Код таблиці повідомлення.</summary>
    public string TableDefCode { get; private set; } = null!;
}
