// src/Ecr.Application/Documents/ExcelExchangeHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;

namespace Ecr.Application.Documents;

/// <summary>
/// Експорт документа у <c>.xlsx</c>. Право <c>Document.Export</c>.
/// </summary>
/// <remarks>
/// ⚠ Обробник **ставить задачу в чергу**, а не будує книгу. Бюджет експорту —
/// 10 с p95 (tz/08 §8.2), і це середнє: книга на 500×60×12 будується довше за
/// будь-який розумний HTTP-таймаут. Синхронний експорт працював би на
/// демонстрації і відвалювався б у останній день періоду, коли його
/// запускають усі одразу.
/// </remarks>
public sealed class ExportDocumentHandler(
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на експорт (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.Export";

    /// <summary>Ставить експорт у чергу.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="options">Режим експорту.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Ідентифікатор задачі для опитування стану.</returns>
    public async Task<string> HandleAsync(
        long documentId, ExcelExportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ І ГРАНТ на проєкт (`A7-55`). Експорт віддає документ ЦІЛКОМ —
        // усі числа, підписи й одиниці. Функціональне право каже «цей
        // користувач узагалі вивантажує документи»; грант каже, ЯКІ. Без
        // другої перевірки право `Document.Export`, видане роллю `DataEntry`,
        // відкривало б будь-який проєкт.
        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw new Errors.AccessDeniedException(
                "ECR-AUTH-0403", $"Немає доступу до документа {documentId}: {read.Reason}.");
        }

        // ⚠ Ідентифікатор файлу створюється ТУТ і йде в завданні. Ключ
        // сховища не може дорівнювати jobId: той повертає черга вже після
        // постановки, а задача має знати, куди класти результат, до запуску.
        var exportId = Guid.NewGuid().ToString("N");

        return await jobs
            .EnqueueAsync<IExcelExportJob>(new ExcelExportTask(documentId, options, exportId), ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Завдання на експорт.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="Options">Режим експорту разом із періодом.</param>
/// <param name="ExportId">Ключ, під яким задача покладе готову книгу.</param>
public sealed record ExcelExportTask(long DocumentId, ExcelExportOptions Options, string ExportId);

/// <summary>
/// Попередній перегляд імпорту. Право <c>Document.Import</c>.
/// </summary>
/// <remarks>
/// ⛔ Перегляд — не «зручність», а частина механізму (ФВ-4.3): імпорт без
/// нього непомітно перезаписує чужу роботу. Тому окремий обробник, окреме
/// право і окремий крок.
/// </remarks>
public sealed class PreviewImportHandler(
    IExcelImporter importer,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на імпорт (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.Import";

    /// <summary>Будує diff без застосування.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="file">Книга.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Q-178 (аудит фази 2, авторизація). Узгоджено з патерном
    /// <see cref="ExportDocumentHandler"/> (`A7-55`): глобальне право —
    /// це «користувач узагалі імпортує», грант на документ — «У ЦЕЙ».
    /// Фактичний захист комірок уже був глибше (`ExcelImporter.PreviewAsync`
    /// → `CanEditSliceAsync` на кожен блок), але вхідна перевірка тепер є
    /// й тут — заради того самого дизайну, що й в експорту, а не тому, що
    /// без неї була доведена діра.
    /// </remarks>
    public async Task<ImportPreview> HandleAsync(long documentId, Stream file, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw new Errors.AccessDeniedException(
                "ECR-AUTH-0403", $"Немає доступу до документа {documentId}: {read.Reason}.");
        }

        // ⚠ Синхронно, попри розмір файлу: користувач стоїть над результатом і
        // без нього не може зробити наступний крок. Перегляд у фоні означав би
        // опитування задачі заради того, щоб побачити перелік змін.
        return await importer.PreviewAsync(documentId, file, ct).ConfigureAwait(false);
    }
}

/// <summary>Застосування раніше переглянутого імпорту. Право <c>Document.Import</c>.</summary>
public sealed class ApplyImportHandler(
    IExcelImporter importer,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на імпорт (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.Import";

    /// <summary>Застосовує diff.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="previewToken">Токен раніше побудованого diff.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Q-178, той самий патерн, що й <see cref="PreviewImportHandler"/>
    /// вище і <see cref="ExportDocumentHandler"/>.
    /// </remarks>
    public async Task<PatchCellsResponse> HandleAsync(
        long documentId, string previewToken, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw new Errors.AccessDeniedException(
                "ECR-AUTH-0403", $"Немає доступу до документа {documentId}: {read.Reason}.");
        }

        return await importer.ApplyAsync(documentId, previewToken, ct).ConfigureAwait(false);
    }
}
