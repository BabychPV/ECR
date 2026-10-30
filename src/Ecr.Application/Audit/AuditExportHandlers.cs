using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Audit;

/// <summary>Підготовлений експорт журналу: ім'я файлу, кількість і лінивий потік рядків.</summary>
/// <typeparam name="T">Тип рядка журналу.</typeparam>
/// <param name="FileName">Ім'я файлу для <c>Content-Disposition</c>.</param>
/// <param name="Total">Рядків на момент підрахунку.</param>
/// <param name="Rows">Рядки; читаються сторінками (≤ 500 у пам'яті) під час відправки.</param>
public sealed record AuditExport<T>(string FileName, int Total, IAsyncEnumerable<T> Rows);

/// <summary>
/// Спільний хід експорту CSV журналу аудиту: право/вікно (через обробник сторінок) → стеля → подія безпеки → потік.
/// </summary>
/// <remarks>
/// ⛔ Стеля (<c>Audit:ExportMaxRows</c>, типово <see cref="DefaultMaxRows"/>) перевіряється ДО першого байта;
/// понад неї — <c>422 err.ECR-REQ-0422.auditExportTooLarge</c> («звузьте вікно або фільтри»), файлу немає.
/// Фонової задачі для експорту аудиту в системі немає: вікно журналу обмежене 92 добами, тож синхронний потік
/// сторінками досить; для більшого обсягу — вужчі вікна.
/// ⛔ Подія експорту пишеться ДО потоку (обірване завантаження однаково означає, що дані пішли) і НЕ несе даних
/// журналу: лише вкладка, вікно, фільтри, порядок і кількість рядків.
/// </remarks>
public static class AuditExportRunner
{
    /// <summary>Стеля рядків, коли конфіг не задає іншої.</summary>
    public const int DefaultMaxRows = 100_000;

    /// <summary>Час у CSV: UTC, ISO 8601 з міліcекундами (однозначний у будь-якій локалі Excel).</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Рядок часу UTC для CSV.</summary>
    /// <param name="moment">Момент.</param>
    public static string Stamp(DateTime moment)
        => DateTime.SpecifyKind(moment, DateTimeKind.Utc).ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>Ім'я файлу експорту: <c>audit-&lt;вкладка&gt;-&lt;від&gt;-&lt;до&gt;.csv</c>.</summary>
    /// <param name="tab">Вкладка.</param>
    /// <param name="from">Початок вікна.</param>
    /// <param name="to">Кінець вікна.</param>
    public static string FileNameOf(string tab, DateTime from, DateTime to)
        => $"audit-{tab}-{from:yyyyMMdd'T'HHmmss'Z'}-{to:yyyyMMdd'T'HHmmss'Z'}.csv";

    /// <summary>Підготовка експорту.</summary>
    /// <param name="fetch">Читає сторінку за курсором (<c>null</c> — перша); несе право й перевірки вікна.</param>
    /// <param name="totalOf">Кількість рядків за фільтром (із першої сторінки або окремим підрахунком).</param>
    /// <param name="maxRows">Жорстка стеля рядків.</param>
    /// <param name="eventType">Тип події безпеки.</param>
    /// <param name="details">Деталі події з кількістю рядків (без даних журналу).</param>
    /// <param name="fileName">Ім'я файлу.</param>
    /// <param name="writer">Запис події безпеки.</param>
    /// <param name="clock">Годинник.</param>
    /// <param name="currentUser">Поточний користувач.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <typeparam name="T">Тип рядка журналу.</typeparam>
    public static async Task<AuditExport<T>> PrepareAsync<T>(
        Func<string?, Task<PagedResult<T>>> fetch,
        Func<PagedResult<T>, Task<int>> totalOf,
        int maxRows,
        string eventType,
        Func<int, object> details,
        string fileName,
        IAuditWriter writer,
        IClock clock,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(totalOf);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(currentUser);

        // Перша сторінка перевіряє право, вікно й межі читання: до неї не записано жодного байта.
        var first = await fetch(null).ConfigureAwait(false);
        var total = first.NextCursor is null ? first.Items.Count : await totalOf(first).ConfigureAwait(false);

        if (total > maxRows)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Експорт містив би {total} рядків, стеля — {maxRows}: звузьте вікно або фільтри.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.auditExportTooLarge",
                    ["total"] = total.ToString(CultureInfo.InvariantCulture),
                    ["max"] = maxRows.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⛔ C4: це подія-СПРОБА (доступ до журналу), незалежна від долі будь-якої транзакції навколо.
        await writer.WriteIndependentSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                eventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(details(total)),
                currentUser.UserId ?? 0,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return new AuditExport<T>(fileName, total, StreamAsync(first, fetch, maxRows, ct));
    }

