// src/Ecr.Application/Documents/DownloadExportHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;

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
    public async Task<byte[]> HandleAsync(string exportId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        return await exports.FindAsync(exportId, ct).ConfigureAwait(false)
               ?? throw new NotFoundException(
                   "ECR-DOC-0404",
                   "Книги немає або строк її життя вийшов: побудуйте експорт заново.");
    }
}
