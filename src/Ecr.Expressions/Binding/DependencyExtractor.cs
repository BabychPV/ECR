using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Витягує залежності виразу для збереження в <c>cfg.FormulaDependency</c>.
/// Зворотний індекс по цій таблиці — основа інкрементного перерахунку.
/// </summary>
public sealed class DependencyExtractor(ReferenceResolver resolver, RangeExpander expander)
{
    /// <summary>Вид залежності: комірка (<c>cfg.FormulaDependency.DependsOnKind</c> = 0).</summary>
    public const byte KindCell = 0;

    /// <summary>Поле шапки документа.</summary>
    public const byte KindHeader = 1;

    /// <summary>Крос-періодне посилання.</summary>
    public const byte KindCrossPeriod = 3;

    /// <summary>
    /// Поле запису довідника — друге ребро <c>REGFIELD</c>, КРІМ звичайної
    /// Cell-залежності від самої Lookup-комірки (ту додає загальний обхід
    /// аргументів функції, як для будь-якого іншого посилання-аргументу).
    /// </summary>
    /// <remarks>
    /// ✎ RT-21: також ребро «довідник/шлях поля» функцій довідників
    /// (<c>REGFIND</c>, <c>REGONE</c>, агрегати, <c>ROW.a.b</c>) — без таблиці,
    /// див. <c>AddRegistryUse</c>.
    /// </remarks>
    public const byte KindRegistry = 2;

    /// <summary>Обходить AST і збирає всі залежності.</summary>
    /// <param name="root">Корінь виразу.</param>
    /// <param name="currentTableDefId">Таблиця, в якій живе формула.</param>
    /// <param name="currentRowKey">Рядок формули; <c>null</c> для формул рівня колонки.</param>
    /// <param name="tables">Таблиці для розкриття діапазонів: <c>TableDefId</c> → таблиця.</param>
    /// <param name="diagnostics">Куди складати зауваження публікації.</param>
    /// <param name="currentColumnDefId">Колонка для плейсхолдера <c>{Month}</c>.</param>
    public IReadOnlyList<ExtractedDependency> Extract(
        AstNode root,
        int currentTableDefId,
        string? currentRowKey,
        IReadOnlyDictionary<int, TableDef>? tables = null,
        List<ExpressionDiagnostic>? diagnostics = null,
        int? currentColumnDefId = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<ExtractedDependency>();
        Visit(root, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId, rowRegistry: null);
        return found;
    }

