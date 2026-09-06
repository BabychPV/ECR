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

    /// <summary>
    /// Розкриті залежності всіх формул версії — для <c>cfg.FormulaDependency</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Таблиця залежностей не наповнювалася НІЧИМ. Наслідок мовчазний і
    /// найгірший з можливих: граф залежностей порожній, тож каскадний
    /// перерахунок не бачить похідних комірок — числа лишаються старими без
    /// жодної помилки на екрані (<c>A7-63</c>).
    ///
    /// ⚠ Розбір повторюється, а не переиспользовується з <see cref="Run"/>.
    /// Публікація — рідкісна операція, а зчепити збереження з перевіркою
    /// означало б, що жодну з них не можна змінити окремо. Ціна — один
    /// зайвий розбір на публікацію.
    ///
    /// ⚠ Діапазони тут уже РОЗКРИТІ в конкретні <c>RowKey</c>: у рантаймі
    /// діапазонів не існує (`B03` §4), і саме тому зміна порядку рядків після
    /// публікації не змінює результат.
    /// </remarks>
    /// <param name="version">Версія, що публікується.</param>
    /// <param name="formulaEngine">Рушій — розбір виразів.</param>
    /// <returns>Залежності, готові до збереження.</returns>
    public static IReadOnlyList<FormulaDependency> Dependencies(
        TemplateVersion version, IFormulaEngine formulaEngine)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(formulaEngine);

        var snapshot = Snapshot(version);
        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), new RangeExpander());

        var tables = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .ToDictionary(t => t.Id);

        var result = new List<FormulaDependency>();

        foreach (var table in tables.Values)
        {
            foreach (var formula in table.Formulas.Where(f => !f.IsDeleted))
            {
                var parsed = formulaEngine.Parse(formula.Expression, formula.Dialect);

                // Непридатний вираз сюди не доходить: публікація вже
                // відхилена `Run`. Але метод має бути придатним і окремо —
                // мовчазний `NullReferenceException` при збереженні гірший
                // за пропущену формулу.
                if (parsed.Expression is null)
                {
                    continue;
                }

                var dependencies = extractor.Extract(
                    parsed.Expression.Root, table.Id, RowKeyOf(table, formula), tables);

                foreach (var dependency in dependencies)
                {
                    result.Add(FormulaDependency.ForFormula(
                        formula.Id,
                        dependency.DependsOnKind,
                        dependency.TableDefId,
                        dependency.RowKey,
                        dependency.ColumnDefId,
                        dependency.FilterJson,
                        dependency.PeriodOffset,
                        dependency.SortOrder));
                }
            }
        }

        return result;
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

    /// <summary>Знімок структури версії — для резолвера посилань і типів.</summary>
    /// <param name="version">Версія, що публікується.</param>
    public static TemplateVersionSnapshot Snapshot(TemplateVersion version)
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

    /// <summary>
    /// Перевіряє ПРАВИЛА версії: суперечливі рівні (<c>ФВ-5.10</c>) і
    /// обов'язкові колонки без покриття (<c>ФВ-5.11</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Обидві перевірки названі вимогами і не існували. Знайдено матрицею
    /// трасування: вимоги лишалися непокритими, і спроба знайти для них тест
    /// показала, що перевіряти нічого — <see cref="Run"/> дивиться лише на
    /// формули. Наслідок точно той, від якого вимоги застерігають:
    /// суперечність виявляється в рантаймі, коли оператор уже не може
    /// зберегти рядок і не розуміє чому.
    ///
    /// ⚠ Окремий метод, а не гілка всередині <see cref="Run"/>: той потребує
    /// рушія формул і контекстів типів, а ці дві перевірки — самої лише
    /// структури. Зшити їх означало б вимагати рушій там, де він не потрібен.
    /// </remarks>
    /// <param name="version">Версія, що публікується.</param>
    /// <returns>Перелік проблем; порожній — правила несуперечливі.</returns>
    public static IReadOnlyList<ExpressionDiagnostic> CheckRules(TemplateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var diagnostics = new List<ExpressionDiagnostic>();

        foreach (var table in version.Sheets.SelectMany(s => s.Tables).Where(t => !t.IsDeleted))
        {
            CheckSeverityConflicts(table, diagnostics);
            CheckRequiredCoverage(table, diagnostics);
        }

        return diagnostics;
    }

    /// <summary>
    /// Правила з перетинними областями дії і різними рівнями (<c>ФВ-5.10</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Область дії — це пара «рівень області × колонка», а не сам предикат:
    /// чи перетинаються УМОВИ двох правил, статично не з'ясувати. Але дві дії
    /// на ту саму комірку з різними рівнями — суперечність незалежно від
    /// умов: комірковий <c>Error</c> блокує запис, а <c>Warning</c> ні, і
    /// оператор бачить пораду, якої не може виконати.
    ///
    /// ⚠ Правило без <c>ColumnDefId</c> накриває ВСІ колонки таблиці, тому
    /// перетинається з кожним правилом тієї ж області.
    /// </remarks>
    private static void CheckSeverityConflicts(TableDef table, List<ExpressionDiagnostic> diagnostics)
    {
        var active = table.ValidationRules.Where(r => r.IsActive).ToList();

        foreach (var scope in active.Select(r => r.Scope).Distinct())
        {
            var inScope = active.Where(r => r.Scope == scope).ToList();

            foreach (var rule in inScope)
            {
                foreach (var other in inScope)
                {
                    // Пара розглядається один раз і лише в один бік: інакше
                    // кожна суперечність приїхала б до користувача двічі.
                    if (string.CompareOrdinal(rule.Code, other.Code) >= 0)
                    {
                        continue;
                    }

                    if (rule.Severity == other.Severity || !Overlap(rule, other))
                    {
                        continue;
                    }

                    diagnostics.Add(new ExpressionDiagnostic(
                        ExpressionErrors.RuleConflict,
                        $"Правила {rule.Code} ({rule.Severity}) і {other.Code} ({other.Severity}) "
                        + $"діють на ту саму область таблиці {table.Code} з різними рівнями.",
                        0,
                        1));
                }
            }
        }
    }

    /// <summary>Чи накривають два правила спільну колонку.</summary>
    private static bool Overlap(ValidationRule left, ValidationRule right)
        => left.ColumnDefId is null || right.ColumnDefId is null
           || left.ColumnDefId == right.ColumnDefId;

    /// <summary>
    /// Обов'язкові колонки без правила і без формули (<c>ФВ-5.11</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Структурна перевірка обов'язковості спрацьовує НА ЗАПИСІ: явна
    /// порожнеча в обов'язковій колонці відхиляється. Комірку, якої не
    /// торкалися взагалі, вона не бачить — записи не було. Тому «обов'язкова»
    /// без жодного правила рівня рядка, таблиці чи документа — це обіцянка
    /// без виконавця: подання пройде з незаповненим полем.
    ///
    /// ⚠ Обчислювана колонка покриття не потребує: значення в ній з'являється
    /// саме, і «не заповнено» для неї означало б помилку розрахунку.
    /// </remarks>
    private static void CheckRequiredCoverage(TableDef table, List<ExpressionDiagnostic> diagnostics)
    {
        var covered = table.ValidationRules
            .Where(r => r.IsActive)
            .Select(r => r.ColumnDefId)
            .ToHashSet();

        // Правило без колонки накриває таблицю цілком.
        var coversEverything = covered.Contains(null);

        var computed = table.Formulas
            .Where(f => !f.IsDeleted)
            .Select(f => f.ColumnDefId)
            .ToHashSet();

        foreach (var column in table.Columns.Where(c => !c.IsDeleted && c.IsRequired))
        {
            if (coversEverything || covered.Contains(column.Id) || computed.Contains(column.Id))
            {
                continue;
            }

            diagnostics.Add(new ExpressionDiagnostic(
                ExpressionErrors.RequiredNotCovered,
                $"Колонка {table.Code}.{column.Code} обов'язкова, але її не перевіряє жодне "
                + "правило і не заповнює жодна формула: незаповнене значення не буде помічене.",
                0,
                1));
        }
    }
}
