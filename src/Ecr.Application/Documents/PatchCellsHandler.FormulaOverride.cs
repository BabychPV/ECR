using Ecr.Application.Documents.Dto;
using Ecr.Application.Documents.FormulaOverride;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// ✎ D-461: «ручне значення замість формули» на шляху запису — і поштучному (<c>PATCH …/cells</c>), і книжковому
/// (імпорт Excel). Рішення людини 30.10.
/// </summary>
/// <remarks>
/// ⚠ Що вирішує ДОСТУП (<c>EditRules.CanEdit</c>, <see cref="EditDecision.ViaFormulaOverride"/>): комірку формули можна
/// писати лише з правом <c>Document.OverrideFormula</c> і коли формула дозволяє (або комірка вже ручна); решта правил
/// (період, стан аркуша, гранти, Deny, архів, симуляція) — без змін. Що вирішує ЦЕЙ файл:
/// <list type="bullet">
/// <item>нове ручне значення — лише коли формула ЗАРАЗ дозволяє (вимкнений прапорець лишає наявні значення, але нових
/// не пускає) і з причиною (обрізана, ≤ <see cref="CellFormulaOverride.MaxReasonLength"/>, без керівних символів,
/// окрім переносу рядка) — інакше <c>422 ECR-CELL-0422</c> (<c>overrideReasonRequired</c>);</item>
/// <item>видалення значення (чи «явна порожнеча») в ручній комірці — ПОВЕРНЕННЯ формули: значення й позначка
/// знімаються, перерахунок рахує комірку знову. Окремого стану «ручна порожнеча» немає. Видалення в комірці формули
/// без ручного значення — нічого (обчислене не стирається);</item>
/// <item>позначки <c>doc.CellFormulaOverride</c> пишуться в ТІЙ САМІЙ транзакції, що значення й журнал
/// (<c>Origin = FormulaOverride</c> + <c>Reason</c> / <c>FormulaRestored</c>).</item>
/// </list>
/// ⛔ Причина — лише в журналі й позначці; у тексти відмов вона не потрапляє (нерозкриття, <c>DenyLeakGuardTests</c>).
/// </remarks>
public sealed partial class PatchCellsHandler
{
    /// <summary>Причина, з якою імпорт Excel ставить ручне значення замість формули (D-461, без запиту людини).</summary>
    public const string ExcelImportOverrideReason = "Excel import";

    /// <summary>План ручних значень батчу (ключ — <c>(RowKey, ColumnDefId)</c>, стабільний і для нових рядків).</summary>
    private sealed class FormulaOverridePlan
    {
        /// <summary>Нові/змінені ручні значення → причина.</summary>
        public Dictionary<(string RowKey, int ColumnDefId), string> Reasons { get; } = [];

        /// <summary>Комірки, що повертаються до формули (позначка є, значення знімається).</summary>
        public HashSet<(string RowKey, int ColumnDefId)> Restores { get; } = [];

        public bool IsEmpty => Reasons.Count == 0 && Restores.Count == 0;
    }

    /// <summary>Позначає комірку батчу, писати яку дозволено саме як ручне значення замість формули.</summary>
    private static void NoteFormulaOverride(RequestContext context, EditDecision decision, string rowKey, int columnDefId)
    {
        if (decision.IsAllowed && decision.ViaFormulaOverride)
        {
            context.OverrideCells.Add((rowKey, columnDefId));
        }
    }

