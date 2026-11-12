// src/Ecr.Application/Validation/FormulaErrorIssues.cs
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Validation;

/// <summary>
/// ✎ C-1 (рішення PO 11.11 п. 5, D-475): комірка з помилкою формули або «не оновлена через помилку» — Error, що
/// блокує подання аркуша, як <c>PERMIT-SPLIT</c> (<see cref="PermitCoverageIssues.SplitRuleCode"/>).
/// </summary>
public static class FormulaErrorIssues
{
    /// <summary>Код правила повідомлення.</summary>
    public const string RuleCode = "FORMULA-ERROR";

    /// <summary>Повідомлення для комірок з помилкою (мовою <paramref name="language"/>).</summary>
    /// <param name="cells">Комірки з помилкою; <c>null</c> — немає.</param>
    /// <param name="tables">Таблиці версії за Id (у т. ч. таблиці джерел).</param>
    /// <param name="language">Мова запасного тексту.</param>
    public static IReadOnlyList<ValidationMessage> Messages(
        IReadOnlyList<FormulaErrorCell>? cells, IReadOnlyDictionary<int, TableDef> tables, string language)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var messages = new List<ValidationMessage>();
        foreach (var cell in cells ?? [])
        {
            if (!tables.TryGetValue(cell.TableDefId, out var table)
                || table.Columns.FirstOrDefault(c => c.Id == cell.ColumnDefId) is not { } column)
            {
                continue;
            }

            var blocked = cell.Kind == FormulaError.BlockedKind;
            var sourceTable = blocked && cell.SourceTableDefId is { } sid && tables.TryGetValue(sid, out var st) ? st : null;
            var sourceColumn = sourceTable?.Columns.FirstOrDefault(c => c.Id == cell.SourceColumnDefId);
            var key = blocked ? ValidationMessageTemplates.FormulaErrorBlocked : ValidationMessageTemplates.FormulaError;
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["column"] = column.HeaderL10n.Get("en") ?? column.Code,
                ["code"] = cell.ErrorCode,
                ["source"] = sourceColumn is null
                    ? cell.SourceRowKey ?? string.Empty
                    : $"{sourceTable!.Code} · {sourceColumn.HeaderL10n.Get("en") ?? sourceColumn.Code} ({cell.SourceRowKey})",
            };
            messages.Add(new ValidationMessage(
                ValidationSeverity.Error,
                RuleCode,
                ValidationMessageTemplates.Render(key, language, parameters),
                table.Id,
                cell.RowKey,
                column.Code,
                BlocksSave: false,

                // S6: повідомлення, джерело якого читачеві закрите, ховає `HiddenValidationIssues.CanSee`.
                SourceTableDefId: blocked ? cell.SourceTableDefId : null,
                SourceColumnCode: blocked ? sourceColumn?.Code : null,
                MessageKey: key,
                Params: parameters));
        }

        return messages;
    }
}
