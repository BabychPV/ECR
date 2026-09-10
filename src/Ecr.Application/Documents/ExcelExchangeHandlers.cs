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

        // ⚠ `createdByUserId` — щоб автор прочитав стан ВЛАСНОЇ задачі без
        // System.ViewHealth (Q-156).
        return await jobs
            .EnqueueAsync<IExcelExportJob>(
                new ExcelExportTask(documentId, options, exportId), ct, currentUser.UserId)
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
/// <remarks>
/// ⚠ Директива №11, T10 #45: на відміну від <see cref="ExportDocumentHandler"/>
/// (завжди в чергу), тут поріг — більшість імпортів переглядають і
/// застосовують кілька змінених рядків, і черга додала б лише затримку
/// опитування там, де відповідь готова за долі секунди.
/// </remarks>
public sealed class ApplyImportHandler(
    IExcelImporter importer,
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на імпорт (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.Import";

    /// <summary>
    /// Поріг: скільки змінених комірок іще застосовується синхронно.
    /// </summary>
    /// <remarks>
    /// ⚠ Судження (директива №11, T10 #45), не факт із документа. Орієнтир —
    /// вже задокументовані бюджети запису: <c>ICellStore.ApplyAsync</c> обіцяє
    /// p95 &lt; 150 мс на 100 комірок (`ICellStore.cs`), тобто ~1.5 мс/комірку
    /// на СХОДЖЕННІ SQL, без урахування валідації й перерахунку зверху. Дві
    /// тисячі комірок — це вже кілька секунд разом з накладними витратами
    /// PatchCellsHandler на кожну із задіяних таблиць, комфортно нижче
    /// типового тайм-ауту проксі, але досить високо, щоб рідкісна правка
    /// кількох рядків (типовий випадок) не потрапляла в чергу даремно.
    /// </remarks>
    public const int LargeImportThreshold = 2_000;

    /// <summary>Застосовує diff — синхронно або, для великого, у черзі.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="previewToken">Токен раніше побудованого diff.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Q-178, той самий патерн, що й <see cref="PreviewImportHandler"/>
    /// вище і <see cref="ExportDocumentHandler"/>.
    /// <para>
    /// ⛔ D-134 (T10 #45): без порогу великий імпорт застосовувався б
    /// синхронно завжди, і запит блокувався б доти, доки
    /// <c>PatchCellsHandler</c> не пройде кожну зі змінених таблиць —
    /// секунди чи десятки секунд утримання HTTP-з'єднання й потоку на
    /// операцію, для якої вже є готовий шлях у чергу (як і в експорту).
    /// </para>
    /// </remarks>
    public async Task<ImportApplyResult> HandleAsync(
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

        var pendingCount = await importer.CountPendingChangesAsync(previewToken, ct).ConfigureAwait(false);

        if (pendingCount > LargeImportThreshold)
        {
            var jobId = await jobs
                .EnqueueAsync<IExcelImportJob>(
                    new ExcelImportTask(documentId, previewToken), ct, currentUser.UserId)
                .ConfigureAwait(false);

            return ImportApplyResult.Queued(jobId);
        }

        var applied = await importer.ApplyAsync(documentId, previewToken, ct).ConfigureAwait(false);

        return ImportApplyResult.Applied(applied);
    }
}

/// <summary>Результат застосування імпорту: синхронно ГОТОВО, або в ЧЕРЗІ.</summary>
/// <remarks>
/// ⚠ Рівно одне з двох полів не <c>null</c> — конструктори приховані навмисно
/// (директива №11, T10 #45): виклик через <see cref="Applied"/>/<see cref="Queued"/>
/// не дає зібрати запис, у якому «застосовано» й «у черзі» правдиві одночасно.
/// </remarks>
public sealed record ImportApplyResult
{
    private ImportApplyResult(PatchCellsResponse? response, string? jobId)
    {
        Response = response;
        JobId = jobId;
    }

    /// <summary>Результат синхронного застосування; <c>null</c> — пішло в чергу.</summary>
    public PatchCellsResponse? Response { get; }

    /// <summary>Ідентифікатор фонової задачі; <c>null</c> — застосовано синхронно.</summary>
    public string? JobId { get; }

    /// <summary>Застосовано синхронно.</summary>
    public static ImportApplyResult Applied(PatchCellsResponse response) => new(response, null);

    /// <summary>Поставлено в чергу — завелике для синхронного шляху.</summary>
    public static ImportApplyResult Queued(string jobId) => new(null, jobId);
}

/// <summary>Завдання застосування великого імпорту у фоні.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="PreviewToken">Токен раніше побудованого diff.</param>
public sealed record ExcelImportTask(long DocumentId, string PreviewToken);
