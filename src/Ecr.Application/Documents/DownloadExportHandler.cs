// src/Ecr.Application/Documents/DownloadExportHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Documents;

/// <summary>
/// Віддає побудовану книгу <c>.xlsx</c>. Право <c>Document.Export</c>.
/// </summary>
/// <remarks>
/// ⚠ Закриває `P-14`: до цього книга будувалася й лягала у сховище, але
/// забрати її не було звідки — у контракті існував лише
/// <c>POST …/export</c>, який повертає <c>202</c> з <c>jobId</c>.
/// <para>
/// ⛔ Право перевіряється **знову**, а не «вже перевірили при постановці».
/// Між постановкою і завантаженням минає час: ролі могли змінити, а
/// ідентифікатор експорту — переслати іншій людині.
/// </para>
/// <para>
/// ⛔ Q-180 (аудит фази 2, авторизація). До цього перевірявся лише
/// глобальний <see cref="Permission"/> — захистом від чужого документа
/// була практично нездобувна, але єдина лінія оборони: непередбачуваність
/// 128-бітного <c>exportId</c>. Людина визнала прогалину не доведеною
/// вразливістю, але вирішила закрити її так само, як і решту фази 2
/// (`Q-178`, <see cref="ExportDocumentHandler"/>): грант на КОНКРЕТНИЙ
/// документ, а не лише функціональне право. Це вимагало розширити
/// <see cref="IExportStore"/>, щоб він узагалі пам'ятав, з якого документа
/// побудована книга — раніше він зберігав лише байти.
/// </para>
/// <para>
/// ⛔ S20 (аудит безпеки): кожне віддане завантаження — подія
/// <see cref="EventType"/> у журналі безпеки (хто, документ, формат, розмір).
/// Замовлення експорту (<see cref="ExportDocumentHandler"/>) і завантаження —
/// різні події: книгу можна забрати кілька разів за її строк життя.
/// </para>
/// </remarks>
public sealed class DownloadExportHandler(
    IExportStore exports,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IAuditWriter audit,
    Ecr.Domain.Abstractions.IClock clock)
{
    /// <summary>Право на експорт (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.Export";

    /// <summary>Тип події в <c>aud.SecurityEvent</c>.</summary>
    public const string EventType = "DocumentExportDownloaded";

    /// <summary>Читає готову книгу.</summary>
    /// <param name="exportId">Ключ експорту з прогресу задачі.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">
    /// Книги немає, строк її життя вийшов або її замовив інший користувач
    /// (S6) — <c>ECR-DOC-0404</c>, однаково для всіх трьох.
    /// </exception>
    /// <exception cref="AccessDeniedException">
    /// Немає гранта на документ, з якого побудована книга — <c>ECR-AUTH-0403</c>.
    /// </exception>
    public async Task<byte[]> HandleAsync(string exportId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);

        var profile = await Security.PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⚠ Існування ПЕРЕД грантом — той самий порядок, що й у Q-179: без
        // нього застарілий/невідомий exportId завжди впав би на «немає
        // гранта», ховаючи справжню причину (файл прострочився чи його
        // взагалі не було) за помилковим 403.
        var book = await exports.FindAsync(exportId, ct).ConfigureAwait(false);

        // ⛔ S6: книга побудована в межах читання ЗАМОВНИКА — без його
        // заборонених таблиць і колонок, а з рештою. Видимість документа тут
        // не аргумент: колега з тим самим проєктом, але з іншими заборонами,
        // отримав би чужий зріз. Тому віддається лише замовникові, а чужий
        // `exportId` — рівно та сама 404, що й неіснуючий: інакше відповідь
        // сама казала б «такий експорт є, просто не твій». Винятку для
        // адміністратора немає — права «читати чужі експорти» в системі немає.
        var requester = currentUser.UserId ?? profile.UserId;
        if (book is null || book.OwnerUserId != requester)
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                "Книги немає або строк її життя вийшов: побудуйте експорт заново.",
                new Dictionary<string, object?>
                {
                    // ⚠ Без підстановок: `exportId` — внутрішній ключ
                    // задачі, і користувачеві він нічого не каже.
                    ["messageKey"] = "err.ECR-DOC-0404.exportExpired",
                });
        }

        // ⛔ B-08: невидимий документ — 404, як і `GET /documents/{id}`, а не 403
        // «NoGrant»: різниця відповідей сама розкривала б, що документ існує.
        await DocumentVisibility.RequireVisibleAsync(access, profile, book.DocumentId, Permission, ct).ConfigureAwait(false);

        // ⚠ Лише ВІДДАНЕ завантаження: відмови вище не пишуться — вони нічого
        // не віддали, а «чужий exportId» і так рівний неіснуючому (S6).
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                EventType,
                TargetUserId: null,
                TargetRoleId: null,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    documentId = book.DocumentId,
                    format = DocumentExportFormat.OfContent(book.Content).Extension,
                    bytes = book.Content.Length,
                }),
                requester,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return book.Content;
    }
}
