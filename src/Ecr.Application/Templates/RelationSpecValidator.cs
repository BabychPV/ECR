// src/Ecr.Application/Templates/RelationSpecValidator.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Templates;

/// <summary>
/// Перевірка схем D-230 проти колонок таблиць версії (див. припущення в <c>RelationSpec.cs</c>).
/// Чиста функція: нічого не читає з бази.
/// </summary>
/// <remarks>
/// Види <c>Mirror</c>/<c>Reference</c>/<c>Cascade</c>/<c>Copy</c> — лише декларація, схем для них
/// немає, тож вони тут проходять без перевірки (наявна поведінка не змінюється).
/// </remarks>
public static class RelationSpecValidator
{
    /// <summary>Перевіряє схему зв'язку.</summary>
    /// <param name="kind">Вид зв'язку.</param>
    /// <param name="matchJson">Зіставлення рядків.</param>
    /// <param name="mapJson">Налаштування виду.</param>
    /// <param name="sourceColumns">Колонки таблиці-джерела: код → тип.</param>
    /// <param name="targetColumns">Колонки таблиці-приймача: код → тип.</param>
    /// <returns><c>null</c> — усе гаразд; інакше причина відмови.</returns>
    public static RelationSpecFailure? Validate(
        TableRelationKind kind, string matchJson, string? mapJson,
        IReadOnlyDictionary<string, CellDataType> sourceColumns,
        IReadOnlyDictionary<string, CellDataType> targetColumns)
    {
        if (kind is not (TableRelationKind.Rollup or TableRelationKind.Check))
        {
            return null;
        }

        var match = RelationSpecParser.ParseMatch(matchJson);
        if (!match.IsOk)
        {
            return match.Failure;
        }

        foreach (var key in match.Value!.Keys)
        {
            if (!sourceColumns.ContainsKey(key.Source))
            {
                return ColumnMissing("MatchJson key source", key.Source, "source");
            }

            if (!targetColumns.ContainsKey(key.Target))
            {
                return ColumnMissing("MatchJson key target", key.Target, "target");
            }
        }

        if (kind == TableRelationKind.Rollup)
        {
            var spec = RelationSpecParser.ParseRollup(mapJson);
            if (!spec.IsOk)
            {
                return spec.Failure;
            }

            var r = spec.Value!;
            var failure = Column("sourceColumn", r.SourceColumn, sourceColumns, "source", requireNumeric: r.Aggregate != RollupAggregate.Count)
                ?? Column("targetColumn", r.TargetColumn, targetColumns, "target", requireNumeric: true);
            if (failure is not null)
            {
                return failure;
            }

            // D-230: приймач Rollup пише система, тож це колонка Formula — її руками не правлять
            // (PATCH відмовляє ECR-CELL-4221). Інакше значення Rollup і ручне введення перекривали б одне одного.
            return targetColumns[r.TargetColumn] == CellDataType.Formula
                ? null
                : new RelationSpecFailure(
                    "targetNotFormula",
                    $"Column \"{r.TargetColumn}\" (targetColumn) has type {targetColumns[r.TargetColumn]}; the Rollup target must be a Formula column (calculated by the system, not edited by hand).");
        }

        var check = RelationSpecParser.ParseCheck(mapJson);
        if (!check.IsOk)
        {
            return check.Failure;
        }

        var c = check.Value!;
        return Column("left", c.Left, sourceColumns, "source", requireNumeric: true)
            ?? Column("right", c.Right, targetColumns, "target", requireNumeric: true);
    }

    private static RelationSpecFailure? Column(
        string field, string code, IReadOnlyDictionary<string, CellDataType> columns, string table, bool requireNumeric)
    {
        if (!columns.TryGetValue(code, out var type))
        {
            return ColumnMissing(field, code, table);
        }

        // Formula/Calculated дають число, але тип значення відомий лише в рантаймі — пропускаємо.
        if (requireNumeric && type is CellDataType.String or CellDataType.Bool or CellDataType.Date
            or CellDataType.Lookup or CellDataType.Unit)
        {
            return new RelationSpecFailure(
                "columnNotNumeric", $"Column \"{code}\" ({field}) has type {type}; a numeric column (Int/Decimal/Formula/Calculated) is required.");
        }

        return null;
    }

    private static RelationSpecFailure ColumnMissing(string field, string code, string table)
        => new("columnMissing", $"{field}: column \"{code}\" does not exist in the {table} table of this version.");
}
