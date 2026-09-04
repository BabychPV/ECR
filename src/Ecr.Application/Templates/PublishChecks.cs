using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Templates;

/// <summary>
/// Дванадцять перевірок публікації (02b §12).
/// </summary>
/// <remarks>
/// ⚠ Виконуються **ВСІ** до першої публікації версії, і публікація або
/// проходить цілком, або відхиляється з переліком проблем. Зупинка на першій
/// помилці змусила б користувача публікувати версію десятки разів,
/// виправляючи по одній.
///
/// Чому саме тут, а не в рантаймі: помилка типу або одиниці, виявлена під час
/// нічного перерахунку, — це неправильні числа у звіті, які хтось помітить
/// через місяць на звірці. Виявлена при публікації — це червоний екран
/// конфігуратора, який виправляють за хвилину.
/// </remarks>
public static class PublishChecks
{
    /// <summary>Перевіряє всі формули версії.</summary>
    /// <param name="version">Версія, що публікується.</param>
    /// <param name="formulaEngine">Рушій — розбір і топологічний порядок.</param>
    /// <param name="typeContext">Джерело типів; <c>null</c> — перевірка типів пропускається.</param>
    /// <param name="unitContext">Джерело одиниць; <c>null</c> — перевірка одиниць пропускається.</param>
    /// <returns>Перелік проблем; порожній — версію можна публікувати.</returns>
    public static IReadOnlyList<ExpressionDiagnostic> Run(
        TemplateVersion version,
        IFormulaEngine formulaEngine,
        ITypeContext? typeContext = null,
        IUnitContext? unitContext = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(formulaEngine);

        var diagnostics = new List<ExpressionDiagnostic>();
        var snapshot = Snapshot(version);
        var resolver = new ReferenceResolver(snapshot);
        var expander = new RangeExpander();
        var extractor = new DependencyExtractor(resolver, expander);
        var typeChecker = new TypeChecker();
        var unitChecker = new UnitChecker();

        var tables = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .ToDictionary(t => t.Id);

        var nodes = new List<FormulaNode>();

        foreach (var table in tables.Values)
        {
            foreach (var formula in table.Formulas.Where(f => !f.IsDeleted))
            {
                var parsed = formulaEngine.Parse(formula.Expression, formula.Dialect);
                diagnostics.AddRange(parsed.Diagnostics);

                if (parsed.Expression is null)
                {
                    continue;
                }

                var root = parsed.Expression.Root;
                var rowKey = RowKeyOf(table, formula);

                // 11. Предикат динамічного діапазону — без заборонених конструкцій.
                PredicateValidator.Validate(root, diagnostics);

                // 2, 5, 12. Резолвінг посилань, розкриття діапазонів у списки
                // RowKey, заборона конкретного RowKey для RowMode = Dynamic.
                var dependencies = extractor.Extract(
                    root, table.Id, rowKey, tables, diagnostics, formula.ColumnDefId);

                // 3. Типи сумісні в кожній операції.
                if (typeContext is not null)
                {
                    typeChecker.Check(root, typeContext, diagnostics);
                }

                // 9, 10. Одиниці сумісні або є явний CONVERT.
                if (unitContext is not null)
                {
                    unitChecker.Check(root, unitContext, diagnostics);
                }

                nodes.Add(new FormulaNode(
                    formula.Id, table.Id, formula.Scope, formula.ColumnDefId, formula.RowDefId,
                    DependsOn(formula, dependencies, tables)));
            }
        }

        // 4, 6. Ациклічність графа і обчислення EvaluationOrder.
        // Версія без формул не має чого впорядковувати — і це не «все гаразд
        // за замовчуванням», а відсутність предмета перевірки.
        if (nodes.Count == 0)
        {
            return diagnostics;
        }

        var ordering = formulaEngine.BuildEvaluationOrder(nodes);
        if (!ordering.IsSuccess)
        {
            diagnostics.Add(new ExpressionDiagnostic(
                ExpressionErrors.Cycle,
                $"Формули утворюють цикл: {string.Join(" → ", ordering.CyclePath ?? [])}.",
                0, 1));
        }
        else
        {
            // Порядок фіксується ПРИ ПУБЛІКАЦІЇ, а не будується щоразу в
            // рантаймі: сортувати граф на кожен запит — витрата, якої бюджет
            // не передбачає (ФВ-9.4).
            var order = 0;
            foreach (var id in ordering.Order)
            {
                var formula = tables.Values
                    .SelectMany(t => t.Formulas)
                    .FirstOrDefault(f => f.Id == id);

                formula?.SetEvaluationOrder(order++);
            }
        }

        return diagnostics;
    }

    /// <summary>Формули, від яких залежить ця — для топологічного порядку.</summary>
    private static List<int> DependsOn(
        FormulaDef formula,
        IReadOnlyList<ExtractedDependency> dependencies,
        Dictionary<int, TableDef> tables)
    {
        var result = new List<int>();

        foreach (var dependency in dependencies)
        {
            if (dependency.TableDefId is not { } tableId
                || !tables.TryGetValue(tableId, out var table))
            {
                continue;
            }

            // Формула залежить від ІНШОЇ ФОРМУЛИ, якщо читає комірку, яку та
            // формула обчислює. Без цього ребра баланс порахувався б раніше
            // за суми, з яких він складається.
            foreach (var candidate in table.Formulas.Where(f => !f.IsDeleted && f.Id != formula.Id))
            {
                if (Produces(candidate, table, dependency))
                {
                    result.Add(candidate.Id);
                }
            }
        }

        return result;
    }

    private static bool Produces(FormulaDef formula, TableDef table, ExtractedDependency dependency)
    {
        if (formula.ColumnDefId is { } columnId && dependency.ColumnDefId != columnId
            && formula.Scope != FormulaScope.Row)
        {
            return false;
        }

        return formula.Scope switch
        {
            FormulaScope.Column => formula.ColumnDefId == dependency.ColumnDefId,
            FormulaScope.Row => RowKeyOf(table, formula) == dependency.RowKey,
            _ => formula.ColumnDefId == dependency.ColumnDefId
                 && RowKeyOf(table, formula) == dependency.RowKey,
        };
    }

    private static string? RowKeyOf(TableDef table, FormulaDef formula)
        => formula.RowDefId is { } rowId
            ? table.Rows.FirstOrDefault(r => r.Id == rowId)?.RowKeyValue
            : null;

    /// <summary>Знімок структури версії — для резолвера посилань.</summary>
    private static TemplateVersionSnapshot Snapshot(TemplateVersion version)
    {
        var columns = new Dictionary<int, ColumnDef>();
        var rows = new Dictionary<(int TableDefId, string RowKey), RowDef>();

        foreach (var table in version.Sheets.SelectMany(s => s.Tables))
        {
            foreach (var column in table.Columns)
            {
                columns[column.Id] = column;
            }

            foreach (var row in table.Rows)
            {
                rows[(table.Id, row.RowKeyValue)] = row;
            }
        }

        return new TemplateVersionSnapshot(
            version.Id, version.PresentationRevision, version.Sheets, columns, rows);
    }
}
