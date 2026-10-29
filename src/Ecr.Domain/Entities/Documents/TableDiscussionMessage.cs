// src/Ecr.Domain/Entities/Documents/TableDiscussionMessage.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Повідомлення чату таблиці документа (<c>doc.TableDiscussionMessage</c>): ОДИН чат на пару «документ × код таблиці»,
/// кожне повідомлення несе період, у якому його написано.
/// </summary>
/// <remarks>
/// ⛔ Це коментар, а не дані: запис сюди ніколи не торкається <c>doc.CellValue</c>, розрахунків і станів аркушів.
/// ⚠ Код таблиці (<see cref="TableDefCode"/>), а не <c>TableDefId</c>: ідентифікатор змінюється з кожною версією шаблону,
/// код — ні, тож розмова переживає перенос документа на нову версію.
/// ⚠ Видалення м'яке: текст лишається в рядку (і в журналі), UI показує «повідомлення видалено» — клієнту текст
/// видаленого не віддається.
/// </remarks>
public sealed class TableDiscussionMessage : Entity<long>
{
    /// <summary>Стеля довжини тексту (символів).</summary>
    public const int MaxBodyLength = 4000;

    /// <summary>Вікно, у якому автор може правити своє повідомлення.</summary>
    public static readonly TimeSpan EditWindow = TimeSpan.FromMinutes(15);

    private TableDiscussionMessage() { }

    /// <summary>Створює повідомлення (текст уже нормалізовано й перевірено на довжину викликачем).</summary>
    public TableDiscussionMessage(long documentId, string tableDefCode, int periodKey, int authorUserId, string body, DateTime utcNow)
    {
        DocumentId = documentId;
        TableDefCode = tableDefCode;
        PeriodKey = periodKey;
        AuthorUserId = authorUserId;
        Body = body;
        CreatedAt = utcNow;
    }

    /// <summary>Документ.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Код таблиці в шаблоні (стабільний між версіями).</summary>
    public string TableDefCode { get; private set; } = null!;

    /// <summary>Період, у якому написано (рік*100+номер).</summary>
    public int PeriodKey { get; private set; }

    /// <summary>Автор.</summary>
    public int AuthorUserId { get; private set; }

    /// <summary>Простий текст.</summary>
    public string Body { get; private set; } = null!;

    /// <summary>Коли написано (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Коли востаннє правлено (UTC); <c>null</c> — не правлено.</summary>
    public DateTime? EditedAt { get; private set; }

    /// <summary>Коли видалено (UTC); <c>null</c> — живе.</summary>
    public DateTime? DeletedAt { get; private set; }

    /// <summary>Хто видалив (автор або модератор).</summary>
    public int? DeletedByUserId { get; private set; }

    /// <summary>Чи видалено.</summary>
    public bool IsDeleted => DeletedAt is not null;

    /// <summary>До якого моменту автор ще може правити (UTC).</summary>
    public DateTime EditableUntil => CreatedAt + EditWindow;

    /// <summary>Чи відкрито вікно правки на момент <paramref name="utcNow"/> (межа включно).</summary>
    public bool IsWithinEditWindow(DateTime utcNow) => utcNow <= EditableUntil;

    /// <summary>Замінює текст.</summary>
    public void Edit(string body, DateTime utcNow)
    {
        Body = body;
        EditedAt = utcNow;
    }

    /// <summary>Видаляє м'яко.</summary>
    public void SoftDelete(int byUserId, DateTime utcNow)
    {
        DeletedAt = utcNow;
        DeletedByUserId = byUserId;
    }
}