    // `rowRegistry` — код довідника області `ROW` (перший аргумент найближчого
    // агрегата чи REGONE); `null` — області немає або код не літерал.
    private void Visit(
        AstNode node,
        List<ExtractedDependency> found,
        int currentTableDefId,
        string? currentRowKey,
        IReadOnlyDictionary<int, TableDef>? tables,
        List<ExpressionDiagnostic>? diagnostics,
        int? currentColumnDefId,
        string? rowRegistry)
    {
        // ⛔ L7-01: лівий гребінь ланцюга — рекурсія глибиною в кількість ланок.
        if (!TraversalStackGuard.TryEnter(node, diagnostics))
        {
            return;
        }

        switch (node)
        {
            case CellReferenceNode reference:
                Add(reference, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId);
                return;

            case SymbolReferenceNode { Kind: SymbolKind.Header } header:
                // ⛔ До цього рядка код поля НЕ перевірявся: залежність
                // додавалася за `header.Name` без резолвінгу, і `HDR("TYPO")`
                // з неіснуючим кодом публікувався без жодного зауваження —
                // той самий мовчазний клас дефекту, що вже описаний для
                // невідомої таблиці/колонки (`CellReferenceNode` нижче, через
                // `resolver.Resolve`). `ResolveHeader` звітує в `diagnostics`
                // тим самим каналом (`ExpressionDiagnostic`, код
                // `ExpressionErrors.Unresolved`) і повертає `null` — тоді
                // залежність не додається, рівно як `Add(CellReferenceNode …)`
                // не додає її для нерезолвленого посилання на комірку.
                if (resolver.ResolveHeader(header, diagnostics) is not null)
                {
                    found.Add(new ExtractedDependency(
                        KindHeader, null, header.Name, null, null, null, found.Count));
                }

                return;

            case SymbolReferenceNode { Kind: SymbolKind.Formula } formula:
                // Залежність між формулами потрібна саме для топологічного
                // порядку: без неї !Base порахувалася б після того, хто її
                // читає, і результат був би «майже правильним».
                found.Add(new ExtractedDependency(
                    KindCell, null, formula.Name, null, null, null, found.Count));
                return;

            // Календарний контекст не є залежністю: він не змінюється від
            // правки комірок, тож інкрементний перерахунок його не стосується.
            case PeriodPropertyNode:
            case LiteralNode:
            case SymbolReferenceNode:
                return;

            case UnaryNode unary:
                Visit(unary.Operand, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId, rowRegistry);
                return;

            case BinaryNode binary:
                Visit(binary.Left, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId, rowRegistry);
                Visit(binary.Right, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId, rowRegistry);
                return;

            case ConditionalNode conditional:
                Visit(conditional.Condition, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId, rowRegistry);
                Visit(conditional.WhenTrue, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId, rowRegistry);
                Visit(conditional.WhenFalse, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId, rowRegistry);
                return;

            // `ROW.a.b` — поле рядка області агрегата: ребро «довідник області /
            // шлях». Поза відомою областю (код не літерал, правило довідника,
            // якого видобувач не знає) ребра немає — про невідомий код уже
            // сказав TypeChecker.
            case RowFieldNode rowField:
                if (rowRegistry is not null)
                {
                    AddRegistryUse(found, rowRegistry, string.Join('.', rowField.Path));
                }

                return;

            case FunctionNode function:
                // ⚠ REGFIELD дає ДВА ребра з одного вузла: звичайне Cell —
                // від Lookup-комірки аргументу 0 (його додає обхід нижче,
                // той самий шлях, яким комірка-аргумент іде для будь-якої
                // іншої функції), і Registry — від ПОЛЯ довідника, яке ця
                // комірка вибирає. Друге видобуває `AddRegistryDependency`
                // ДО обходу, бо саме воно статичне лише тут: у рантаймі
                // конкретний `EntryId` залежить від значення комірки.
                if (function.Arguments.Count == 2
                    && string.Equals(function.Name, "REGFIELD", StringComparison.OrdinalIgnoreCase))
                {
                    AddRegistryDependency(function, found, currentTableDefId, currentRowKey, currentColumnDefId);
                    AddRegistryFieldUse(function, found, rowRegistry);
                }

                // RT-21 (§5.8): функції довідників дають ребро «довідник» на
                // сам код, а агрегати й REGONE відкривають область `ROW` для
                // аргументів з другого — внутрішній рядок затіняє зовнішній.
                var registryCode = RegistryCodeOf(function);
                if (registryCode is not null)
                {
                    AddRegistryUse(found, registryCode, null);
                }

                var opensRow = RegistryForms.RowScopeNames.Contains(function.Name);

                for (var i = 0; i < function.Arguments.Count; i++)
                {
                    Visit(function.Arguments[i], found, currentTableDefId, currentRowKey, tables, diagnostics,
                        currentColumnDefId, opensRow && i > 0 ? registryCode : rowRegistry);
                }

                return;

            default:
                return;
        }
    }

    private void Add(
        CellReferenceNode reference,
        List<ExtractedDependency> found,
        int currentTableDefId,
        string? currentRowKey,
        IReadOnlyDictionary<int, TableDef>? tables,
        List<ExpressionDiagnostic>? diagnostics,
        int? currentColumnDefId)
    {
        var resolved = resolver.Resolve(
            reference, currentTableDefId, currentRowKey, diagnostics, currentColumnDefId);

        if (resolved is null)
        {
            return;
        }

        var kind = reference.PeriodOffset == 0 ? KindCell : KindCrossPeriod;
        var offset = reference.PeriodOffset == 0 ? (short?)null : (short)reference.PeriodOffset;

        if (reference.Row is RowSelector.Range range)
        {
            // ⚠ Діапазон РОЗКРИВАЄТЬСЯ тут, при публікації, і далі не існує.
            // Саме це робить зміну Ordinal після публікації безпечною:
            // формула вже посилається на конкретні рядки.
            if (tables is null || !tables.TryGetValue(resolved.TableDefId, out var table))
            {
                diagnostics?.Add(new ExpressionDiagnostic(
                    ExpressionErrors.Unresolved,
                    "The range cannot be expanded: the table is not available.",
                    reference.Position, 1,
                    "expr.ref.rangeTableUnavailable"));
                return;
            }

            foreach (var rowKey in expander.Expand(table, range.FromRowKey, range.ToRowKey, diagnostics, reference.Position))
            {
                found.Add(new ExtractedDependency(
                    kind, resolved.TableDefId, rowKey, resolved.ColumnDefId, null, offset, found.Count));
            }

            return;
        }

        found.Add(new ExtractedDependency(
            kind, resolved.TableDefId, resolved.RowKey, resolved.ColumnDefId,
            resolved.FilterJson, offset, found.Count));
    }