    // Посторінково: у пам'яті щомиті не більше однієї сторінки (500 рядків); рядки, дописані після підрахунку,
    // не прорвуть стелю.
    private static async IAsyncEnumerable<T> StreamAsync<T>(
        PagedResult<T> page, Func<string?, Task<PagedResult<T>>> fetch, int maxRows,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var emitted = 0;
        while (true)
        {
            foreach (var item in page.Items)
            {
                if (emitted++ >= maxRows)
                {
                    yield break;
                }

                yield return item;
            }

            if (page.NextCursor is null)
            {
                yield break;
            }

            ct.ThrowIfCancellationRequested();
            page = await fetch(page.NextCursor).ConfigureAwait(false);
        }
    }
}

/// <summary>Експорт журналу змін комірок у CSV (гаряче + архів). Право й межі читання — ті самі, що в переліку.</summary>
public sealed class ExportCellChangesHandler(
    GetCellChangesHandler pages, IAuditWriter writer, IClock clock, ICurrentUser currentUser)
{
    /// <summary>Тип події журналу безпеки.</summary>
    public const string ExportedEventType = "AuditCellsExported";

    /// <summary>Заголовок CSV.</summary>
    public static readonly string[] Columns =
    [
        "changedAtUtc", "documentId", "documentBusinessKey", "periodKey", "rowKey", "columnDefId", "columnCode",
        "oldValue", "newValue", "changedByUserId", "changedBy", "origin", "isLateEdit", "isOutOfWindow", "isOverwrite",
        "fromArchive",
    ];

    /// <summary>Перевіряє право, вікно й стелю, пише подію експорту — і лише тоді віддає рядки.</summary>
    /// <param name="filter">Ті самі вікно, фільтри й порядок, що в переліку.</param>
    /// <param name="maxRows">Жорстка стеля рядків.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<AuditExport<CellChangeView>> PrepareAsync(CellChangeFilter filter, int maxRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // TotalCount першої сторінки — те, що читач ВИДИТЬ (межі читання S6 застосовано), а не сума вікна.
        return AuditExportRunner.PrepareAsync(
            cursor => pages.HandleAsync(filter, new CursorRequest(CursorRequest.MaxLimit, cursor), ct),
            first => Task.FromResult(first.TotalCount ?? first.Items.Count),
            maxRows,
            ExportedEventType,
            total => new
            {
                tab = "cells",
                from = filter.From,
                to = filter.To,
                documentId = filter.DocumentId,
                rowKey = filter.RowKey,
                columnDefId = filter.ColumnDefId,
                author = filter.ChangedByUserId,
                origin = filter.Origin,
                lateOnly = filter.LateOnly,
                periodKey = filter.PeriodKey,
                hasQuery = filter.Query is not null,
                sort = filter.Sort.ToString(),
                rows = total,
            },
            AuditExportRunner.FileNameOf("cells", filter.From, filter.To),
            writer, clock, currentUser, ct);
    }

    /// <summary>Рядок CSV для однієї зміни.</summary>
    /// <param name="row">Запис журналу.</param>
    public static string ToCsv(CellChangeView row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return CsvFormat.Row(
            AuditExportRunner.Stamp(row.ChangedAt),
            row.DocumentId.ToString(CultureInfo.InvariantCulture),
            row.DocumentBusinessKey,
            row.PeriodKey.ToString(CultureInfo.InvariantCulture),
            row.RowKey,
            row.ColumnDefId.ToString(CultureInfo.InvariantCulture),
            row.ColumnCode,
            row.OldValue,
            row.NewValue,
            row.ChangedByUserId.ToString(CultureInfo.InvariantCulture),
            row.ChangedByDisplayName,
            row.Origin,
            Flag(row.IsLateEdit),
            Flag(row.IsOutOfWindow),
            Flag(row.IsOverwrite),
            Flag(row.FromArchive));
    }

    private static string Flag(bool value) => value ? "true" : "false";
}

