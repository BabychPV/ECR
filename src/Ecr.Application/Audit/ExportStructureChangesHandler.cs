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

/// <summary>
/// Експорт журналу структурних змін у CSV (<c>BE-16</c>). Право — те саме, що в переліку.
/// </summary>
/// <remarks>
/// ⚠ Сторінки читаються через <see cref="GetStructureChangesHandler"/>, а не
/// напряму з <see cref="IAuditReader"/>: право, вікно й фільтри не можуть
/// розійтися з переліком, бо це той самий код.
/// </remarks>
public sealed class ExportStructureChangesHandler(
    GetStructureChangesHandler pages,
    IAuditReader audit,
    IAuditWriter writer,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Стеля рядків, коли конфіг не задає іншої.</summary>
    public const int DefaultMaxRows = 100_000;

    /// <summary>Тип події журналу безпеки.</summary>
    public const string ExportedEventType = "AuditStructureExported";

    /// <summary>Заголовок CSV.</summary>
    public static readonly string[] Columns =
        ["changedAtUtc", "entityType", "entityId", "operation", "changeReason", "changedByUserId", "oldJson", "newJson"];

    /// <summary>
    /// Перевіряє право, вікно й стелю, пише подію експорту — і лише тоді віддає рядки.
    /// </summary>
    /// <param name="filter">Ті самі вікно й фільтри, що в переліку.</param>
    /// <param name="maxRows">Жорстка стеля рядків.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REQ-0422</c> — рядків більше за стелю.</exception>
    public async Task<StructureChangeExport> PrepareAsync(
        StructureChangeFilter filter, int maxRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Право — явно й першим (перелік перевіряє його ще раз на кожній сторінці).
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, GetCellChangesHandler.Permission, ct)
            .ConfigureAwait(false);

        // Перша сторінка перевіряє й вікно: до неї не записано жодного байта.
        var first = await pages
            .HandleAsync(filter, new CursorRequest(CursorRequest.MaxLimit), ct)
            .ConfigureAwait(false);

        var total = first.NextCursor is null
            ? first.Items.Count
            : await audit.CountStructureJournalAsync(filter, ct).ConfigureAwait(false);

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

        // Подія — ДО потоку: обірване завантаження однаково означає, що дані пішли.
        await writer.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                ExportedEventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(new
                {
                    from = filter.From,
                    to = filter.To,
                    entityType = filter.EntityType,
                    changedByUserId = filter.ChangedByUserId,
                    rows = total,
                }),
                currentUser.UserId ?? 0,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return new StructureChangeExport(
            $"audit-structure-{filter.From:yyyyMMdd'T'HHmmss'Z'}-{filter.To:yyyyMMdd'T'HHmmss'Z'}.csv",
            total,
            RowsAsync(first, filter, maxRows, ct));
    }

    /// <summary>Рядок CSV для одного запису; час — UTC ISO 8601.</summary>
    /// <param name="row">Запис журналу.</param>
    public static string ToCsv(StructureChangeView row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return CsvFormat.Row(
            DateTime.SpecifyKind(row.ChangedAt, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            row.EntityType,
            row.EntityId.ToString(CultureInfo.InvariantCulture),
            row.Operation,
            row.ChangeReason,
            row.ChangedByUserId.ToString(CultureInfo.InvariantCulture),
            row.OldJson,
            row.NewJson);
    }

    // Посторінково: у пам'яті щомиті не більше однієї сторінки (500 рядків).
    private async IAsyncEnumerable<StructureChangeView> RowsAsync(
        PagedResult<StructureChangeView> page, StructureChangeFilter filter, int maxRows,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var emitted = 0;
        while (true)
        {
            foreach (var row in page.Items)
            {
                // Рядки, дописані після підрахунку, не прорвуть стелю.
                if (emitted++ >= maxRows)
                {
                    yield break;
                }

                yield return row;
            }

            if (page.NextCursor is null)
            {
                yield break;
            }

            page = await pages
                .HandleAsync(filter, new CursorRequest(CursorRequest.MaxLimit, page.NextCursor), ct)
                .ConfigureAwait(false);
        }
    }
}

/// <summary>Підготовлений експорт: ім'я файлу, кількість і лінивий потік рядків.</summary>
/// <param name="FileName">Ім'я файлу для <c>Content-Disposition</c>.</param>
/// <param name="Total">Рядків на момент підрахунку.</param>
/// <param name="Rows">Рядки; читаються сторінками під час відправки.</param>
public sealed record StructureChangeExport(
    string FileName, int Total, IAsyncEnumerable<StructureChangeView> Rows);