    /// <summary>
    /// Registry-ребро <c>REGFIELD(lookup, 'код')</c>: адреса Lookup-комірки —
    /// та сама, що резолвить звичайний обхід аргументу, — і код поля з
    /// другого аргументу, разом.
    /// </summary>
    /// <remarks>
    /// ⛔ Код поля лягає в <see cref="ExtractedDependency.FilterJson"/> — те
    /// саме поле, яким Cell-залежність несе предикат `RowMode = Dynamic`.
    /// Конфлікту немає: REGFIELD приймає лише ОДНУ комірку аргументом 0
    /// (перевірка нижче), тому Registry-запис на предикатний рядок ніколи не
    /// трапляється — FilterJson тут завжди означає код поля, а в Cell-записі
    /// того самого вузла (їх додає окремий обхід) — завжди предикат або
    /// <c>null</c>. Заводити п'яте поле під один рядок означало б повторити
    /// вже наявну колонку заради розрізнення, якого дає сам <c>DependsOnKind</c>.
    /// </remarks>
    private void AddRegistryDependency(
        FunctionNode function,
        List<ExtractedDependency> found,
        int currentTableDefId,
        string? currentRowKey,
        int? currentColumnDefId)
    {
        if (function.Arguments[0] is not CellReferenceNode reference)
        {
            // Перший аргумент — не пряме посилання (вкладений вираз, IF
            // тощо): статичної адреси Lookup-комірки нема звідки взяти. Це не
            // помилка публікації — REGFIELD однаково порахується в рантаймі,
            // лише без цього другого ребра графа залежностей.
            return;
        }

        if (function.Arguments[1] is not LiteralNode { Type: ExpressionValueType.Text, Value: string fieldCode })
        {
            // Код поля обчислюється в рантаймі, а не написаний літералом —
            // статично невідомий, видобувати залежність нема на що.
            return;
        }

        // REGFIELD адресує ОДНУ комірку: діапазон чи предикат аргументом 0
        // синтаксично можливі (це звичайний `CellReferenceNode`), але
        // Registry-ребро для них не має сенсу — яке з багатьох значень
        // діапазону дає id запису, невідомо статично. `Function` теж
        // обчислить помилку в рантаймі (`RegistryField` вимагає рівно одне
        // значення на групу), тут лише немає чим доповнити граф залежностей.
        if (reference.Row is RowSelector.Range or RowSelector.Predicate)
        {
            return;
        }

        // ⚠ Лише ПОТОЧНИЙ період: `RecalculationService` сьогодні будує
        // знімок довідника за поточний зріз, а не за минулі періоди
        // (симетрично тому, як Cell-залежність із `PeriodOffset != 0` не
        // входить у зворотний індекс `RecalculationPlanBuilder`). Cell/
        // CrossPeriod ребро для самої комірки генеричний обхід додає
        // однаково — лише другого, Registry-ребра, для минулого періоду тут
        // не буде.
        if (reference.PeriodOffset != 0)
        {
            return;
        }

        // ⚠ `diagnostics: null` тут НАВМИСНО, а не недогляд: той самий вузол
        // резолвиться ще раз у генеричному обході (для Cell-залежності), і
        // ЙОГО виклик уже звітує про нерезолвлене посилання. Передати список
        // сюди теж означало б подвоєне зауваження на одну й ту саму помилку —
        // редактор показав би її двічі там, де публікація одну (`ФВ-9.15a`).
        var resolved = resolver.Resolve(reference, currentTableDefId, currentRowKey, null, currentColumnDefId);

        if (resolved is null)
        {
            return;
        }

        found.Add(new ExtractedDependency(
            KindRegistry, resolved.TableDefId, resolved.RowKey, resolved.ColumnDefId,
            fieldCode, null, found.Count));
    }

    /// <summary>
    /// Код довідника функції довідника — рядковий літерал першого аргументу
    /// <c>REGFIND</c>/<c>REGONE</c>/агрегата; <c>null</c> — інша функція або код
    /// обчислюється (перевірка 15 звітує про це в <c>TypeChecker</c>).
    /// </summary>
    private static string? RegistryCodeOf(FunctionNode function)
        => function.Arguments.Count > 0
           && (RegistryForms.RowScopeNames.Contains(function.Name)
               || string.Equals(function.Name, RegistryForms.Find, StringComparison.OrdinalIgnoreCase))
            ? ReferenceResolver.RegistryCodeLiteral(function.Arguments[0])
            : null;

