// src/Ecr.Domain/Entities/Documents/TableDiscussionRead.cs
namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Позначка «прочитано до» користувача в чаті таблиці (<c>doc.TableDiscussionRead</c>, ключ «користувач × документ × код»).
/// </summary>
/// <remarks>Монотонна: <see cref="Advance"/> ніколи не зменшує значення (повторне або запізніле «прочитано» нічого не псує).</remarks>
public sealed class TableDiscussionRead
{
    private TableDiscussionRead() { }

    /// <summary>Створює позначку.</summary>
    public TableDiscussionRead(int userId, long documentId, string tableDefCode, long lastReadMessageId)
    {
        UserId = userId;
        DocumentId = documentId;
        TableDefCode = tableDefCode;
        LastReadMessageId = lastReadMessageId;
    }

    /// <summary>Користувач.</summary>
    public int UserId { get; private set; }

    /// <summary>Документ.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Код таблиці.</summary>
    public string TableDefCode { get; private set; } = null!;

    /// <summary>Остання прочитана позначка (ідентифікатор повідомлення).</summary>
    public long LastReadMessageId { get; private set; }

    /// <summary>Підіймає позначку; нижча за наявну ігнорується.</summary>
    public void Advance(long messageId)
    {
        if (messageId > LastReadMessageId)
        {
            LastReadMessageId = messageId;
        }
    }
}
