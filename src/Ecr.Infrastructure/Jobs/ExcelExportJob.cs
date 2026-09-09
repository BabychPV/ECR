using Ecr.Application.Documents;
using Ecr.Application.Ports;

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
public sealed class ExcelExportJob(IExcelExporter exporter, IExportStore exports) : IExcelExportJob
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

        await progress.ReportAsync(10, "Читання документа", ct).ConfigureAwait(false);

        await using var book = await exporter
            .ExportAsync(task.DocumentId, task.Options, ct)
            .ConfigureAwait(false);

        await progress.ReportAsync(80, "Збереження книги", ct).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        await book.CopyToAsync(buffer, ct).ConfigureAwait(false);

        await exports
            .SaveAsync(task.ExportId, task.DocumentId, buffer.ToArray(), Lifetime, ct)
            .ConfigureAwait(false);

        // ⚠ Ключ повідомляється в прогресі: саме за ним клієнт, побачивши
        // завершення задачі, забирає книгу.
        await progress.ReportAsync(100, task.ExportId, ct).ConfigureAwait(false);
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
