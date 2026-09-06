using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Складає <see cref="RecalculationPlan"/> зі збереженого графа залежностей.
/// </summary>
/// <remarks>
/// ⛔ Це та ланка, якої не існувало. Граф зберігався (тепер), план читався
/// (`RecalculationService.Plan`), а перекласти перший на другий не було чим —
/// і каскадний перерахунок не запускався ніколи (<c>A7-63</c>).
///
/// ⚠ Переклад НЕ тривіальний, і саме тому його не було: граф описує
/// СТРУКТУРУ (<c>TableDefId</c>, <c>RowKey</c>, <c>ColumnDefId</c>), а план
/// адресує ДАНІ (<c>PeriodKey</c>, <c>TableRowId</c>, <c>ColumnDefId</c>).
/// Міст між ними — відповідність «ключ рядка → ідентифікатор рядка» в
/// конкретному екземплярі таблиці за конкретний період.
/// </remarks>
public static class RecalculationPlanBuilder
{
    /// <summary>Вид залежності «комірка» (<c>cfg.FormulaDependency.DependsOnKind</c> = 0).</summary>
    private const byte CellKind = 0;

    /// <summary>Будує план для одного екземпляра таблиці за період.</summary>
    /// <param name="snapshot">Структура версії шаблону.</param>
    /// <param name="dependencies">Розкриті залежності формул версії.</param>
    /// <param name="rowIdsByTable">
    /// Таблиця → (ключ рядка → ідентифікатор рядка) у документі за цей період.
    /// </param>
    /// <param name="periodKey">Період, для якого адресуються комірки.</param>
    /// <returns>План, придатний для <see cref="RecalculationService.Plan"/>.</returns>
    public static RecalculationPlan Build(
        TemplateVersionSnapshot snapshot,
        IReadOnlyList<FormulaDependency> dependencies,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>> rowIdsByTable,
        PeriodKey periodKey)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(rowIdsByTable);

        var plan = new RecalculationPlan();

        var tables = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .ToDictionary(t => t.Id);

        var formulas = tables.Values
            .SelectMany(t => t.Formulas.Where(f => !f.IsDeleted).Select(f => (Table: t, Formula: f)))
            .ToDictionary(pair => pair.Formula.Id);

        foreach (var (_, formula) in formulas.Values)
        {
            plan.Declare(formula.Id, formula.EvaluationOrder, formula.IsCrossSheet, formula.IsSnapshot);
        }

        foreach (var dependency in dependencies)
        {
            if (dependency.FormulaDefId is not { } formulaId || !formulas.ContainsKey(formulaId))
            {
                continue;
            }

            // ⚠ Інші види залежності — шапка, довідник, інший проєкт — не
            // адресуються коміркою цього зрізу, тож у зворотний індекс не
            // входять. Це не пропуск: правка шапки не є правкою комірки, і
            // перерахунок від неї запускається іншим шляхом.
            if (dependency.DependsOnKind != CellKind)
            {
                continue;
            }

            // ⛔ Зсув періоду ігнорується НАВМИСНО. Залежність від минулого
            // періоду не робить формулу брудною від правки в поточному, а
            // правка в минулому періоді перераховує його власний зріз —
            // де ця ж залежність має зсув 0.
            if (dependency.PeriodOffset is { } offset && offset != 0)
            {
                continue;
            }

            // ⛔ Рядки беруться з ТІЄЇ таблиці, на яку вказує залежність, а не
            // з тієї, де сталася правка. Один спільний перелік рядків давав би
            // формулі, що читає сусідню таблицю, чужі ідентифікатори — тобто
            // ребро графа в нікуди, і формула не перераховувалася б ніколи.
            if (dependency.TableDefId is { } targetTable
                && rowIdsByTable.TryGetValue(targetTable, out var rowIds))
            {
                AddCellEdge(plan, formulaId, dependency, rowIds, periodKey);
            }

            AddFormulaEdges(plan, formulaId, dependency, tables, formulas);
        }

        return plan;
    }

    /// <summary>Ребро «формула залежить від комірки».</summary>
    private static void AddCellEdge(
        RecalculationPlan plan,
        int formulaId,
        FormulaDependency dependency,
        IReadOnlyDictionary<string, long> rowIdsByKey,
        PeriodKey periodKey)
    {
        if (dependency.ColumnDefId is not { } columnId)
        {
            return;
        }

        // ⚠ Залежність БЕЗ конкретного рядка — це предикат динамічного
        // діапазону (`RowMode = Dynamic`): наперед відомого списку рядків не
        // існує, і формула вважається залежною від УСІХ рядків своєї
        // таблиці. Пропустити її означало б, що додавання рядка в динамічну
        // таблицю не перераховує підсумок.
        if (dependency.RowKey is not { } rowKey)
        {
            foreach (var rowId in rowIdsByKey.Values)
            {
                plan.DependsOnCell(formulaId, new CellAddress(periodKey, rowId, columnId));
            }

            return;
        }

        if (rowIdsByKey.TryGetValue(rowKey, out var id))
        {
            plan.DependsOnCell(formulaId, new CellAddress(periodKey, id, columnId));
        }

        // Рядка немає в цьому екземплярі — залежність просто не спрацює.
        // Це нормально: шаблон описує рядки, яких у конкретному документі
        // може не бути (`RowMode = Dynamic`).
    }

    /// <summary>Ребра «формула залежить від результату іншої формули».</summary>
    private static void AddFormulaEdges(
        RecalculationPlan plan,
        int formulaId,
        FormulaDependency dependency,
        Dictionary<int, TableDef> tables,
        Dictionary<int, (TableDef Table, FormulaDef Formula)> formulas)
    {
        if (dependency.TableDefId is not { } tableId || !tables.TryGetValue(tableId, out var table))
        {
            return;
        }

        foreach (var candidate in table.Formulas.Where(f => !f.IsDeleted && f.Id != formulaId))
        {
            if (!formulas.ContainsKey(candidate.Id))
            {
                continue;
            }

            // Те саме правило, що й при публікації: без цього ребра баланс
            // порахувався б раніше за суми, з яких він складається.
            if (FormulaOutputs.Produces(candidate, table, dependency.RowKey, dependency.ColumnDefId))
            {
                plan.DependsOnFormula(formulaId, candidate.Id);
            }
        }
    }
}