    /// <summary>
    /// Розкладає комірки формул батчу на «ручне значення» і «повернути формулу»; повертає зміни без no-op видалень.
    /// </summary>
    /// <remarks>
    /// ⚠ Один запит позначок на батч і лише коли в батчі Є видалення в комірках формул (решта — нуль звернень).
    /// </remarks>
    private async Task<CellChangeLists> PlanFormulaOverridesAsync(
        PatchCellsRequest request, RequestContext context, CellChangeLists planned, CancellationToken ct)
    {
        context.OverridePlan = null;
        if (context.OverrideCells.Count == 0)
        {
            return planned;
        }

        var policy = FormulaOverridePolicy.For(context.Table);
        var plan = new FormulaOverridePlan();
        var reasons = RequestedReasons(request, context);

        var routedTable = monthRouter is { Enabled: true } && monthRouter.AppliesToTable(context.Table);

        var upserts = new List<CellRecord>(planned.Upserts.Count);
        var deletes = new List<CellAddress>(planned.Deletes.Count);
        var restoreCandidates = new List<(CellAddress Address, (string, int) Key)>();

        foreach (var upsert in planned.Upserts)
        {
            var key = KeyOf(planned, upsert.Address);
            if (key is not { } cell || !context.OverrideCells.Contains(cell))
            {
                upserts.Add(upsert);
                continue;
            }

            var column = context.Snapshot.ColumnsById[upsert.Address.ColumnDefId];
            RefuseRoutedInput(routedTable, column);

            if (upsert.Value.IsEmpty)
            {
                // Окремого стану «ручна порожнеча» немає (рішення людини): порожнеча = повернути формулу.
                restoreCandidates.Add((upsert.Address, cell));
                continue;
            }

            // ⛔ Вимкнений прапорець: наявне ручне значення лишається (його можна повернути), нового — не можна.
            if (!policy.Allows(cell.RowKey, column))
            {
                throw CalculatedCellRefusal();
            }

            plan.Reasons[cell] = ValidReason(reasons.GetValueOrDefault(cell), cell.RowKey, column.Code);
            upserts.Add(upsert with { Value = upsert.Value with { IsCalculated = false } });
        }

        foreach (var delete in planned.Deletes)
        {
            var key = KeyOf(planned, delete);
            if (key is { } cell && context.OverrideCells.Contains(cell))
            {
                RefuseRoutedInput(routedTable, context.Snapshot.ColumnsById[delete.ColumnDefId]);
                restoreCandidates.Add((delete, cell));
                continue;
            }

            deletes.Add(delete);
        }

        if (restoreCandidates.Count > 0)
        {
            // Нові рядки (тимчасові адреси) позначок не мають — питати нема про що.
            var existing = formulaOverrides is null
                ? new Dictionary<CellAddress, CellFormulaOverride>()
                : await formulaOverrides
                    .ReadAsync([.. restoreCandidates.Select(c => c.Address).Where(a => !IsPlaceholder(a.TableRowId))], ct)
                    .ConfigureAwait(false);

            foreach (var (address, cell) in restoreCandidates)
            {
                if (existing.ContainsKey(address))
                {
                    plan.Restores.Add(cell);
                    deletes.Add(address);
                }

                // Інакше — no-op: обчислене значення не стирається.
            }
        }

        context.OverridePlan = plan.IsEmpty ? null : plan;
        return planned with { Upserts = upserts, Deletes = deletes };
    }

