using Ecr.Application.Documents;
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Застосування ВЕЛИКОГО імпорту з <c>.xlsx</c> у фоні (директива №11, T10
/// #45).
/// </summary>
/// <remarks>
/// ⚠ Той самий шлях запису, що й синхронний — <see cref="IExcelImporter.ApplyAsync"/>,
/// а не окрема копія логіки. Задача лише переносить виклик у чергу; сам запис
/// (і його правила — конфлікти версій, валідація, перерахунок) лишається
/// одним місцем коду, як і в <see cref="ApplyImportHandler"/>.
/// </remarks>
public sealed class ExcelImportJob(IExcelImporter importer) : IExcelImportJob
{
    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "excel-import-apply";

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var task = ImportPayload.Parse(payload);

        await progress.ReportAsync(10, "Застосування diff", ct).ConfigureAwait(false);

        var result = await importer.ApplyAsync(task.DocumentId, task.PreviewToken, ct).ConfigureAwait(false);

        // ⚠ Клієнт не забирає окремий файл (на відміну від експорту) — сам
        // результат застосування невеликий, і повідомлення прогресу досить,
        // щоб показати підсумок без другого запиту.
        await progress
            .ReportAsync(
                100, $"Застосовано комірок: {result.AppliedCells}; повідомлень валідації: {result.Validation.Count}",
                ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Розбір завдання застосування імпорту.</summary>
internal static class ImportPayload
{
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Розбирає завдання черги.</summary>
    public static ExcelImportTask Parse(object? payload)
    {
        if (payload is ExcelImportTask typed)
        {
            return typed;
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);

        return System.Text.Json.JsonSerializer.Deserialize<ExcelImportTask>(json, Options)
               ?? throw new InvalidOperationException(
                   "Завдання застосування імпорту не розбирається: невідома форма payload.");
    }
}
