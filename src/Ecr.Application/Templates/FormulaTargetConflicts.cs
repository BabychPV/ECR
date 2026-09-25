using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Templates;

/// <summary>
/// Публікаційна перевірка: дві формули не обчислюють одну комірку (V-03).
/// </summary>
/// <remarks>
/// ⛔ V-03 (UX-прохід 2026-09-24, третій раунд): рядкова формула (<c>RTOT</c>)
/// і колонкова (<c>CFRM</c>) обидві цілили в <c>RTOT·CFRM</c>. Публікація
/// цього не бачила, а перерахунок давав два записи з однією адресою в одному
/// пакеті й падав на <c>PRIMARY KEY … dbo.@cells</c> — подання 500, фонова
/// задача ретраїла хвилинами. Рушій тепер вирішує такий збіг детерміновано
/// (пише остання в порядку обчислення) — це запобіжник для вже опублікованих
/// версій, а не задум: котре з двох чисел автор мав на увазі, знає лише він.
/// Тому нову таку конфігурацію публікація відхиляє з назвою обох формул.
///
/// ⚠ Ціль формули рядка без явної колонки — «свій рядок у кожній колонці,
/// тип якої приймає результат» (<see cref="FormulaTargetTypes"/>, V-04).
/// Колонка, куди рядкова формула однаково не напише (Lookup, дата для
/// числового результату), конфлікту не утворює. Невідомий статичний тип
/// (<c>IF</c>, <c>REGFIELD</c>) — це «може бути будь-що», тобто найсуворіше
/// припущення: конфлікт за будь-якої колонки, що приймає хоч щось.
///
/// ⚠ Адреси в підстановках — у нотації мови виразів, а не словами:
/// <c>RTOT·*</c> (рядок у всіх колонках), <c>*·CFRM</c> (колонка в усіх
/// рядках), <c>RTOT·CFRM</c> (комірка). Так повідомлення не змішує мов, яким
/// би не був інтерфейс.
/// </remarks>
public static class FormulaTargetConflicts
{
    /// <summary>Ключ каталогу діагностики.</summary>
    public const string MessageKey = "expr.publish.formulaTargetConflict";

    /// <summary>Знаходить пари формул, що обчислюють ту саму комірку.</summary>
    /// <param name="version">Версія, що публікується.</param>
    /// <param name="formulaEngine">Рушій — статичний тип результату формули рядка.</param>
    /// <returns>По одному зауваженню на кожну пару, що перетинається.</returns>
    public static IReadOnlyList<ExpressionDiagnostic> Check(TemplateVersion version, IFormulaEngine formulaEngine)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(formulaEngine);

        var diagnostics = new List<ExpressionDiagnostic>();

        foreach (var table in PublishChecks.LiveTables(version.Sheets))
        {
            var columns = table.Columns.Where(c => !c.IsDeleted).ToDictionary(c => c.Id);
            var targets = table.Formulas
                .Where(f => !f.IsDeleted)
                .OrderBy(f => f.Id)
                .Select(f => Target.Of(f, table, columns, formulaEngine))
                .Where(t => t is not null)
                .Select(t => t!)
                .ToList();

            for (var i = 0; i < targets.Count; i++)
            {
                for (var j = i + 1; j < targets.Count; j++)
                {
                    if (Cell(targets[i], targets[j], columns) is { } cell)
                    {
                        diagnostics.Add(Diagnostic(table, targets[i], targets[j], cell));
                    }
                }
            }
        }

        return diagnostics;
    }

    /// <summary>Спільна комірка двох цілей; <c>null</c> — не перетинаються.</summary>
    private static string? Cell(Target left, Target right, Dictionary<int, ColumnDef> columns)
    {
        if (left.RowKey is not null && right.RowKey is not null && left.RowKey != right.RowKey)
        {
            return null;
        }

        var row = left.RowKey ?? right.RowKey;

        if (left.ColumnDefId is { } l && right.ColumnDefId is { } r)
        {
            return l == r ? AddressOf(row, columns[l].Code) : null;
        }

        // Хоча б одна сторона — рядок «у всіх колонках»: перетин там, де
        // інша сторона має конкретну колонку і рядкова туди справді напише.
        var concreteId = left.ColumnDefId ?? right.ColumnDefId;
        if (concreteId is { } concrete)
        {
            var wildcard = left.ColumnDefId is null ? left : right;
            return wildcard.Writes(columns[concrete]) ? AddressOf(row, columns[concrete].Code) : null;
        }

        // Обидві — рядкові «у всіх колонках» на тому самому рядку.
        var shared = columns.Values.FirstOrDefault(c => left.Writes(c) && right.Writes(c));
        return shared is null ? null : AddressOf(row, shared.Code);
    }

    private static string AddressOf(string? row, string? column) => $"{row ?? "*"}·{column ?? "*"}";

    private static ExpressionDiagnostic Diagnostic(TableDef table, Target first, Target second, string cell)
        => new(
            ExpressionErrors.Unresolved,
            $"Формули {first.Address} і {second.Address} обидві обчислюють комірку {cell} таблиці "
            + $"'{table.Code}': комірку може обчислювати лише одна формула.",
            0,
            1,
            MessageKey,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["first"] = first.Address,
                ["second"] = second.Address,
                ["cell"] = cell,
                ["table"] = table.Code,
            });

    /// <summary>Ціль формули: рядок і колонка; <c>null</c> — «усі».</summary>
    private sealed record Target(string? RowKey, int? ColumnDefId, ExpressionValueType? ResultType, string Address)
    {
        /// <summary>Чи пише рядкова формула «у всіх колонках» у цю колонку.</summary>
        public bool Writes(ColumnDef column)
            => ResultType is { } type && type != ExpressionValueType.Null
                ? FormulaTargetTypes.Accepts(column.DataType, type)
                : Enum.GetValues<ExpressionValueType>().Any(t => FormulaTargetTypes.Accepts(column.DataType, t));

        public static Target? Of(
            FormulaDef formula, TableDef table, Dictionary<int, ColumnDef> columns, IFormulaEngine engine)
        {
            var rowKey = formula.RowDefId is { } rowId
                ? table.Rows.FirstOrDefault(r => r.Id == rowId && !r.IsDeleted)?.RowKeyValue
                : null;

            int? columnId = formula.ColumnDefId is { } id && columns.ContainsKey(id) ? id : null;
            var columnCode = columnId is { } c ? columns[c].Code : null;

            return formula.Scope switch
            {
                FormulaScope.Column when columnId is not null
                    => new Target(null, columnId, null, AddressOf(null, columnCode)),
                FormulaScope.Row when rowKey is not null && formula.ColumnDefId is null
                    => new Target(rowKey, null, engine.Parse(formula.Expression, formula.Dialect).Expression?.ResultType,
                        AddressOf(rowKey, null)),
                FormulaScope.Row or FormulaScope.Cell when rowKey is not null && columnId is not null
                    => new Target(rowKey, columnId, null, AddressOf(rowKey, columnCode)),
                _ => null,
            };
        }
    }
}
