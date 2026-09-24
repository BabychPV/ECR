using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;

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
/// <para>
/// ⛔ F-01 (UX-PASS, четвертий раунд): задача виконується ВІД ІМЕНІ автора
/// (<see cref="ExcelImportTask.Actor"/>). Доти <c>PatchCellsHandler</c> бачив
/// у фоні анонімного користувача — HTTP-запиту немає, — і кожен імпорт понад
/// поріг падав на 10 % з <c>ECR-AUTH-0401</c>, не записавши нічого. Права
/// перевіряються як для автора (його профіль, його гранти, його групи з
/// токена на момент постановки), і журнал змін підписано ним.
/// </para>
/// </remarks>
public sealed class ExcelImportJob(
    IExcelImporter importer,
    JobActorScope actorScope,
    IAccessDecisionService access,
    ICurrentUser currentUser) : IExcelImportJob
{
    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "excel-import-apply";

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var task = ImportPayload.Parse(payload);

        // ⚠ Завдання без автора (поставлене до F-01) лишається без нього — і
        // відмовляє тією самою `ECR-AUTH-0401`, що й раніше, а не пише від
        // імені системи: «хтось колись поставив» — не автор.
        using var actor = task.Actor is { } author ? actorScope.Enter(author) : null;

        // ⛔ Право — ще раз, у мить виконання, а не лише при постановці: між
        // ними право могли відкликати, і задача не має писати від імені
        // людини, яка вже не має права імпортувати.
        _ = await PermissionCheck
            .RequireAsync(access, currentUser, ApplyImportHandler.Permission, ct)
            .ConfigureAwait(false);

        await progress.ReportKeyAsync(10, "jobs.importApplyingDiff", ct).ConfigureAwait(false);

        var result = await importer.ApplyAsync(task.DocumentId, task.PreviewToken, ct).ConfigureAwait(false);

        // ⚠ Клієнт не забирає окремий файл (на відміну від експорту) — сам
        // результат застосування невеликий, і повідомлення прогресу досить,
        // щоб показати підсумок без другого запиту.
        await progress
            .ReportKeyAsync(
                100,
                "jobs.importApplied",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cells"] = result.AppliedCells.ToString(CultureInfo.InvariantCulture),
                    ["validationCount"] = result.Validation.Count.ToString(CultureInfo.InvariantCulture),
                },
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
