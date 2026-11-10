// src/Ecr.Application/Documents/CalculationRejectionReasons.cs
using System.Runtime.CompilerServices;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Ast;

namespace Ecr.Application.Documents;

/// <summary>Причина відхилення рядка правилом категорії: правило, що закрило рядок, і колонки, які треба заповнити.</summary>
/// <param name="RuleCode">Правило методології, що закрило рядок; <c>null</c> — зараз жодне (рядок змінили після прогону).</param>
/// <param name="Columns">
/// Порожні колонки рядка, які згадує правило категорії (через формули й бібліотеки); обчислювана колонка шаблону
/// замінена порожньою колонкою, з якої вона рахується. Порожньо — назвати нічого (або хоч одна прихована від читача).
/// </param>
internal sealed record CalculationRejectionReason(string? RuleCode, IReadOnlyList<ColumnDef> Columns);

/// <summary>
/// Чому рядок відхилено правилом категорії (<see cref="ICalculationIssueStore.CategoryRuleRejected"/>): виводиться під час
/// ЧИТАННЯ проблем, а не зберігається — у <c>calc.CalculationIssue</c> немає колонок для правила й колонок (без міграції,
/// рішення координатора 10.11).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Версія методології — та сама, що в прогоні й у підказках (<see cref="CalculationIssueHints"/>): опублікована, чинна на
/// кінець періоду, компаратор <see cref="MethodologyVersionKey.Currency"/>. Правило — перший збіг за пріоритетом
/// (<see cref="MethodologyRuleMatcher.Classify"/>), ключі предикатів — через <see cref="MethodologyKeyLocalizer"/> (C1).
/// </para>
/// <para>
/// ⚠ Колонки — аргументи <c>@Code</c> виразу правила категорії і формул <c>!Code</c>, до яких він веде (власних і
/// бібліотечних, транзитивно), що є колонками ЦІЄЇ таблиці й порожні в рядку. Обчислювана колонка (формула шаблону,
/// напр. <c>REGFIELD([X_Ref], 'NAME')</c>) людиною не заповнюється — замість неї називаються порожні колонки, на які
/// посилається її формула (doc 95, 10.11: <c>Land_OverhauledEngines</c> → <c>Land_OverhauledEnginesRef</c>).
/// </para>
/// <para>
/// ⛔ Безпека: хоч одна названа колонка прихована від читача — колонок немає зовсім (як у підказках).
/// </para>
/// </remarks>
internal sealed class CalculationRejectionReasons(
    IRowStore rowStore,
    ICellStore cellStore,
    IMetadataCache metadata,
    IPeriodStore periods,
    IMethodologyStore methodologies,
    IFormulaEngine formulaEngine,
    IColumnPathMapper? columnMapper)
{
    /// <summary>Будує причини для відхилених рядків.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="period">Період.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <param name="rejected">Видимі проблеми виду <see cref="ICalculationIssueStore.CategoryRuleRejected"/>.</param>
    /// <param name="canReadColumn">Чи бачить читач колонку.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>(TableDefId, RowKey, MethodologyId) → причина; немає ключа — причину не виведено.</returns>
    public async Task<IReadOnlyDictionary<(int TableDefId, string RowKey, int? MethodologyId), CalculationRejectionReason>>
        BuildAsync(
            long documentId,
            PeriodKey period,
            long? sheetCopyId,
            IReadOnlyList<CalculationIssueRow> rejected,
            Func<int, bool> canReadColumn,
            CancellationToken ct)
    {
        var result = new Dictionary<(int, string, int?), CalculationRejectionReason>();
        if (rejected.Count == 0)
        {
            return result;
        }

        var bounds = await periods.FindPeriodBoundsAsync(documentId, period.Value, ct).ConfigureAwait(false);
        if (bounds is null)
        {
            return result;
        }

        var tableIds = rejected.Select(i => i.TableDefId).Distinct().ToList();
        var instances = (await rowStore.GetTableInstancesAsync(documentId, period, ct).ConfigureAwait(false))
            .Where(i => i.SheetCopyId == sheetCopyId && tableIds.Contains(i.TableDefId))
            .GroupBy(i => i.TableDefId)
            .ToDictionary(g => g.Key, g => g.First());
        if (instances.Count == 0)
        {
            return result;
        }

        var rowIds = await rowStore
            .GetRowIdsBatchAsync([.. instances.Values.Select(i => i.TableInstanceId)], period, ct)
            .ConfigureAwait(false);

        var methodologyIds = rejected.Select(i => i.MethodologyId).OfType<int>().Distinct().ToList();
        IReadOnlyList<MethodologyVersion> published = methodologyIds.Count == 0
            ? []
            : await methodologies.GetPublishedVersionsOfAsync(methodologyIds, ct).ConfigureAwait(false);
        var versionOf = published
            .Where(v => v.EffectiveFrom is not null && v.EffectiveFrom <= bounds.PeriodEnd)
            .GroupBy(v => v.MethodologyId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(v => new MethodologyVersionKey(v.EffectiveFrom, v.Version, v.Id), MethodologyVersionKey.Currency)
                    .First().Id);

        var argumentsOf = new Dictionary<int, IReadOnlySet<string>>();
        var rulesOf = new Dictionary<(int Version, int TemplateVersion), IReadOnlyList<CompiledMethodologyRule>>();

        foreach (var issue in rejected)
        {
            if (issue.MethodologyId is not { } methodologyId
                || !versionOf.TryGetValue(methodologyId, out var versionId)
                || !instances.TryGetValue(issue.TableDefId, out var instance)
                || !rowIds.TryGetValue(instance.TableInstanceId, out var keys)
                || !keys.TryGetValue(issue.RowKey, out var rowId))
            {
                continue;
            }

            var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
            if (snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == issue.TableDefId) is not { } table)
            {
                continue;
            }

            var columns = table.Columns.Where(c => !c.IsDeleted).ToList();
            var cells = await cellStore
                .ReadCellsAsync([.. columns.Select(c => new CellAddress(period, rowId, c.Id))], ct)
                .ConfigureAwait(false);
            string TextOf(ColumnDef column)
                => cells.TryGetValue(new CellAddress(period, rowId, column.Id), out var value)
                    ? MethodologyRuleMatcher.Text(value)?.Trim() ?? string.Empty
                    : string.Empty;

            // 1. Правило, що закрило рядок (перший збіг за пріоритетом, як у прогоні).
            if (!rulesOf.TryGetValue((versionId, instance.TemplateVersionId), out var compiled))
            {
                var rules = (await methodologies.GetRulesAsync(versionId, ct).ConfigureAwait(false))
                    .Where(r => r.IsActive)
                    .OrderBy(r => r.Priority)
                    .ToList();
                var localized = await MethodologyKeyLocalizer
                    .LocalizeAsync(columnMapper, instance.TemplateVersionId, rules, requiredInputs: null, ct)
                    .ConfigureAwait(false);
                compiled = MethodologyRuleMatcher.Compile(localized.Predicates);
                rulesOf[(versionId, instance.TemplateVersionId)] = compiled;
            }

            var values = columns.ToDictionary(
                c => c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                c => (string?)(TextOf(c) is { Length: > 0 } text ? text : null),
                StringComparer.Ordinal);
            var ruleCode = MethodologyRuleMatcher.Classify(compiled, values).Winner?.Code;

            // 2. Порожні колонки, які згадує правило категорії.
            if (!argumentsOf.TryGetValue(versionId, out var arguments))
            {
                arguments = await CategoryRuleArgumentsAsync(versionId, bounds.PeriodEnd, ct).ConfigureAwait(false);
                argumentsOf[versionId] = arguments;
            }

            var byCode = columns
                .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var named = new List<ColumnDef>();
            foreach (var argument in arguments)
            {
                if (!byCode.TryGetValue(argument, out var column) || TextOf(column).Length > 0)
                {
                    continue;
                }

                // Обчислювана колонка: людина заповнює джерела її формули. Джерела вже заповнені — колонка порахується
                // наступним перерахунком, називати нічого (сама вона лише для читання).
                var sources = TemplateSources(table, column, byCode).ToList();
                named.AddRange(sources.Count > 0 ? sources.Where(s => TextOf(s).Length == 0) : [column]);
            }

            var distinct = named.DistinctBy(c => c.Id).OrderBy(c => c.Ordinal).ThenBy(c => c.Id).ToList();

            // ⛔ Прихована колонка — жодної (див. remarks класу).
            result[(issue.TableDefId, issue.RowKey, issue.MethodologyId)] = new CalculationRejectionReason(
                ruleCode, distinct.All(c => canReadColumn(c.Id)) ? distinct : []);
        }

        return result;
    }

    /// <summary>Аргументи <c>@Code</c> правила категорії версії разом із формулами, до яких воно веде.</summary>
    private async Task<IReadOnlySet<string>> CategoryRuleArgumentsAsync(int versionId, DateOnly onDate, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rule = await methodologies.GetCategoryRuleAsync(versionId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rule))
        {
            return found;
        }

        var formulas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var formula in await methodologies.GetFormulasAsync(versionId, ct).ConfigureAwait(false))
        {
            formulas.TryAdd(formula.Code, formula.Expression);
        }

        // Власна формула перекриває бібліотечну з тим самим кодом (так розв'язує `!Name` і модуль).
        foreach (var library in await methodologies.GetLibraryContentsAsync(versionId, onDate, ct).ConfigureAwait(false))
        {
            foreach (var formula in library.Formulas)
            {
                formulas.TryAdd(formula.Code, formula.Expression);
            }
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(rule);
        while (pending.Count > 0)
        {
            if (formulaEngine.Parse(pending.Pop(), ExpressionDialect.Methodology).Expression?.Root is not { } root)
            {
                continue;
            }

            foreach (var symbol in Symbols(root))
            {
                if (symbol.Kind == SymbolKind.Argument)
                {
                    found.Add(symbol.Name);
                }
                else if (symbol.Kind == SymbolKind.Formula
                         && visited.Add(symbol.Name)
                         && formulas.TryGetValue(symbol.Name, out var expression))
                {
                    pending.Push(expression);
                }
            }
        }

        return found;
    }

    /// <summary>Колонки цього рядка, з яких рахується обчислювана колонка шаблону (посилання <c>[Code]</c> її формули).</summary>
    private IEnumerable<ColumnDef> TemplateSources(
        TableDef table, ColumnDef column, Dictionary<string, ColumnDef> byCode)
    {
        var formula = table.Formulas.FirstOrDefault(f => !f.IsDeleted && f.ColumnDefId == column.Id);
        if (formula is null
            || formulaEngine.Parse(formula.Expression, formula.Dialect).Expression?.Root is not { } root)
        {
            yield break;
        }

        foreach (var reference in Nodes(root).OfType<CellReferenceNode>())
        {
            if (reference.SheetCode is null
                && reference.TableCode is null
                && reference.PeriodOffset == 0
                && byCode.TryGetValue(reference.ColumnSelector, out var source)
                && source.Id != column.Id)
            {
                yield return source;
            }
        }
    }

    private static IEnumerable<SymbolReferenceNode> Symbols(AstNode node) => Nodes(node).OfType<SymbolReferenceNode>();

    /// <summary>Усі вузли дерева в порядку обходу.</summary>
    private static IEnumerable<AstNode> Nodes(AstNode node)
    {
        // ⛔ L7-01: лівий гребінь ланцюга — рекурсія глибиною в кількість ланок.
        RuntimeHelpers.EnsureSufficientExecutionStack();

        yield return node;

        IEnumerable<AstNode> children = node switch
        {
            BinaryNode binary => [binary.Left, binary.Right],
            UnaryNode unary => [unary.Operand],
            ConditionalNode conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
            FunctionNode function => function.Arguments,
            _ => [],
        };

        foreach (var child in children)
        {
            foreach (var found in Nodes(child))
            {
                yield return found;
            }
        }
    }
}
