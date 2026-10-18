// src/Ecr.Application/Permits/Bindings/PermitRowPlanner.cs
using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Permits.Bindings;

/// <summary>Зіставлена колонка для планувальника: код колонки аркуша, поле дозволу, роль.</summary>
/// <param name="SheetColumnCode">Код колонки аркуша.</param>
/// <param name="PermitFieldCode">Код поля дозволу.</param>
/// <param name="Role">Роль.</param>
public sealed record PlannerColumn(string SheetColumnCode, string PermitFieldCode, PermitColumnRole Role);

/// <summary>Запланований рядок аркуша.</summary>
/// <param name="RowKey">Ключ рядка аркуша: <c>P&lt;id запису таблиці дозволу&gt;</c>.</param>
/// <param name="EntryId">Запис таблиці дозволу, з якого рядок породжено.</param>
/// <param name="Cells">Значення за кодом колонки аркуша: число, текст, булеве, дата (<see cref="DateOnly"/>) або <c>null</c>.</param>
/// <param name="Locked">Колонки, значення яких пише машина і не править користувач.</param>
public sealed record PlannedRow(
    string RowKey, long EntryId, IReadOnlyDictionary<string, object?> Cells, IReadOnlyList<string> Locked);

/// <summary>План рядків аркуша з дозволу.</summary>
/// <param name="Rows">Рядки в порядку таблиці дозволу <c>(Ordinal, Id)</c>.</param>
/// <param name="Warnings">Попередження (поле не читається тощо).</param>
public sealed record PermitRowPlan(IReadOnlyList<PlannedRow> Rows, IReadOnlyList<PermitBindingIssue> Warnings);

/// <summary>
/// Чиста побудова рядків аркуша з рядків таблиці дозволу над знімком довідників (Permits, PM-09).
/// </summary>
/// <remarks>
/// ⚠ Одна реалізація на перегляд (PM-09) і на рантайм «рядки з дозволу» (PM-10): якби вони розійшлися, попередній
/// перегляд показував би не те, що створить документ. Нічого не пише й не читає БД - лише <see cref="IRegistrySnapshot"/>,
/// видимість якого (чинний, активний, не видалений на дату знімка) вже застосована.
/// </remarks>
public static class PermitRowPlanner
{
    private const string RowKeyPrefix = "P";

    /// <summary>Ключ рядка аркуша для запису таблиці дозволу.</summary>
    /// <param name="entryId">Запис таблиці дозволу.</param>
    public static string RowKeyFor(long entryId)
        => RowKeyPrefix + entryId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Планує рядки.</summary>
    /// <param name="snapshot">Знімок довідників на кінець періоду.</param>
    /// <param name="tableRegistryCode">Код реєстру таблиці дозволу (<c>PRM_&lt;тип&gt;_&lt;таблиця&gt;</c>).</param>
    /// <param name="permitEntryId">Запис-шапка дозволу.</param>
    /// <param name="columns">Зіставлені колонки.</param>
    public static PermitRowPlan Plan(
        IRegistrySnapshot snapshot, string tableRegistryCode, long permitEntryId, IReadOnlyList<PlannerColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableRegistryCode);
        ArgumentNullException.ThrowIfNull(columns);

        var warnings = new List<PermitBindingIssue>();
        var ids = snapshot.FindReferencing(tableRegistryCode, PermitHeaderFields.PermitFieldCode, permitEntryId);

        if (ids is null)
        {
            warnings.Add(new PermitBindingIssue(
                "binding.warning.tableUnavailable",
                new Dictionary<string, string> { ["registryCode"] = tableRegistryCode },
                $"Таблиця дозволу «{tableRegistryCode}» недоступна у знімку: рядків немає."));
            return new PermitRowPlan([], warnings);
        }

        var locked = columns.Select(c => c.SheetColumnCode).ToList();
        var rows = new List<PlannedRow>(ids.Count);

        foreach (var id in ids)
        {
            var cells = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                var value = snapshot.GetField(id, column.PermitFieldCode);
                if (value.IsError)
                {
                    warnings.Add(new PermitBindingIssue(
                        "binding.warning.fieldUnreadable",
                        new Dictionary<string, string>
                        {
                            ["fieldCode"] = column.PermitFieldCode,
                            ["entryId"] = id.ToString(CultureInfo.InvariantCulture),
                        },
                        $"Поле «{column.PermitFieldCode}» запису {id} не читається ({value.ErrorCode})."));
                }

                cells[column.SheetColumnCode] = ToCell(value);
            }

            rows.Add(new PlannedRow(RowKeyFor(id), id, cells, locked));
        }

        return new PermitRowPlan(rows, warnings);
    }

    private static object? ToCell(ExpressionValue value)
    {
        if (value.IsNull || value.IsError)
        {
            return null;
        }

        return value.Type switch
        {
            ExpressionValueType.Number => value.AsNumber(),
            ExpressionValueType.Text => (string?)value.Value,
            ExpressionValueType.Boolean => value.Value as bool?,
            ExpressionValueType.Date => value.Value is DateTime d ? DateOnly.FromDateTime(d) : null,
            _ => null,
        };
    }
}
