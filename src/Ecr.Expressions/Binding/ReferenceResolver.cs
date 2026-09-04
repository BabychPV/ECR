using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Резолвить посилання в конкретні <c>TableDefId</c>, <c>ColumnDefId</c>,
/// <c>RowKey</c>. Виконується **при публікації**: нерезолвлене посилання має
/// зупинити публікацію, а не зіпсувати число в проді.
/// </summary>
public sealed class ReferenceResolver(TemplateVersionSnapshot snapshot)
{
    /// <summary>Резолвить одне посилання.</summary>
    /// <param name="node">Вузол посилання.</param>
    /// <param name="currentTableDefId">Таблиця, в якій живе формула — для скорочених форм.</param>
    /// <param name="currentRowKey">Рядок формули; <c>null</c> для формул рівня колонки.</param>
    /// <param name="diagnostics">Куди складати зауваження публікації.</param>
    /// <param name="currentColumnDefId">
    /// Колонка, яку підставляє <c>{Month}</c>; <c>null</c> — плейсхолдер
    /// лишається нерозв'язаним і посилання прив'язується до колонки за кодом.
    /// </param>
    /// <returns>Резолвлене посилання або <c>null</c>, якщо воно не резолвиться.</returns>
    public ResolvedReference? Resolve(
        CellReferenceNode node,
        int currentTableDefId,
        string? currentRowKey,
        List<ExpressionDiagnostic>? diagnostics = null,
        int? currentColumnDefId = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        var table = FindTable(node, currentTableDefId, diagnostics);
        if (table is null)
        {
            return null;
        }

        var columnId = ResolveColumn(node, table, currentColumnDefId, diagnostics);
        if (columnId is null)
        {
            return null;
        }

        switch (node.Row)
        {
            case RowSelector.Current:
                return new ResolvedReference(table.Id, currentRowKey, columnId.Value, node.PeriodOffset, null);

            case RowSelector.Single single:
            {
                // ⚠ Для RowMode = Dynamic конкретний RowKey ЗАБОРОНЕНИЙ:
                // динамічні рядки створює користувач, вони живуть у документі,
                // а не в шаблоні — посилання на конкретний із них не має сенсу
                // і зламається на першому ж документі без цього рядка.
                if (table.AllowsDynamicRows)
                {
                    Report(diagnostics, node,
                        $"Таблиця '{table.Code}' динамічна: посилатися на конкретний рядок " +
                        $"'{single.RowKey}' не можна, лише предикатом.");
                    return null;
                }

                if (!snapshot.RowsByKey.ContainsKey((table.Id, single.RowKey)))
                {
                    Report(diagnostics, node,
                        $"Рядка '{single.RowKey}' немає в таблиці '{table.Code}'.");
                    return null;
                }

                return new ResolvedReference(table.Id, single.RowKey, columnId.Value, node.PeriodOffset, null);
            }

            case RowSelector.Range:
                // Діапазон резолвиться не в одне посилання, а в СПИСОК —
                // цим займається DependencyExtractor разом із RangeExpander.
                return new ResolvedReference(table.Id, null, columnId.Value, node.PeriodOffset, null);

            case RowSelector.Predicate predicate:
            {
                if (!table.AllowsDynamicRows)
                {
                    Report(diagnostics, node,
                        $"Таблиця '{table.Code}' фіксована: предикат тут зайвий, рядки відомі наперед.");
                    return null;
                }

                return new ResolvedReference(
                    table.Id, null, columnId.Value, node.PeriodOffset, ToFilterJson(predicate.Condition));
            }

            default:
                Report(diagnostics, node, "Невідомий селектор рядків.");
                return null;
        }
    }

    /// <summary>Предикат у вигляді, придатному для <c>cfg.FormulaDependency.FilterJson</c>.</summary>
    public static string ToFilterJson(AstNode condition)
        => JsonSerializer.Serialize(new Dictionary<string, string> { ["where"] = AstPrinter.Print(condition) });

    private TableDef? FindTable(CellReferenceNode node, int currentTableDefId, List<ExpressionDiagnostic>? diagnostics)
    {
        var tables = snapshot.Sheets.SelectMany(s => s.Tables.Select(t => (Sheet: s, Table: t))).ToList();

        if (node.TableCode is null)
        {
            var own = tables.FirstOrDefault(x => x.Table.Id == currentTableDefId).Table;
            if (own is null)
            {
                Report(diagnostics, node, $"Таблиці {currentTableDefId}, у якій живе формула, немає у знімку.");
            }

            return own;
        }

        var candidates = tables
            .Where(x => string.Equals(x.Table.Code, node.TableCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (node.SheetCode is { } sheetCode)
        {
            candidates = candidates
                .Where(x => string.Equals(x.Sheet.Code, sheetCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            // Без коду аркуша шукаємо в ТОМУ САМОМУ аркуші, де живе формула:
            // [Main].[…] означає «таблиця Main тут», а не «будь-яка Main».
            var ownSheet = tables.FirstOrDefault(x => x.Table.Id == currentTableDefId).Sheet;
            if (ownSheet is not null)
            {
                candidates = candidates.Where(x => x.Sheet.Id == ownSheet.Id).ToList();
            }
        }

        if (candidates.Count == 0)
        {
            Report(diagnostics, node,
                $"Таблиці '{node.SheetCode ?? "…"}.{node.TableCode}' немає в опублікованій версії шаблону.");
            return null;
        }

        return candidates[0].Table;
    }

    private static int? ResolveColumn(
        CellReferenceNode node, TableDef table, int? currentColumnDefId, List<ExpressionDiagnostic>? diagnostics)
    {
        if (node.ColumnSelector is "{Month}" or "{Period}")
        {
            if (currentColumnDefId is { } id)
            {
                return id;
            }

            Report(diagnostics, node,
                "Плейсхолдер '{Month}' можна вжити лише у формулі, прив'язаній до місячної колонки.");
            return null;
        }

        var column = table.Columns.FirstOrDefault(
            c => !c.IsDeleted && string.Equals(c.Code, node.ColumnSelector, StringComparison.OrdinalIgnoreCase));

        if (column is null)
        {
            Report(diagnostics, node, $"Колонки '{node.ColumnSelector}' немає в таблиці '{table.Code}'.");
            return null;
        }

        // ⚠ Обмеження на Lookup-колонку тут НЕ перевіряється, хоч і спокусливо.
        // `02b` §5 забороняє її саме **в арифметиці** — а резолвер не знає,
        // у якій операції стоїть посилання. Перша версія відхиляла КОЖНЕ
        // посилання на Lookup, і разом із арифметикою відкидала цілком законний
        // предикат `[WHERE [WasteType] = 'W-01']` (Q-072). Правило живе там, де
        // видно контекст, — у TypeChecker: Lookup має тип Text, і в арифметиці
        // він падає сам, а в порівнянні з рядком працює.
        return column.Id;
    }

    private static void Report(List<ExpressionDiagnostic>? diagnostics, AstNode node, string message)
        => diagnostics?.Add(new ExpressionDiagnostic(
            ExpressionErrors.Unresolved, message, node.Position, 1));
}

/// <summary>Резолвлене посилання.</summary>
public sealed record ResolvedReference(
    int TableDefId, string? RowKey, int ColumnDefId, int PeriodOffset, string? FilterJson);
