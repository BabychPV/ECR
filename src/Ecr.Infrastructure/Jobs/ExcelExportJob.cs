using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Побудова книги <c>.xlsx</c> у фоні.
/// </summary>
/// <remarks>
/// Бюджет експорту — 10 с p95 (tz/08 §8.2), і це середнє: книга на 500×60×12
/// будується довше за будь-який розумний HTTP-таймаут.
///
/// ⚠ <b>Готовий файл кладеться у сховище експорту</b>, а не віддається у
/// відповідь: операція фонова, і відповіді на неї вже ніхто не чекає.
/// Забирає книгу окремий запит — <c>GET /api/v1/documents/{id}/export/{exportId}</c>
/// (`P-14`).
/// <para>
/// Тримати файл у памʼяті задачі або писати в тимчасову теку інстансу було б
/// гірше: у першому випадку він зникає разом із задачею, у другому — його не
/// бачить інстанс, на який потрапить наступний запит.
/// </para>
/// </remarks>
public sealed class ExcelExportJob(
    IExcelExporter exporter, DocumentDataExporter data, IExportStore exports, IAccessDecisionService access)
    : IExcelExportJob
{
    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "excel-export";

    /// <summary>
    /// Скільки живе готовий файл.
    /// </summary>
    /// <remarks>
    /// Година — це час, за який людина встигає забрати файл, за яким сама ж
    /// прийшла. Довше тримати немає сенсу: книга — знімок даних на момент
    /// побудови, і за добу вона вже не відповідає документу.
    /// </remarks>
    public static TimeSpan Lifetime => TimeSpan.FromHours(1);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var task = ExportPayload.Parse(payload);

        // ⛔ S6: файл кладеться під замовником, і завантажує його лише він.
        // Завдання без замовника (поставлене до цієї правки) не будує нічого:
        // файл без власника не віддав би ніхто, а будувати його — марна робота.
        var ownerUserId = task.RequestedByUserId
                          ?? throw new Ecr.Application.Errors.AccessDeniedException(
                              "ECR-AUTH-0401",
                              "Завдання експорту не називає замовника: побудуйте експорт заново.",
                              new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        await progress.ReportKeyAsync(10, "jobs.exportReadingDocument", ct).ConfigureAwait(false);

        var options = await WithReadScopeAsync(task, ownerUserId, ct).ConfigureAwait(false);

        byte[] content;
        if (task.Format is null or DocumentExportFormat.Xlsx)
        {
            await using var book = await exporter
                .ExportAsync(task.DocumentId, options, ct)
                .ConfigureAwait(false);

            using var buffer = new MemoryStream();
            await book.CopyToAsync(buffer, ct).ConfigureAwait(false);
            content = buffer.ToArray();
        }
        else
        {
            content = await data
                .ExportAsync(
                    task.DocumentId, options.PeriodKey, task.Format, options.IncludeFormulas,
                    options.HiddenTableDefIds, options.HiddenColumnDefIds, ct)
                .ConfigureAwait(false);
        }

        await progress.ReportKeyAsync(80, "jobs.exportSavingWorkbook", ct).ConfigureAwait(false);

        await exports
            .SaveAsync(task.ExportId, task.DocumentId, ownerUserId, content, Lifetime, ct)
            .ConfigureAwait(false);

        // ⚠ Q-326: НЕ конвертується на структурований ключ. Ключ
        // повідомляється в прогресі: саме за ним клієнт (`ExportButton.tsx`),
        // побачивши завершення задачі, забирає книгу — `exportId` тут ДАНІ,
        // а не текст для людини, і `JobProgressMessageResolver` пропускає
        // будь-який рядок, що не є JSON-конвертом, без змін.
        await progress.ReportAsync(100, task.ExportId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Режим експорту з межами читання: ті, що приїхали в завданні, або — коли
    /// їх немає — пораховані тут від імені замовника.
    /// </summary>
    /// <remarks>
    /// ⛔ S6, закрито за замовчуванням. <c>null</c> у межах — це завдання, що
    /// не несе їх (поставлене до S6 або викликачем, який їх не порахував), а
    /// не «заборон немає». Раніше такий експорт ішов без фільтра, тобто
    /// віддавав заборонені таблиці й колонки. Тепер межі рахуються на момент
    /// виконання тим самим <see cref="IAccessDecisionService.ReadScopeAsync"/>,
    /// що й у <c>ExportDocumentHandler</c>, для профілю замовника.
    /// Порожній список (заборон справді немає) — не <c>null</c> і не
    /// перераховується.
    /// </remarks>
    private async Task<ExcelExportOptions> WithReadScopeAsync(ExcelExportTask task, int ownerUserId, CancellationToken ct)
    {
        if (task.Options.HiddenTableDefIds is not null && task.Options.HiddenColumnDefIds is not null)
        {
            return task.Options;
        }

        var profile = await access.BuildProfileAsync(ownerUserId, ct).ConfigureAwait(false);
        var readable = await access.ReadScopeAsync(profile, task.DocumentId, ct).ConfigureAwait(false);

        return task.Options with
        {
            HiddenTableDefIds = readable.HiddenTableIds(),
            HiddenColumnDefIds = readable.HiddenColumnIds(),
        };
    }
}

/// <summary>Розбір завдання експорту.</summary>
internal static class ExportPayload
{
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Розбирає завдання черги.</summary>
    public static ExcelExportTask Parse(object? payload)
    {
        if (payload is ExcelExportTask typed)
        {
            return typed;
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);

        return System.Text.Json.JsonSerializer.Deserialize<ExcelExportTask>(json, Options)
               ?? throw new InvalidOperationException(
                   "Завдання експорту не розбирається: невідома форма payload.");
    }
}
