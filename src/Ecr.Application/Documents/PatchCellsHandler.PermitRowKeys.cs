using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents;

public sealed partial class PatchCellsHandler
{
    /// <summary>
    /// PA2-S-02: колонки з роллю «ключ рядка» (<see cref="PermitColumnRole.RowKey"/>) рядків, створених прив'язкою до дозволу,
    /// людина не править - так само, як «ліміт» та «інфо» (ті закриває <c>ColumnIsReadOnly</c> шаблону, перегляд прив'язки
    /// показує всі зіставлені колонки як <c>Locked</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Гаряча дорога Land не змінюється: перевірка мовчить, доки в батчі немає рядка з ключем виду <c>P&lt;число&gt;</c>
    /// (<see cref="PermitRowPlanner.RowKeyFor"/>), а тоді один запит <c>AnyActiveRows</c>; повний перелік колонок - лише для
    /// аркушів, де прив'язка є. Відмова - та сама, що в <c>CheckAccess</c>: <c>403 ECR-ACCS-0403 deniedCells</c>, причина
    /// <c>ColumnReadOnly</c>. Рядок, створений людиною з ключем <c>P7</c> у таблиці з прив'язкою, теж не править ключ: ключ
    /// належить прив'язці.
    /// </remarks>
    private async Task EnsurePermitRowKeysUntouchedAsync(RequestContext context, CancellationToken ct)
    {
        if (permitBindings is null)
        {
            return;
        }

        var permitRows = context.Updates.Where(r => PermitRowPlanner.IsPermitRowKey(r.RowKey)).ToList();
        if (permitRows.Count == 0 || !await permitBindings.AnyActiveRowsAsync([context.Table.Id], ct).ConfigureAwait(false))
        {
            return;
        }

        var keyColumns = (await permitBindings.ListActiveRowsAsync([context.Table.Id], ct).ConfigureAwait(false))
            .SelectMany(view => view.Columns)
            .Where(c => c.Role == PermitColumnRole.RowKey)
            .Select(c => c.SheetColumnDefId)
            .ToHashSet();

        var denied = permitRows.Sum(row => row.Cells.Count(cell =>
            context.Columns.TryGetValue(cell.ColumnCode, out var columnId) && keyColumns.Contains(columnId)));
        if (denied == 0)
        {
            return;
        }

        throw new AccessDeniedException(
            "ECR-ACCS-0403",
            $"Заборонених комірок у батчі: {denied}. Причина першої: {EditDenyReason.ColumnReadOnly}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.deniedCells",
                ["deniedCount"] = denied.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reason"] = EditDenyReason.ColumnReadOnly.ToString(),
            });
    }
}