/// <summary>Експорт журналу подій безпеки у CSV.</summary>
public sealed class ExportSecurityEventsHandler(
    GetSecurityEventsHandler pages, IAuditWriter writer, IAccessDecisionService access, IClock clock, ICurrentUser currentUser)
{
    /// <summary>Право перегляду журналу аудиту — те саме, що в переліку вкладки.</summary>
    private const string Permission = "Security.ViewAudit";

    /// <summary>Тип події журналу безпеки.</summary>
    public const string ExportedEventType = "AuditSecurityExported";

    /// <summary>Заголовок CSV.</summary>
    public static readonly string[] Columns =
        ["changedAtUtc", "eventType", "targetUserId", "targetRoleId", "changedByUserId", "changedBy", "correlationId", "detailsJson"];

    /// <summary>Перевіряє право, вікно й стелю, пише подію експорту — і лише тоді віддає рядки.</summary>
    /// <param name="filter">Ті самі вікно, фільтри й порядок, що в переліку.</param>
    /// <param name="maxRows">Жорстка стеля рядків.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<AuditExport<SecurityEventView>> PrepareAsync(SecurityEventFilter filter, int maxRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Право — явно й першим (перелік перевіряє його ще раз на кожній сторінці), як в експорті структури.
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        return await AuditExportRunner.PrepareAsync(
            cursor => pages.HandleAsync(filter, new CursorRequest(CursorRequest.MaxLimit, cursor), ct),
            _ => pages.CountAsync(filter, ct),
            maxRows,
            ExportedEventType,
            total => new
            {
                tab = "security",
                from = filter.From,
                to = filter.To,
                eventType = filter.EventType,
                changedByUserId = filter.ChangedByUserId,
                sort = filter.Sort.ToString(),
                rows = total,
            },
            AuditExportRunner.FileNameOf("security", filter.From, filter.To),
            writer, clock, currentUser, ct).ConfigureAwait(false);
    }

    /// <summary>Рядок CSV для однієї події.</summary>
    /// <param name="row">Запис журналу.</param>
    public static string ToCsv(SecurityEventView row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return CsvFormat.Row(
            AuditExportRunner.Stamp(row.ChangedAt),
            row.EventType,
            row.TargetUserId?.ToString(CultureInfo.InvariantCulture),
            row.TargetRoleId?.ToString(CultureInfo.InvariantCulture),
            row.ChangedByUserId.ToString(CultureInfo.InvariantCulture),
            row.ChangedByDisplayName,
            row.CorrelationId,
            row.DetailsJson);
    }
}

/// <summary>Експорт журналу подій погодження у CSV.</summary>
public sealed class ExportWorkflowEventsHandler(
    GetWorkflowEventsHandler pages, IAuditWriter writer, IAccessDecisionService access, IClock clock, ICurrentUser currentUser)
{
    /// <summary>Право перегляду журналу аудиту — те саме, що в переліку вкладки.</summary>
    private const string Permission = "Security.ViewAudit";

    /// <summary>Тип події журналу безпеки.</summary>
    public const string ExportedEventType = "AuditWorkflowExported";

    /// <summary>Заголовок CSV.</summary>
    public static readonly string[] Columns =
    [
        "changedAtUtc", "action", "fromStatus", "toStatus", "comment", "changedByUserId", "changedBy",
        "documentId", "documentBusinessKey", "sheetCode", "periodKey", "stepOrdinal",
    ];

    /// <summary>Перевіряє право, вікно й стелю, пише подію експорту — і лише тоді віддає рядки.</summary>
    /// <param name="filter">Ті самі вікно, фільтри й порядок, що в переліку.</param>
    /// <param name="maxRows">Жорстка стеля рядків.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<AuditExport<WorkflowEventView>> PrepareAsync(WorkflowEventFilter filter, int maxRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        return await AuditExportRunner.PrepareAsync(
            cursor => pages.HandleAsync(filter, new CursorRequest(CursorRequest.MaxLimit, cursor), ct),
            _ => pages.CountAsync(filter, ct),
            maxRows,
            ExportedEventType,
            total => new
            {
                tab = "workflow",
                from = filter.From,
                to = filter.To,
                action = filter.Action,
                changedByUserId = filter.ChangedByUserId,
                documentId = filter.DocumentId,
                sort = filter.Sort.ToString(),
                rows = total,
            },
            AuditExportRunner.FileNameOf("workflow", filter.From, filter.To),
            writer, clock, currentUser, ct).ConfigureAwait(false);
    }

    /// <summary>Рядок CSV для однієї події.</summary>
    /// <param name="row">Запис журналу.</param>
    public static string ToCsv(WorkflowEventView row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return CsvFormat.Row(
            AuditExportRunner.Stamp(row.ChangedAt),
            row.Action,
            row.FromStatus,
            row.ToStatus,
            row.Comment,
            row.ChangedByUserId?.ToString(CultureInfo.InvariantCulture),
            row.ChangedByDisplayName,
            row.DocumentId.ToString(CultureInfo.InvariantCulture),
            row.DocumentBusinessKey,
            row.SheetCode,
            row.PeriodKey.ToString(CultureInfo.InvariantCulture),
            row.StepOrdinal?.ToString(CultureInfo.InvariantCulture));
    }
}
