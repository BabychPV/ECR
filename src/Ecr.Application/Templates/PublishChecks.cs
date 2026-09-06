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

        var tables = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .ToDictionary(t => t.Id);

        var nodes = new List<FormulaNode>();

        foreach (var table in tables.Values)
        {
            foreach (var formula in table.Formulas.Where(f => !f.IsDeleted))
            {
                // ⛔ Перевірки одного виразу винесені в `CheckExpression` і
                // викликаються ЗВІДСИ. Редактор виразів (`ФВ-9.15a`) кличе той
                // самий метод: інакше «перевірка при введенні» і публікація
                // були б двома реалізаціями одного переліку, і редактор світив
                // би зеленим те, що публікація відхилить.
                var checkResult = CheckExpression(
                    formula.Expression,
                    formula.Dialect,
                    new ExpressionSite(table.Id, RowKeyOf(table, formula), formula.ColumnDefId),
                    new ExpressionScope(formulaEngine, snapshot, typeContext, unitContext),
                    diagnostics);

                if (checkResult is null)
                {
                    continue;
                }

                var dependencies = checkResult.Dependencies;

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
    /// <param name="formulaEngine">Рушій — розбір виразів і обхід AST.</param>
    /// <returns>Залежності, готові до збереження.</returns>
    public static IReadOnlyList<FormulaDependency> Dependencies(
        TemplateVersion version, IFormulaEngine formulaEngine)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(formulaEngine);

        var snapshot = Snapshot(version);

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

                // ⚠ Колонка формули передається так само, як у `Run`. Без неї
                // плейсхолдер `{Month}` не резолвився, і збережений граф
                // МОВЧКИ втрачав ребра саме тих формул, які пишуться на
                // місячну колонку, — перевірка публікації їх бачила, а
                // каскадний перерахунок уже ні.
                var extraction = formulaEngine.ExtractDependencies(
                    parsed.Expression,
                    snapshot,
                    new DependencyContext(table.Id, RowKeyOf(table, formula), formula.ColumnDefId));

                foreach (var dependency in extraction.Dependencies)
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
        IReadOnlyList<FormulaDependencyRef> dependencies,
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
                if (Recalculation.FormulaOutputs.Produces(
                        candidate, table, dependency.RowKey, dependency.ColumnDefId))
                {
                    result.Add(candidate.Id);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Ключ рядка формули — те саме визначення, що й у рантаймі.
    /// </summary>
    /// <remarks>
    /// ⚠ Правило «що обчислює формула» винесене в
    /// <see cref="Recalculation.FormulaOutputs"/>: воно потрібне і тут, при
    /// побудові топологічного порядку, і в рантаймі, при побудові зворотного
    /// індексу. Дві копії розійшлися б на першій правці, і розбіжність була б
    /// видима лише як неправильне число.
    /// </remarks>
    private static string? RowKeyOf(TableDef table, FormulaDef formula)
        => Recalculation.FormulaOutputs.RowKeyOf(table, formula);

    /// <summary>
    /// Перевірки ОДНОГО виразу — рівно ті й рівно в тому порядку, які
    /// застосовує публікація (<c>02b</c> §12, пункти 1–3, 5, 7–12).
    /// </summary>
    /// <remarks>
    /// ⛔ Метод існує заради ОДНОГО твердження: «редактор показує ті самі
    /// зауваження на тих самих позиціях, що й публікація» (<c>ФВ-9.15a</c>).
    /// Друга реалізація цього переліку розійшлася б із першою на першій же
    /// правці, і розбіжність була б видима не як помилка, а як довіра до
    /// зеленого редактора, після якого публікація відмовляє.
    ///
    /// ⚠ Тут НЕМАЄ перевірок 4 і 6 — ациклічності графа і порядку обчислення.
    /// Це не пропуск: цикл є властивістю ВЕРСІЇ, а не виразу. Один вираз не
    /// містить у собі відповіді на питання, чи утворює він цикл із рештою, і
    /// вдавати цю відповідь означало б обіцяти те, чого перевірка не робить.
    /// </remarks>
    /// <param name="expression">Текст виразу.</param>
    /// <param name="dialect">Діалект (<c>D-113</c>).</param>
    /// <param name="site">Місце виразу в структурі: таблиця, рядок, колонка.</param>
    /// <param name="scope">Оточення перевірки: рушій, таблиці, контексти.</param>
    /// <param name="diagnostics">Куди складати зауваження.</param>
    /// <returns>
    /// Розібраний вираз і його залежності; <c>null</c> — вираз не розібрався,
    /// і решта перевірок безпредметна.
    /// </returns>
    public static ExpressionCheckResult? CheckExpression(
        string expression,
        ExpressionDialect dialect,
        ExpressionSite site,
        ExpressionScope scope,
        List<ExpressionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(diagnostics);

        // 1, 7, 8. Синтаксис, наявність функції в діалекті, кількість і типи
        // аргументів; плюс заборонені в діалекті посилання.
        var parsed = scope.FormulaEngine.Parse(expression, dialect);
        diagnostics.AddRange(parsed.Diagnostics);

        if (parsed.Expression is null)
        {
            return null;
        }

        var root = parsed.Expression.Root;

        // 11. Предикат динамічного діапазону — без заборонених конструкцій.
        // ⚠ Не потребує ані структури, ані контекстів: працює завжди.
        PredicateValidator.Validate(root, diagnostics);

        // 2, 5, 12. Резолвінг посилань, розкриття діапазонів у списки RowKey,
        // заборона конкретного RowKey для RowMode = Dynamic.
        //
        // ⛔ Обхід AST робить РУШІЙ (`H-3`), а не власний екземпляр
        // `DependencyExtractor` тут. Доти і редактор, і публікація тримали по
        // своєму — тобто по власній відповіді на питання «від чого залежить
        // формула»; розійшовшись, вони давали б різні зауваження на той самий
        // текст, а `ФВ-9.15a` тримається саме на їхній тотожності.
        //
        // ⚠ Без знімка структури ці три перевірки ПРОПУСКАЮТЬСЯ — саме так, як
        // пропускаються типи й одиниці без своїх контекстів. Редактор без
        // версії шаблону перевіряє лише те, що можна перевірити без неї, і
        // порожній перелік тоді означає «синтаксис цілий», а не «все гаразд».
        IReadOnlyList<FormulaDependencyRef> dependencies = [];

        if (scope.Structure is { } structure && site.TableDefId is { } tableDefId)
        {
            var extraction = scope.FormulaEngine.ExtractDependencies(
                parsed.Expression,
                structure,
                new DependencyContext(tableDefId, site.RowKey, site.ColumnDefId));

            dependencies = extraction.Dependencies;
            diagnostics.AddRange(extraction.Diagnostics);
        }

        // 3. Типи сумісні в кожній операції.
        if (scope.TypeContext is { } typeContext)
        {
            new TypeChecker().Check(root, typeContext, diagnostics);
        }

        // 9, 10. Одиниці сумісні або є явний CONVERT.
        if (scope.UnitContext is { } unitContext)
        {
            new UnitChecker().Check(root, unitContext, diagnostics);
        }

        return new ExpressionCheckResult(parsed.Expression, dependencies);
    }

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

/// <summary>
/// Місце виразу в структурі версії.
/// </summary>
/// <remarks>
/// ⚠ Усі три поля необов'язкові, і це не послаблення. Редактор виразів
/// відкривають і тоді, коли формула ще нікуди не прив'язана: людина пише текст
/// і лише потім вирішує, чиєю колонкою він буде. Вимагати місце наперед
/// означало б, що перевірити вираз можна лише після того, як його вже кудись
/// поклали.
/// </remarks>
/// <param name="TableDefId">Таблиця, в якій живе вираз; <c>null</c> — ще ніде.</param>
/// <param name="RowKey">Рядок формули; <c>null</c> для формул рівня колонки.</param>
/// <param name="ColumnDefId">Колонка — для підстановки <c>{Month}</c>.</param>
public readonly record struct ExpressionSite(int? TableDefId, string? RowKey, int? ColumnDefId);

/// <summary>
/// Оточення перевірки виразу: що саме можна перевірити в цьому виклику.
/// </summary>
/// <remarks>
/// ⛔ Кожне поле, яке дорівнює <c>null</c>, ВИМИКАЄ свою групу перевірок, і
/// перелік зауважень стає рівно настільки повним, наскільки повне оточення.
/// Це названо явно, бо порожній перелік без структури означає «синтаксис
/// цілий», а не «вираз правильний», — і сплутати ці два твердження означає
/// пообіцяти публікацію, якої не буде.
/// </remarks>
/// <param name="FormulaEngine">Розбір виразу і обхід AST; потрібен завжди.</param>
/// <param name="Structure">
/// Знімок структури версії; <c>null</c> — посилання не перевіряються.
/// </param>
/// <param name="TypeContext">Джерело типів; <c>null</c> — типи не перевіряються.</param>
/// <param name="UnitContext">Джерело одиниць; <c>null</c> — одиниці не перевіряються.</param>
public sealed record ExpressionScope(
    IFormulaEngine FormulaEngine,
    TemplateVersionSnapshot? Structure,
    ITypeContext? TypeContext,
    IUnitContext? UnitContext);

/// <summary>Результат перевірки одного виразу.</summary>
/// <param name="Expression">Розібраний вираз із типом результату.</param>
/// <param name="Dependencies">Комірки, від яких вираз залежить.</param>
public sealed record ExpressionCheckResult(
    ParsedExpression Expression,
    IReadOnlyList<FormulaDependencyRef> Dependencies);
