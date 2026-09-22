// src/Ecr.Application/Reporting/ExportSnapshotHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Reporting;

/// <summary>
/// Вивантаження зрізу в <c>.xlsx</c> (<c>R7</c>). Право <c>Report.Export</c>
/// плюс грант <c>Read</c> на проєкт зрізу — як у рядків і перевірки.
/// </summary>
/// <remarks>
/// ⛔ Право <c>Report.Export</c> лежало в каталозі (<c>09-seed.sql</c>) і в
/// ролях <c>DataEntry</c>/<c>Viewer</c> від самого початку — і не відкривало
/// НІЧОГО: жоден обробник його не питав. Тобто право видавали, а дії за ним не
/// існувало. Тут воно нарешті щось означає.
///
/// ⚠ Право окреме від <c>Report.ViewRegulatory</c> навмисно, хоч вміст той
/// самий, що й у <c>GET …/rows</c>. Різниця не в обсязі даних, а в тому, що
/// книга ВИХОДИТЬ ІЗ СИСТЕМИ: рядки на екрані лишаються за периметром аудиту
/// й прав, файл — ні. Саме для цього розділення право й заводили.
///
/// ⛔ <b>D-52a лишається в силі:</b> це зріз, а не державна форма. PDF
/// держформи як був, так і лишається в SSRS (<c>D-52</c>) — тут плаский аркуш
/// із колонками опису, без шапки форми, підписів і нумерації граф.
/// </remarks>
public sealed class ExportSnapshotHandler(
    IReportSnapshotBuilder snapshots,
    ISnapshotWorkbookWriter workbooks,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на вивантаження звіту (`02-contracts.md` §9).</summary>
    public const string Permission = "Report.Export";

    /// <summary>
    /// Стеля рядків книги.
    /// </summary>
    /// <remarks>
    /// ⚠ Це НЕ межа Excel (1 048 576) і не межа бази. Це межа ПАМ'ЯТІ: книга
    /// будується об'єктною моделлю ClosedXML цілком у пам'яті, і мільйон рядків
    /// поклав би процес заради одного запиту. Зріз такого розміру вивантажують
    /// не книгою, а вʼюхою <c>rpt.v_*</c>, задля якої <c>rpt.*</c> і існує.
    ///
    /// ⛔ Відмова, а не мовчазне обрізання: книга, у якій «майже всі» рядки,
    /// виглядає як повна і ніде про це не каже.
    /// </remarks>
    public const int MaxRows = 50_000;

    /// <summary>Будує книгу зрізу.</summary>
    /// <param name="snapshotId">Зріз.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Зрізу немає або він у невидимому проєкті.</exception>
    /// <exception cref="BusinessRuleException">Рядків більше за <see cref="MaxRows"/>.</exception>
    public async Task<SnapshotExport> HandleAsync(long snapshotId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var projectId = await snapshots.FindProjectIdAsync(snapshotId, ct).ConfigureAwait(false);

        // ⛔ Чужий = неіснуючий, той самий 404, що й у рядків (BE-17, Q-239):
        // перелік чужих зрізів не показує взагалі, і відмова «є, але не твій»
        // розповідала б перебором ідентифікаторів те, що перелік приховує.
        if (projectId is not { } project
            || profile.LevelFor(ResourceKind.Project, project) < GrantLevel.Read)
        {
            throw NotFound(snapshotId);
        }

        var first = await Page(snapshotId, 0, ct).ConfigureAwait(false);
        var rows = new List<SnapshotRow>(first.Rows);
        var cursor = first.NextCursor;

        // ⚠ Сторінками тим самим методом порту, що й екран: зріз на 200 000
        // рядків інакше приїхав би одним запитом ще до того, як стеля нижче
        // встигне його відхилити.
        while (cursor is { } after && rows.Count <= MaxRows)
        {
            var next = await Page(snapshotId, after, ct).ConfigureAwait(false);
            rows.AddRange(next.Rows);
            cursor = next.NextCursor;
        }

        if (rows.Count > MaxRows)
        {
            throw new BusinessRuleException(
                ErrorCodes.ReportInvalid,
                $"У зрізі {snapshotId} понад {MaxRows} рядків: книга такого розміру не будується.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-RPT-0422.exportTooLarge",
                    ["snapshotId"] = snapshotId.ToString(CultureInfo.InvariantCulture),
                    ["limit"] = MaxRows.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⚠ Групи й підсумки беруться з ПЕРШОЇ сторінки: макет (`R8`) рахується
        // по всьому зрізу, тож на кожній сторінці він однаковий, а книга має
        // показати рівно те, що показує екран.
        var content = await workbooks
            .WriteAsync(
                new SnapshotWorkbook(
                    snapshotId, first.Columns, rows, first.Groups, first.Totals, first.ShowGroupHeader),
                ct)
            .ConfigureAwait(false);

        return new SnapshotExport(
            string.Create(CultureInfo.InvariantCulture, $"snapshot-{snapshotId}.xlsx"), content);
    }

    /// <summary>Сторінка рядків; зріз, що зник між викликами, — той самий 404.</summary>
    /// <remarks>
    /// ⚠ Мова та сама, що й у <c>GET …/rows</c> (<c>R9</c>): книга й екран
    /// мусять підписувати колонки однаково, інакше «звірити у файлі» перестає
    /// бути звіркою.
    /// </remarks>
    private async Task<SnapshotRowsPage> Page(long snapshotId, int afterRowNo, CancellationToken ct)
        => await snapshots
               .RowsAsync(
                   snapshotId, afterRowNo, GetSnapshotRowsHandler.MaxLimit, currentUser.Language, ct)
               .ConfigureAwait(false)
           ?? throw NotFound(snapshotId);

    private static NotFoundException NotFound(long snapshotId)
        => new(
            ErrorCodes.ReportNotFound,
            $"Зрізу {snapshotId} немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-RPT-0404.snapshot",
                ["snapshotId"] = snapshotId.ToString(CultureInfo.InvariantCulture),
            });
}

/// <summary>Готова книга зрізу.</summary>
/// <param name="FileName">Ім'я файлу для заголовка <c>Content-Disposition</c>.</param>
/// <param name="Content">Потік книги; закриває його той, хто віддає відповідь.</param>
public sealed record SnapshotExport(string FileName, Stream Content)
{
    /// <summary>MIME книги <c>.xlsx</c> — той самий, що й у вивантаженні документа.</summary>
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
}
