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
                        "expr.ref.rowKeyInDynamicTable", DiagnosticParams.Of(("table", table.Code), ("row", single.RowKey)),
                        $"Table \"{table.Code}\" is dynamic: a specific row \"{single.RowKey}\" cannot be "
                        + "referenced, only a predicate.");
                    return null;
                }

                if (!snapshot.RowsByKey.ContainsKey((table.Id, single.RowKey)))
                {
                    Report(diagnostics, node,
                        "expr.ref.unknownRow", DiagnosticParams.Of(("row", single.RowKey), ("table", table.Code)),
                        $"Row \"{single.RowKey}\" does not exist in table \"{table.Code}\".");
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
                        "expr.ref.predicateInFixedTable", DiagnosticParams.Of(("table", table.Code)),
                        $"Table \"{table.Code}\" is fixed: a predicate is not needed, its rows are known in advance.");
                    return null;
                }

                return new ResolvedReference(
                    table.Id, null, columnId.Value, node.PeriodOffset, ToFilterJson(predicate.Condition));
            }

            default:
                Report(diagnostics, node, "expr.ref.unknownRowSelector", null, "Unknown row selector.");
                return null;
        }
    }

    /// <summary>Предикат у вигляді, придатному для <c>cfg.FormulaDependency.FilterJson</c>.</summary>
    public static string ToFilterJson(AstNode condition)
        => JsonSerializer.Serialize(new Dictionary<string, string> { ["where"] = AstPrinter.Print(condition) });

    /// <summary>Резолвить посилання на поле шапки документа (<c>HDR.Код</c>).</summary>
    /// <param name="node">Вузол символьного посилання з <c>Kind = SymbolKind.Header</c>.</param>
    /// <param name="diagnostics">Куди складати зауваження публікації.</param>
    /// <returns>Визначення поля або <c>null</c>, якщо такого коду немає в знімку.</returns>
    /// <remarks>
    /// ⛔ За зразком <see cref="FindTable"/>: до цього методу така перевірка
    /// не існувала взагалі, і `DependencyExtractor.Visit` додавав Header-
    /// залежність за `header.Name` БЕЗ жодного резолвінгу — код поля міг бути
    /// друкарською помилкою, публікація мовчала, а формула в рантаймі
    /// рахувалася як Null.
    /// </remarks>
    public HeaderFieldDef? ResolveHeader(SymbolReferenceNode node, List<ExpressionDiagnostic>? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(node);

        var field = snapshot.HeaderFields.FirstOrDefault(
            f => string.Equals(f.Code, node.Name, StringComparison.OrdinalIgnoreCase));

        if (field is null)
        {
            Report(diagnostics, node,
                "expr.ref.unknownHeaderField", DiagnosticParams.Of(("name", node.Name)),
                $"Header field \"{node.Name}\" does not exist in the published template version.");
        }

        return field;
    }

    private TableDef? FindTable(CellReferenceNode node, int currentTableDefId, List<ExpressionDiagnostic>? diagnostics)
    {
        var tables = snapshot.Sheets.SelectMany(s => s.Tables.Select(t => (Sheet: s, Table: t))).ToList();

        if (node.TableCode is null)
        {
            var own = tables.FirstOrDefault(x => x.Table.Id == currentTableDefId).Table;
            if (own is null)
            {
                Report(diagnostics, node,
                    "expr.ref.ownTableMissing",
                    DiagnosticParams.Of(("tableDefId", currentTableDefId.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    $"Table {currentTableDefId}, which holds the formula, is not in the snapshot.");
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
                "expr.ref.unknownTable", DiagnosticParams.Of(("sheet", node.SheetCode ?? "…"), ("table", node.TableCode)),
                $"Table \"{node.SheetCode ?? "…"}.{node.TableCode}\" does not exist in the published template version.");
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
                "expr.ref.monthPlaceholderOutsideColumn", null,
                "The month placeholder can be used only in a formula bound to a monthly column.");
            return null;
        }

        var column = table.Columns.FirstOrDefault(
            c => !c.IsDeleted && string.Equals(c.Code, node.ColumnSelector, StringComparison.OrdinalIgnoreCase));

        if (column is null)
        {
            Report(diagnostics, node,
                "expr.ref.unknownColumn", DiagnosticParams.Of(("column", node.ColumnSelector), ("table", table.Code)),
                $"Column \"{node.ColumnSelector}\" does not exist in table \"{table.Code}\".");
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

    private static void Report(
        List<ExpressionDiagnostic>? diagnostics,
        AstNode node,
        string messageKey,
        IReadOnlyDictionary<string, string>? messageParams,
        string message)
        => diagnostics?.Add(new ExpressionDiagnostic(
            ExpressionErrors.Unresolved, message, node.Position, 1, messageKey, messageParams));

    // ——— Довідники: перевірки 15 і 16 (FEATURE-REGISTRY-TABLES §5.5, `02b` §12) ———

    /// <summary>
    /// Код довідника з першого аргументу функції довідника, якщо він —
    /// рядковий літерал; <c>null</c> — ні (перевірка 15, <c>expr.registryCodeMustBeLiteral</c>).
    /// </summary>
    /// <param name="node">Перший аргумент <c>REGFIND</c>/<c>REGONE</c>/агрегата.</param>
    public static string? RegistryCodeLiteral(AstNode node)
        => node is LiteralNode { Type: ExpressionValueType.Text, Value: string code } ? code : null;

    /// <summary>Резолвить довідник за кодом (перевірка 15).</summary>
    /// <param name="registries">Джерело форм довідників.</param>
    /// <param name="code">Код — рядковий літерал виразу.</param>
    /// <param name="literal">Вузол літерала — для позиції діагностики.</param>
    /// <param name="diagnostics">Куди складати зауваження; <c>null</c> — мовчки.</param>
    /// <returns>Форма довідника; <c>null</c> — такого немає.</returns>
    /// <remarks>
    /// ⚠ Позиція — сам літерал разом із лапками: редактор підсвічує саме код,
    /// а не весь виклик.
    /// </remarks>
    public static RegistryShape? ResolveRegistry(
        IRegistryShapeSource registries,
        string code,
        AstNode literal,
        List<ExpressionDiagnostic>? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(literal);

        var shape = registries.FindRegistry(code);
        if (shape is null)
        {
            diagnostics?.Add(new ExpressionDiagnostic(
                ExpressionErrors.Unresolved,
                $"Registry \"{code}\" does not exist.",
                literal.Position, code.Length + 2,
                "expr.registryUnknown", DiagnosticParams.Of(("registry", code))));
        }

        return shape;
    }

    /// <summary>
    /// Проходить шлях поля від довідника <paramref name="registry"/>:
    /// кожен сегмент існує, проміжні — поля <c>Lookup</c> (перевірка 16).
    /// </summary>
    /// <param name="registries">Джерело форм — за ним іде перехід через <c>Lookup</c>.</param>
    /// <param name="registry">Довідник, від якого починається шлях.</param>
    /// <param name="path">Коди полів: <c>[COMPONENT, MW]</c>.</param>
    /// <param name="segmentSpan">Позиція й довжина сегмента <c>i</c> у тексті виразу.</param>
    /// <param name="diagnostics">Куди складати зауваження; <c>null</c> — мовчки.</param>
    /// <returns>Останнє поле шляху; <c>null</c> — шлях не проходить.</returns>
    /// <remarks>
    /// ⛔ Це і є закриття <c>Д-5</c>: до цього методу описка в коді поля
    /// <c>REGFIELD</c> проходила публікацію мовчки й виявлялася лише як
    /// <c>#REF</c> у нічному прогоні — тобто неправильною клітинкою звіту,
    /// яку помітять через місяць, а не червоним рядком редактора.
    ///
    /// ⚠ Звітує ПЕРШУ ваду шляху й зупиняється: після невідомого сегмента
    /// решта шляху не має довідника, в якому її шукати, і кожне наступне
    /// зауваження було б наслідком першого.
    /// </remarks>
    public static RegistryFieldShape? ResolveRegistryPath(
        IRegistryShapeSource registries,
        RegistryShape registry,
        IReadOnlyList<string> path,
        Func<int, (int Position, int Length)> segmentSpan,
        List<ExpressionDiagnostic>? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(segmentSpan);

        var current = registry;
        RegistryFieldShape? field = null;

        for (var i = 0; i < path.Count; i++)
        {
            if (field is not null)
            {
                // Проміжний сегмент мусить вести в інший довідник.
                if (field.DataType != CellDataType.Lookup || field.LookupRegistryCode is null)
                {
                    ReportAt(diagnostics, segmentSpan(i - 1),
                        "expr.registryFieldNotLookup",
                        DiagnosticParams.Of(("registry", current.Code), ("field", field.Code)),
                        $"Field \"{field.Code}\" of registry \"{current.Code}\" is not a Lookup field: "
                        + "a field path can continue only through Lookup fields.");
                    return null;
                }

                var next = registries.FindRegistry(field.LookupRegistryCode);
                if (next is null)
                {
                    ReportAt(diagnostics, segmentSpan(i - 1),
                        "expr.registryUnknown",
                        DiagnosticParams.Of(("registry", field.LookupRegistryCode)),
                        $"Registry \"{field.LookupRegistryCode}\" does not exist.");
                    return null;
                }

                current = next;
            }

            field = current.FindField(path[i]);
            if (field is null)
            {
                ReportAt(diagnostics, segmentSpan(i),
                    "expr.registryFieldUnknown",
                    DiagnosticParams.Of(("registry", current.Code), ("field", path[i])),
                    $"Registry \"{current.Code}\" has no field \"{path[i]}\".");
                return null;
            }
        }

        return field;
    }

    private static void ReportAt(
        List<ExpressionDiagnostic>? diagnostics,
        (int Position, int Length) span,
        string messageKey,
        IReadOnlyDictionary<string, string> messageParams,
        string message)
        => diagnostics?.Add(new ExpressionDiagnostic(
            ExpressionErrors.Unresolved, message, span.Position, Math.Max(1, span.Length), messageKey, messageParams));
}

/// <summary>Резолвлене посилання.</summary>
public sealed record ResolvedReference(
    int TableDefId, string? RowKey, int ColumnDefId, int PeriodOffset, string? FilterJson);