    /// <summary>Причини з тіла запиту: <c>(RowKey, ColumnDefId)</c> → сира причина.</summary>
    private static Dictionary<(string RowKey, int ColumnDefId), string?> RequestedReasons(
        PatchCellsRequest request, RequestContext context)
    {
        var result = new Dictionary<(string, int), string?>();
        foreach (var row in request.Rows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.OverrideReason is not null && context.Columns.TryGetValue(cell.ColumnCode, out var columnId))
                {
                    result[(row.RowKey, columnId)] = cell.OverrideReason;
                }
            }
        }

        return result;
    }

    private static (string RowKey, int ColumnDefId)? KeyOf(CellChangeLists changes, CellAddress address)
        => changes.RowKeyById.TryGetValue(address.TableRowId, out var rowKey) ? (rowKey, address.ColumnDefId) : null;

    /// <summary>
    /// Q-17: комірочна формула в колонці вводу з іншим власником (місяць/якір) — ручне значення не підтримується:
    /// значення жило б в екземплярі власника, а позначка — в екземплярі перегляду.
    /// </summary>
    private static void RefuseRoutedInput(bool routedTable, ColumnDef column)
    {
        if (routedTable && !column.IsComputed)
        {
            throw CalculatedCellRefusal();
        }
    }

    /// <summary>Та сама відмова, що й на комірку формули без права (<c>deniedCells</c>, <c>CalculatedCell</c>).</summary>
    private static AccessDeniedException CalculatedCellRefusal()
        => new(
            "ECR-ACCS-0403",
            $"Заборонених комірок у батчі: 1. Причина першої: {EditDenyReason.CalculatedCell}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.deniedCells",
                ["deniedCount"] = "1",
                ["reason"] = EditDenyReason.CalculatedCell.ToString(),
            });

    /// <summary>
    /// Причина ручного значення: обрізана, непорожня, ≤ <see cref="CellFormulaOverride.MaxReasonLength"/>, без
    /// керівних символів, крім переносу рядка (<c>\r\n</c> зводиться до <c>\n</c>).
    /// </summary>
    /// <exception cref="BusinessRuleException"><c>422 ECR-CELL-0422</c> (<c>overrideReasonRequired</c>).</exception>
    internal static string ValidReason(string? raw, string rowKey, string columnCode)
    {
        var reason = raw?.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() ?? string.Empty;

        if (reason.Length == 0
            || reason.Length > CellFormulaOverride.MaxReasonLength
            || reason.Any(c => char.IsControl(c) && c != '\n'))
        {
            // ⛔ Саму причину в текст відмови не повертаємо — лише межу й адресу.
            throw new BusinessRuleException(
                "ECR-CELL-0422",
                $"Ручне значення замість формули потребує причини до {CellFormulaOverride.MaxReasonLength} символів.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-CELL-0422.overrideReasonRequired",
                    ["max"] = CellFormulaOverride.MaxReasonLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["columnCode"] = columnCode,
                    ["rowKey"] = rowKey,
                });
        }

        return reason;
    }

    /// <summary>Чи комірка — нове ручне значення цього батчу (для структурної перевірки значення).</summary>
    private static bool IsManualOverride(RequestContext context, CellChangeLists changes, CellAddress address)
        => context.OverridePlan is { } plan
           && KeyOf(changes, address) is { } key
           && plan.Reasons.ContainsKey(key);

    /// <summary>
    /// Ставить і знімає позначки <c>doc.CellFormulaOverride</c> у поточній транзакції — за СПРАВЖНІМИ адресами
    /// (після створення нових рядків).
    /// </summary>
    private async Task ApplyFormulaOverrideMarksAsync(
        RequestContext context, CellChangeLists applied, DateTime now, CancellationToken ct)
    {
        if (context.OverridePlan is not { } plan || formulaOverrides is null)
        {
            return;
        }

        var marks = new List<CellFormulaOverride>();
        foreach (var upsert in applied.Upserts)
        {
            if (KeyOf(applied, upsert.Address) is { } key && plan.Reasons.TryGetValue(key, out var reason))
            {
                marks.Add(new CellFormulaOverride(
                    upsert.Address, context.Instance.TableInstanceId, reason, context.UserId, now));
            }
        }

        var removals = applied.Deletes
            .Where(d => KeyOf(applied, d) is { } key && plan.Restores.Contains(key))
            .ToList();

        await formulaOverrides.ApplyAsync(marks, removals, ct).ConfigureAwait(false);
    }

    /// <summary>Причини ручних значень за адресами застосованих змін — для журналу.</summary>
    private static (Dictionary<CellAddress, string>? Reasons, HashSet<CellAddress>? Restored) AuditOverrides(
        RequestContext context, CellChangeLists applied)
    {
        if (context.OverridePlan is not { } plan)
        {
            return (null, null);
        }

        var reasons = new Dictionary<CellAddress, string>();
        foreach (var upsert in applied.Upserts)
        {
            if (KeyOf(applied, upsert.Address) is { } key && plan.Reasons.TryGetValue(key, out var reason))
            {
                reasons[upsert.Address] = reason;
            }
        }

        var restored = applied.Deletes
            .Where(d => KeyOf(applied, d) is { } key && plan.Restores.Contains(key))
            .ToHashSet();

        return (reasons, restored);
    }
}
