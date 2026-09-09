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
/// </remarks>
public sealed class DownloadExportHandler(
    IExportStore exports,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на експорт (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.Export";

    /// <summary>Читає готову книгу.</summary>
    /// <param name="exportId">Ключ експорту з прогресу задачі.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">
    /// Книги немає або строк її життя вийшов — <c>ECR-DOC-0404</c>.
    /// </exception>
    /// <exception cref="AccessDeniedException">
    /// Немає гранта на документ, з якого побудована книга — <c>ECR-AUTH-0403</c>.
    /// </exception>
    public async Task<byte[]> HandleAsync(string exportId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);

        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⚠ Існування ПЕРЕД грантом — той самий порядок, що й у Q-179: без
        // нього застарілий/невідомий exportId завжди впав би на «немає
        // гранта», ховаючи справжню причину (файл прострочився чи його
        // взагалі не було) за помилковим 403.
        var book = await exports.FindAsync(exportId, ct).ConfigureAwait(false)
                   ?? throw new NotFoundException(
                       "ECR-DOC-0404",
                       "Книги немає або строк її життя вийшов: побудуйте експорт заново.");

        var read = await access.CanReadDocumentAsync(profile, book.DocumentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає доступу до документа {book.DocumentId}: {read.Reason}.");
        }

        return book.Content;
    }
}
