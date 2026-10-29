using System.Diagnostics;
using System.Reflection;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// L2b (recalc-latency): індекс виробників комірок у <see cref="RecalculationPlanBuilder"/> дає ТОЙ САМИЙ план, що й
/// прямий перебір формул таблиці (еталон — дослівна копія старого алгоритму нижче), і не квадратичний.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази (запущені локально, не комітяться):
/// (1) у <c>ProducerIndex.Bucket.Collect</c> не збирати <c>_byRow</c> — червоніє диференційний тест;
/// (2) у <c>ProducerIndex.Candidates</c> не дивитися на кошик <c>NoColumn</c> — червоніє на рядкових формулах без колонки;
/// (3) у <c>ProducerIndex.Build</c> класти всі формули в один кошик (звести індекс до перебору) — червоніє бюджет часу.
/// </remarks>
public sealed class RecalculationPlanBuilderIndexTests
{
    private static readonly PeriodKey Period = new(202604);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void План_з_індексом_збігається_з_планом_прямого_перебору(int seed)
    {
        var world = BuildRandomWorld(seed);

        var actual = RecalculationPlanBuilder.Build(world.Snapshot, world.Dependencies, world.RowIds, Period);
        var expected = ReferencePlan(world.Snapshot, world.Dependencies, world.RowIds, Period);

        Assert.Equal(expected.AllFormulas(), actual.AllFormulas());

        foreach (var formulaId in expected.AllFormulas().Append(-1))
        {
            Assert.Equal(expected.DependentsOfFormula(formulaId), actual.DependentsOfFormula(formulaId));
            Assert.Equal(expected.IsCrossSheet(formulaId), actual.IsCrossSheet(formulaId));
            Assert.Equal(expected.IsSnapshot(formulaId), actual.IsSnapshot(formulaId));
            Assert.Equal(expected.EvaluationOrder(formulaId), actual.EvaluationOrder(formulaId));
        }

        var edges = 0;
        foreach (var (tableId, rows) in world.RowIds)
        {
            foreach (var rowId in rows.Values)
            {
                foreach (var columnId in world.ColumnIds[tableId])
                {
                    var address = new CellAddress(Period, rowId, columnId);
                    var expectedDependents = expected.DependentsOf(address);
                    Assert.Equal(expectedDependents, actual.DependentsOf(address));
                    edges += expectedDependents.Count;
                }
            }
        }

        // Підлога: світ справді має ребра «формула → формула» й «комірка → формула» (інакше порівняння порожнього).
        Assert.True(edges > 0, "У світі немає жодного ребра комірки.");
        Assert.True(
            expected.AllFormulas().Any(id => expected.DependentsOfFormula(id).Count > 0),
            "У світі немає жодного ребра формула → формула.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Знімок_добудований_після_першої_побудови_не_віддає_застарілого_індексу()
    {
        var world = BuildRandomWorld(5);
        _ = RecalculationPlanBuilder.Build(world.Snapshot, world.Dependencies, world.RowIds, Period);

        // Тест збирає TableDef руками й додає формулу ПІСЛЯ першої побудови (у проді знімок незмінний, але підпис
        // страхує від цього класу помилки).
        var table = world.Snapshot.Sheets.SelectMany(s => s.Tables).First();
        var column = table.Columns[0];
        var extra = new FormulaDef(table.Id, FormulaScope.Column, "1", ExpressionDialect.Template);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(extra, 900_001);
        extra.AssignColumn(column.Id);
        table.AddFormula(extra);

        var dependencies = new List<FormulaDependency>(world.Dependencies)
        {
            FormulaDependency.ForFormula(
                world.Snapshot.Sheets.SelectMany(s => s.Tables).SelectMany(t => t.Formulas).First(f => f.Id != extra.Id).Id,
                dependsOnKind: 0, table.Id, rowKey: null, column.Id, filterJson: null, periodOffset: null, sortOrder: 99),
        };

        var actual = RecalculationPlanBuilder.Build(world.Snapshot, dependencies, world.RowIds, Period);
        var expected = ReferencePlan(world.Snapshot, dependencies, world.RowIds, Period);

        Assert.Contains(extra.Id, actual.AllFormulas());
        Assert.Equal(expected.AllFormulas(), actual.AllFormulas());
        foreach (var formulaId in expected.AllFormulas())
        {
            Assert.Equal(expected.DependentsOfFormula(formulaId), actual.DependentsOfFormula(formulaId));
        }
    }

    /// <summary>
    /// Квадратичний пошук виробників (залежності × формули таблиці × лінійний пошук рядка) на шаблоні в ~40 000 формул
    /// коштував секунди на ПРАВКУ ОДНІЄЇ КОМІРКИ. Бюджет щедрий (індексований шлях тут — ~0,1–0,3 с у Debug), але
    /// на порядок нижчий за старий.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Побудова_плану_на_шаблоні_Land_масштабу_не_квадратична()
    {
        const int tables = 40;
        const int rows = 100;
        const int formulaColumns = 10;

        var builder = new TemplateBuilder();
        var sheet = builder.Sheet("S");
        var rowIds = new Dictionary<int, IReadOnlyDictionary<string, long>>();
        var dependencies = new List<FormulaDependency>();
        long nextRowId = 1;

        for (var t = 0; t < tables; t++)
        {
            var table = builder.Table(sheet, $"T{t}", TableRowMode.Fixed, TableLayoutKind.PerPeriodInstance);
            var inputs = new[] { builder.Column(table, $"A{t}"), builder.Column(table, $"B{t}") };
            var formulaColumnDefs = Enumerable.Range(0, formulaColumns)
                .Select(c => builder.Column(table, $"F{t}_{c}", CellDataType.Formula))
                .ToList();
            var keys = new Dictionary<string, long>(StringComparer.Ordinal);

            for (var r = 0; r < rows; r++)
            {
                var row = builder.Row(table, $"R{t}_{r}");
                keys[row.RowKeyValue] = nextRowId++;

                for (var c = 0; c < formulaColumns; c++)
                {
                    var formula = builder.Formula(table, "1", FormulaScope.Cell, formulaColumnDefs[c], row);
                    var source = c == 0 ? inputs[0] : formulaColumnDefs[c - 1];
                    dependencies.Add(FormulaDependency.ForFormula(
                        formula.Id, dependsOnKind: 0, table.Id, row.RowKeyValue, source.Id, null, null, 0));
                }
            }

            rowIds[table.Id] = keys;
        }

        var snapshot = builder.Build();

        // Прогрів (JIT), потім найкращий із трьох замірів.
        _ = RecalculationPlanBuilder.Build(snapshot, dependencies, rowIds, Period);
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            _ = RecalculationPlanBuilder.Build(snapshot, dependencies, rowIds, Period);
            watch.Stop();
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        Assert.True(
            best < TimeSpan.FromMilliseconds(1000),
            $"План на {tables * rows * formulaColumns} формул будувався {best.TotalMilliseconds:F0} мс (бюджет 1000 мс; "
            + "квадратичний пошук виробників коштував кілька секунд).");
    }

    // ── світ ─────────────────────────────────────────────────────────────────

    private sealed record World(
        TemplateVersionSnapshot Snapshot,
        List<FormulaDependency> Dependencies,
        Dictionary<int, IReadOnlyDictionary<string, long>> RowIds,
        Dictionary<int, List<int>> ColumnIds);

    /// <summary>
    /// Таблиці з формулами ВСІХ областей (колонкова з колонкою й без, рядкова з колонкою й без, комірочна), видаленою
    /// формулою, рядками, яких нема в екземплярі, і залежностями з порожніми ключами рядка/колонки.
    /// </summary>
    private static World BuildRandomWorld(int seed)
    {
        var random = new Random(seed);
        var builder = new TemplateBuilder();
        var sheet = builder.Sheet("S");

        var rowIds = new Dictionary<int, IReadOnlyDictionary<string, long>>();
        var columnIds = new Dictionary<int, List<int>>();
        var allFormulas = new List<FormulaDef>();
        var tables = new List<(TableDef Table, List<ColumnDef> Columns, List<RowDef> Rows)>();
        long nextRowId = 1;

        for (var t = 0; t < 4; t++)
        {
            var table = builder.Table(sheet, $"T{t}", TableRowMode.Fixed, TableLayoutKind.PerPeriodInstance);
            var columns = Enumerable.Range(0, 5).Select(c => builder.Column(table, $"C{t}_{c}")).ToList();
            var rows = Enumerable.Range(0, 6).Select(r => builder.Row(table, $"R{t}_{r}")).ToList();
            tables.Add((table, columns, rows));
            columnIds[table.Id] = [.. columns.Select(c => c.Id)];

            // Екземпляр має не всі рядки шаблону: два останніх відсутні.
            var keys = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var row in rows.Take(4))
            {
                keys[row.RowKeyValue] = nextRowId++;
            }

            rowIds[table.Id] = keys;

            for (var f = 0; f < 14; f++)
            {
                var column = columns[random.Next(columns.Count)];
                var row = rows[random.Next(rows.Count)];
                FormulaDef formula;
                switch (random.Next(5))
                {
                    case 0:
                        formula = builder.Formula(table, "1", FormulaScope.Column, column);
                        break;
                    case 1:
                        // Колонкова формула БЕЗ колонки (форма, якої БД не допускає, але домен — так).
                        formula = builder.Formula(table, "1", FormulaScope.Column);
                        break;
                    case 2:
                        formula = builder.Formula(table, "1", FormulaScope.Row, row: row);
                        break;
                    case 3:
                        // Рядкова формула з ЯВНОЮ колонкою (аудит 2026-09-16, §1.3): колонку виставляє лише рефлексія.
                        formula = builder.Formula(table, "1", FormulaScope.Row, row: row);
                        typeof(FormulaDef).GetProperty(nameof(FormulaDef.ColumnDefId))!
                            .SetValue(formula, column.Id);
                        break;
                    default:
                        formula = builder.Formula(table, "1", FormulaScope.Cell, column, row);
                        break;
                }

                allFormulas.Add(formula);
            }

            // Одна видалена формула на таблицю: не повинна ні породжувати, ні отримувати ребра.
            var deleted = builder.Formula(table, "1", FormulaScope.Column, columns[0]);
            typeof(FormulaDef).GetProperty(nameof(FormulaDef.IsDeleted))!.SetValue(deleted, true);
            allFormulas.Add(deleted);
        }

        var dependencies = new List<FormulaDependency>();
        var sort = 0;
        foreach (var formula in allFormulas.Where(f => !f.IsDeleted))
        {
            for (var d = 0; d < 4; d++)
            {
                var (table, columns, rows) = tables[random.Next(tables.Count)];

                // Ключ рядка: існуючий, відсутній в екземплярі, неіснуючий, або null (залежність від усієї колонки).
                string? rowKey = random.Next(4) switch
                {
                    0 => null,
                    1 => "NO_SUCH_ROW",
                    _ => rows[random.Next(rows.Count)].RowKeyValue,
                };

                int? columnId = random.Next(6) == 0 ? null : columns[random.Next(columns.Count)].Id;
                dependencies.Add(FormulaDependency.ForFormula(
                    formula.Id, dependsOnKind: 0, table.Id, rowKey, columnId, null, null, sort++));
            }

            // Залежності інших видів і зсуви періоду не повинні давати ребер (як і раніше).
            dependencies.Add(FormulaDependency.ForFormula(
                formula.Id, dependsOnKind: 3, tables[0].Table.Id, tables[0].Rows[0].RowKeyValue,
                tables[0].Columns[0].Id, null, periodOffset: -1, sort++));
            dependencies.Add(FormulaDependency.ForFormula(
                formula.Id, dependsOnKind: 5, tables[1].Table.Id, tables[1].Rows[0].RowKeyValue,
                tables[1].Columns[0].Id, null, periodOffset: (short)Period.Sequence, sort++));
        }

        // Залежність невідомої формули (видалена/чужа версія) — пропускається.
        dependencies.Add(FormulaDependency.ForFormula(
            999_999, dependsOnKind: 0, tables[0].Table.Id, null, tables[0].Columns[0].Id, null, null, sort));

        return new World(builder.Build(), dependencies, rowIds, columnIds);
    }

    // ── еталон: дослівна копія алгоритму ДО L2b (перебір усіх формул таблиці для кожної залежності) ─────────────

    private static RecalculationPlan ReferencePlan(
        TemplateVersionSnapshot snapshot,
        IReadOnlyList<FormulaDependency> dependencies,
        Dictionary<int, IReadOnlyDictionary<string, long>> rowIdsByTable,
        PeriodKey periodKey)
    {
        var plan = new RecalculationPlan();
        var tables = snapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);
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

            var ownPeriodAbsolute = dependency.DependsOnKind == 5 && dependency.PeriodOffset == periodKey.Sequence;
            if (dependency.DependsOnKind != 0 && !ownPeriodAbsolute)
            {
                continue;
            }

            if (!ownPeriodAbsolute && dependency.PeriodOffset is { } offset && offset != 0)
            {
                continue;
            }

            if (dependency.RowKey is null && dependency.ColumnDefId is { } wholeColumnId)
            {
                plan.DependsOnColumn(formulaId, wholeColumnId);
            }

            if (dependency.TableDefId is { } targetTable && rowIdsByTable.TryGetValue(targetTable, out var rowIds)
                && dependency.ColumnDefId is { } columnId)
            {
                if (dependency.RowKey is not { } rowKey)
                {
                    foreach (var rowId in rowIds.Values)
                    {
                        plan.DependsOnCell(formulaId, new CellAddress(periodKey, rowId, columnId));
                    }
                }
                else if (rowIds.TryGetValue(rowKey, out var id))
                {
                    plan.DependsOnCell(formulaId, new CellAddress(periodKey, id, columnId));
                }
            }

            if (dependency.TableDefId is { } tableId && tables.TryGetValue(tableId, out var table))
            {
                foreach (var candidate in table.Formulas.Where(f => !f.IsDeleted && f.Id != formulaId))
                {
                    if (!formulas.ContainsKey(candidate.Id))
                    {
                        continue;
                    }

                    if (ReferenceProduces(candidate, table, dependency.RowKey, dependency.ColumnDefId))
                    {
                        plan.DependsOnFormula(formulaId, candidate.Id);
                    }
                }
            }
        }

        return plan;
    }

    /// <summary>Дослівне правило <c>FormulaOutputs.Produces</c> ДО L2b (лінійний пошук рядка формули).</summary>
    private static bool ReferenceProduces(FormulaDef formula, TableDef table, string? rowKey, int? columnDefId)
    {
        if (formula.ColumnDefId is { } ownColumn && columnDefId != ownColumn)
        {
            return false;
        }

        string? RowKeyOf() => formula.RowDefId is { } rowId
            ? table.Rows.FirstOrDefault(r => r.Id == rowId)?.RowKeyValue
            : null;

        return formula.Scope switch
        {
            FormulaScope.Column => formula.ColumnDefId == columnDefId,
            FormulaScope.Row => RowKeyOf() == rowKey,
            _ => formula.ColumnDefId == columnDefId && RowKeyOf() == rowKey,
        };
    }
}
