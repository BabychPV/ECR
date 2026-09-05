using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Microsoft.Extensions.Caching.Distributed;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Побудова книги <c>.xlsx</c> у фоні.
/// </summary>
/// <remarks>
/// Бюджет експорту — 10 с p95 (tz/08 §8.2), і це середнє: книга на 500×60×12
/// будується довше за будь-який розумний HTTP-таймаут.
///
/// ⚠ <b>Готовий файл кладеться в розподілений кеш під власним ключем</b>, а
/// не віддається у відповідь. Ендпоінта, який його забирає, у контракті
/// <b>немає</b> (проблема P-14): таблиця ендпоінтів `02-contracts.md` §9
/// оголошує лише <c>POST …/export</c>, що повертає <c>202</c> з
/// <c>jobId</c>. Тому файл зберігається, ключ повідомляється в прогресі, і
/// щойн ендпоінт зʼявиться — його реалізація буде читанням цього ключа.
/// Тримати файл у памʼяті задачі або писати в тимчасову теку інстансу було б
/// гірше: у першому випадку він зникає разом із задачею, у другому — його не
/// бачить інстанс, на який потрапить наступний запит.
/// </remarks>
public sealed class ExcelExportJob(IExcelExporter exporter, IDistributedCache cache) : IExcelExportJob
{
    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "excel-export";

    /// <summary>Префікс ключа, під яким лежить готова книга.</summary>
    public const string KeyPrefix = "ecr:export:";

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

        await cache
            .SetAsync(
                KeyPrefix + task.ExportId,
                buffer.ToArray(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime },
                ct)
            .ConfigureAwait(false);

        // ⚠ Ключ повідомляється в прогресі: це єдиний спосіб, у який клієнт
        // сьогодні дізнається, що саме побудовано.
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
