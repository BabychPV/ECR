// src/Ecr.Application/Documents/Discussions/TableDiscussionDtos.cs
namespace Ecr.Application.Documents.Discussions;

/// <summary>Згадка в повідомленні: лише користувач, який сам бачить таблицю.</summary>
/// <param name="UserId">Користувач.</param>
/// <param name="Name">Відображуване ім'я.</param>
public sealed record TableDiscussionMentionDto(int UserId, string Name);

/// <summary>Повідомлення чату таблиці.</summary>
/// <param name="Id">Ідентифікатор; зростає з часом (ключ пагінації).</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="PeriodKey">Період, у якому написано (рік*100+номер).</param>
/// <param name="AuthorUserId">Автор.</param>
/// <param name="AuthorName">Ім'я автора.</param>
/// <param name="Body">Простий текст (ніколи не HTML). <c>null</c> для видаленого — текст видаленого клієнту не віддається.</param>
/// <param name="IsDeleted">Видалено: UI показує «повідомлення видалено».</param>
/// <param name="CreatedAt">Коли написано (UTC).</param>
/// <param name="EditedAt">Коли правлено (UTC); <c>null</c> — не правлено.</param>
/// <param name="DeletedAt">Коли видалено (UTC); <c>null</c> — живе.</param>
/// <param name="CanEdit">Поточний користувач може правити зараз (автор і вікно 15 хв ще відкрите).</param>
/// <param name="EditableUntil">До якого моменту (UTC) можна правити; <c>null</c> — правка недоступна.</param>
/// <param name="CanDelete">Поточний користувач може видалити (автор або модератор).</param>
/// <param name="Mentions">Згадані користувачі (лише ті, хто бачить таблицю).</param>
public sealed record TableDiscussionMessageDto(
    long Id,
    string TableCode,
    int PeriodKey,
    int AuthorUserId,
    string AuthorName,
    string? Body,
    bool IsDeleted,
    DateTime CreatedAt,
    DateTime? EditedAt,
    DateTime? DeletedAt,
    bool CanEdit,
    DateTime? EditableUntil,
    bool CanDelete,
    IReadOnlyList<TableDiscussionMentionDto> Mentions);

/// <summary>Сторінка повідомлень чату таблиці.</summary>
/// <param name="Messages">Повідомлення за зростанням <c>id</c> (старіші першими).</param>
/// <param name="HasMoreBefore">Є старіші за першу повідомлену (для <c>before</c>).</param>
/// <param name="Revision">Ревізія чату (та сама, що в <c>ETag</c>): зростає на кожну зміну, включно з правкою й видаленням.</param>
/// <param name="LastMessageId">Найбільший <c>id</c> чату; для <c>after</c> і для «прочитано».</param>
/// <param name="LastReadMessageId">До якого повідомлення поточний користувач прочитав чат.</param>
/// <param name="CanWrite">Поточний користувач може писати (<c>Document.Discuss</c> і доступ до таблиці).</param>
/// <param name="CanModerate">Поточний користувач може видаляти чужі повідомлення (<c>Document.DiscussModerate</c>).</param>
public sealed record TableDiscussionPageDto(
    IReadOnlyList<TableDiscussionMessageDto> Messages,
    bool HasMoreBefore,
    long Revision,
    long LastMessageId,
    long LastReadMessageId,
    bool CanWrite,
    bool CanModerate);

/// <summary>Підсумок чату однієї таблиці.</summary>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="Total">Скільки живих повідомлень.</param>
/// <param name="Unread">Скільки чужих живих повідомлень після позначки «прочитано».</param>
/// <param name="LastAt">Коли написано останнє живе (UTC); <c>null</c> — чат порожній.</param>
/// <param name="LastMessageId">Найбільший <c>id</c> чату.</param>
/// <param name="MentionedMe">Є непрочитана згадка поточного користувача.</param>
/// <param name="Revision">
/// Ревізія чату: росте на кожне нове, правлене чи видалене повідомлення (та сама, що в ETag сторінки повідомлень).
/// Опитування порівнює її з попередньою і перечитує сторінку лише тоді, коли вона змінилась.
/// </param>
public sealed record TableDiscussionSummaryItemDto(
    string TableCode, int Total, int Unread, DateTime? LastAt, long LastMessageId, bool MentionedMe, long Revision);

/// <summary>Підсумок чатів усіх таблиць документа, які бачить користувач; таблиці без розмов — відсутні.</summary>
/// <param name="Items">Таблиці з хоча б одним повідомленням.</param>
/// <param name="CanWrite">Користувач має право <c>Document.Discuss</c> у проєкті документа.</param>
public sealed record TableDiscussionSummaryDto(IReadOnlyList<TableDiscussionSummaryItemDto> Items, bool CanWrite);

/// <summary>Тіло створення повідомлення.</summary>
/// <param name="Body">Простий текст, 1..4000 символів після нормалізації (керівні символи прибираються).</param>
/// <param name="PeriodKey">Період, у якому пишуть (рік*100+номер).</param>
/// <param name="MentionUserIds">Згадані користувачі (до 10). Ті, хто не бачить таблицю, мовчки відкидаються.</param>
/// <param name="ClientMessageId">
/// Необов'язковий GUID клієнта для безпечного повтору: повторний POST з тим самим значенням (той самий автор, документ і
/// таблиця) повертає ІСНУЮЧЕ повідомлення з <c>200</c>, а не створює друге; нове повідомлення — <c>201</c>.
/// </param>
public sealed record PostTableDiscussionMessageRequest(
    string Body, int PeriodKey, IReadOnlyList<int>? MentionUserIds = null, Guid? ClientMessageId = null);

/// <summary>Тіло правки повідомлення.</summary>
/// <param name="Body">Новий текст.</param>
/// <param name="MentionUserIds">Повний набір згаданих після правки; <c>null</c> — лишити як було.</param>
public sealed record EditTableDiscussionMessageRequest(
    string Body, IReadOnlyList<int>? MentionUserIds = null);

/// <summary>Тіло позначки «прочитано».</summary>
/// <param name="LastReadMessageId">Ідентифікатор останнього побаченого повідомлення (поріг; не зменшує наявну позначку).</param>
public sealed record MarkTableDiscussionReadRequest(long LastReadMessageId);

/// <summary>Кандидат у згадку: лише користувач, який бачить таблицю.</summary>
/// <param name="UserId">Користувач.</param>
/// <param name="Name">Відображуване ім'я.</param>
public sealed record TableDiscussionMentionCandidateDto(int UserId, string Name);

/// <summary>Непрочитана згадка поточного користувача («Мої задачі»).</summary>
/// <param name="MessageId">Повідомлення.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="PeriodKey">Період повідомлення.</param>
/// <param name="AuthorUserId">Автор.</param>
/// <param name="AuthorName">Ім'я автора.</param>
/// <param name="Snippet">Початок тексту (до 160 символів).</param>
/// <param name="CreatedAt">Коли написано (UTC).</param>
public sealed record TableDiscussionMentionTaskDto(
    long MessageId, long DocumentId, string TableCode, int PeriodKey, int AuthorUserId, string AuthorName,
    string Snippet, DateTime CreatedAt);

/// <summary>Непрочитані згадки поточного користувача, найновіші першими.</summary>
/// <param name="Items">Згадки в таблицях, які користувач досі бачить.</param>
public sealed record TableDiscussionMentionTasksDto(IReadOnlyList<TableDiscussionMentionTaskDto> Items);