    /// <summary>
    /// Ребро «довідник/шлях поля» від <c>REGFIELD(entry, 'p')</c>, коли довідник
    /// запису статичний: <c>REGFIND</c>/<c>REGONE</c> із літералом або
    /// <c>ROW.a</c> відомої області (тоді шлях — <c>a.p</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Запис із <c>Lookup</c>-комірки шаблону тут не розбирається: його довідник
    /// знає лише колонка, і ребро для нього вже дає <see cref="AddRegistryDependency"/>
    /// (адреса комірки + код поля). Запис з <c>!Formula</c>, <c>@Arg</c> чи
    /// <c>THIS</c> видобувач без форм довідників не бачить — його називає
    /// публікація за типом <c>EntryRef</c> (RT-23b).
    /// </remarks>
    private static void AddRegistryFieldUse(FunctionNode function, List<ExtractedDependency> found, string? rowRegistry)
    {
        if (function.Arguments[1] is not LiteralNode { Type: ExpressionValueType.Text, Value: string path })
        {
            return;
        }

        switch (function.Arguments[0])
        {
            case FunctionNode entry when RegistryCodeOf(entry) is { } code
                                         && (entry.Name.Equals(RegistryForms.Find, StringComparison.OrdinalIgnoreCase)
                                             || entry.Name.Equals(RegistryForms.One, StringComparison.OrdinalIgnoreCase)):
                AddRegistryUse(found, code, path);
                return;

            case RowFieldNode row when rowRegistry is not null:
                AddRegistryUse(found, rowRegistry, string.Join('.', row.Path) + "." + path);
                return;
        }
    }

    /// <summary>
    /// Ребро <see cref="KindRegistry"/> «довідник/шлях поля» функцій довідників
    /// (§5.8) — один раз на пару в межах виразу.
    /// </summary>
    /// <param name="found">Зібрані залежності.</param>
    /// <param name="registryCode">Код довідника, як його написано у виразі.</param>
    /// <param name="fieldPath">Шлях поля через крапку (<c>COMPONENT.MW</c>); <c>null</c> — сам довідник.</param>
    /// <remarks>
    /// ⛔ Форма ребра відрізняється від шаблонного <c>REGFIELD</c> і розрізняється
    /// за <c>TableDefId</c>: тут його НЕМАЄ (<c>null</c>), бо довідник названо
    /// кодом, а не комірка несе id запису. Тоді <c>RowKey</c> — код довідника,
    /// <c>FilterJson</c> — шлях поля. Та сама конвенція, що вже діє для
    /// <c>!Formula</c> (Cell без таблиці, <c>RowKey</c> — код формули). Наявні
    /// читачі <see cref="KindRegistry"/> (<c>RecalculationService</c>,
    /// <c>ValidationEngine</c>) вимагають <c>TableDefId</c> і такі ребра
    /// пропускають; у <c>cfg.RegistryUse</c> їх переписує публікація (RT-23b, RT-24).
    /// </remarks>
    private static void AddRegistryUse(List<ExtractedDependency> found, string registryCode, string? fieldPath)
    {
        var duplicate = found.Any(d => d.DependsOnKind == KindRegistry
                                       && d.TableDefId is null
                                       && string.Equals(d.RowKey, registryCode, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(d.FilterJson, fieldPath, StringComparison.OrdinalIgnoreCase));
        if (!duplicate)
        {
            found.Add(new ExtractedDependency(KindRegistry, null, registryCode, null, fieldPath, null, found.Count));
        }
    }
}

/// <summary>Витягнута залежність.</summary>
/// <param name="DependsOnKind">0 Cell, 1 Header, 2 Registry, 3 CrossPeriod, 4 CrossProject.</param>
/// <param name="TableDefId">Таблиця; <c>null</c> для шапки.</param>
/// <param name="RowKey">Конкретний рядок; <c>null</c> для предиката.</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="FilterJson">Предикат для <c>RowMode = Dynamic</c>.</param>
/// <param name="PeriodOffset">Зсув періоду; <c>null</c> для поточного.</param>
/// <param name="SortOrder">Порядковий номер у списку залежностей формули.</param>
public sealed record ExtractedDependency(
    byte DependsOnKind, int? TableDefId, string? RowKey, int? ColumnDefId,
    string? FilterJson, short? PeriodOffset, int SortOrder);
