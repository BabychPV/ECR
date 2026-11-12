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
internal sealed record CalculationRejectionReason(string? RuleCode, IReadOnlyList<CalculationRejectionColumn> Columns);

/// <summary>Що назвати людині: колонку цієї таблиці або вхід, якого серед колонок таблиці немає (AN-164b).</summary>
/// <param name="Code">Код колонки (перехід на комірку) або код аргументу формули.</param>
/// <param name="Name">
/// Заголовок колонки мовами каталогу (місячна колонка — з назвою місяця періоду); ПОРОЖНІЙ (без жодної мови) — це не
/// колонка таблиці, а вхід формули: підпис «вхідні дані «код»» будує клієнт ключем каталогу.
/// </param>
/// <param name="ColumnId">Колонка; <c>null</c> — не колонка таблиці.</param>
internal sealed record CalculationRejectionColumn(string Code, LocalizedText Name, int? ColumnId);

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
/// ✎ AN-164b (аудит RC #825, doc 96): для <c>MissingInputs</c> аргументи беруться з ПРОГОНУ (профіль актуального прогону,
/// <see cref="ModuleProfile.ReadMissingArguments"/>), здогад за формулами — лише запасний шлях для старих прогонів.
/// Аргумент, якого серед колонок таблиці немає, називається входом (порожній заголовок). Таблиця «місяці в колонках»
/// читається логічним зрізом, як у прогоні, а з місячних колонок лишається тільки місяць періоду (з його назвою).
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
    IColumnPathMapper? columnMapper,
    ICalculationResultStore? results = null)
{
    /// <summary>Скільки відхилених рядків читається одним зверненням до комірок (рядки × колонки таблиці).</summary>
    internal const int ReadChunkRows = 200;

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

        var argumentsOf = new Dictionary<(int Version, bool AllFormulas), IReadOnlySet<string>>();
        var rulesOf = new Dictionary<(int Version, int TemplateVersion), IReadOnlyList<CompiledMethodologyRule>>();

        // ⚡ 10.11 (людина: «продуктивність на великих навантаженнях»): комірки відхилених рядків читаються ОДНИМ
        // зверненням на таблицю (пачками по ReadChunkRows рядків), а не по зверненню на проблему — до MaxPerTable (1000)
        // проблем на таблицю давали б 1000 запитів на одне відкриття панелі.
        var resolved = new List<(CalculationIssueRow Issue, int VersionId, TableInstanceRef Instance, long RowId)>();
        foreach (var issue in rejected)
        {
            if (issue.MethodologyId is { } methodologyId
                && versionOf.TryGetValue(methodologyId, out var versionId)
                && instances.TryGetValue(issue.TableDefId, out var instance)
                && rowIds.TryGetValue(instance.TableInstanceId, out var keys)
                && keys.TryGetValue(issue.RowKey, out var rowId))
            {
                resolved.Add((issue, versionId, instance, rowId));
            }
        }

        var tablesOf = new Dictionary<int, TableDef?>();
        var cells = new Dictionary<CellAddress, CellValueData>();
        foreach (var group in resolved.GroupBy(r => r.Instance.TableInstanceId))
        {
            var first = group.First();
            var snapshot = await metadata.GetAsync(first.Instance.TemplateVersionId, ct).ConfigureAwait(false);
            var groupTable = snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == first.Issue.TableDefId);
            tablesOf[first.Issue.TableDefId] = groupTable;
            if (groupTable is null)
            {
                continue;
            }

            // AN-164b: «місяці в колонках» — ЛОГІЧНИЙ зріз (введені комірки з екземпляра-власника місяця), як читає прогін
            // (`CalculationInputBuilder` → `ReadSliceAsync`). Фізичні комірки перегляду тут порожні, і правило, що закрило
            // рядок у прогоні, класифікувалось би інакше («rule unknown», doc 96 @202610).
            if (groupTable.LayoutKind == TableLayoutKind.MonthsInColumns)
            {
                var wanted = group.Select(r => r.RowId).ToHashSet();
                foreach (var record in await cellStore.ReadSliceAsync(first.Instance.TableInstanceId, period, ct)
                             .ConfigureAwait(false))
                {
                    if (wanted.Contains(record.Address.TableRowId))
                    {
                        cells[new CellAddress(period, record.Address.TableRowId, record.Address.ColumnDefId)] = record.Value;
                    }
                }

                continue;
            }

            var columnIds = groupTable.Columns.Where(c => !c.IsDeleted).Select(c => c.Id).ToList();
            foreach (var chunk in group.Select(r => r.RowId).Distinct().Chunk(ReadChunkRows))
            {
                var read = await cellStore
                    .ReadCellsAsync([.. chunk.SelectMany(row => columnIds.Select(c => new CellAddress(period, row, c)))], ct)
                    .ConfigureAwait(false);
                foreach (var (address, value) in read)
                {
                    cells[address] = value;
                }
            }
        }

        // ⚡ Структура таблиці й джерела формул обчислюваних колонок — раз на таблицю, не на кожну з ~1000 проблем.
        var shapeOf = new Dictionary<int, (List<ColumnDef> Columns, Dictionary<string, ColumnDef> ByCode)>();
        var sourcesOf = new Dictionary<(int Table, int Column), List<ColumnDef>>();
        IReadOnlyDictionary<(long TableInstanceId, string RowKey, int? MethodologyId), IReadOnlyList<string>>? runArguments = null;

        // AN-164b: місячна колонка іншого місяця, ніж період, людині не потрібна — прогін її не читав.
        var month = period.Value % 100;
        bool OtherMonth(ColumnDef column)
            => column.IsMonthColumn && column.MonthNumber is { } number && month is >= 1 and <= 12 && number != month;

        // ⚠ Колонка без заголовка — підпис кодом: порожній заголовок у відповіді означає «вхід поза колонками».
        LocalizedText LabelOf(ColumnDef column)
            => column.HeaderL10n.Values.Count == 0
                ? new LocalizedText(new Dictionary<string, string> { ["en"] = column.Code })
                : column.IsMonthColumn && month is >= 1 and <= 12
                ? new LocalizedText(column.HeaderL10n.Values.ToDictionary(
                    v => v.Key,
                    v => $"{v.Value} · {Permits.Validity.PermitMonthCoverage.MonthName(month, v.Key)}",
                    StringComparer.OrdinalIgnoreCase))
                : column.HeaderL10n;

        foreach (var (issue, versionId, instance, rowId) in resolved)
        {
            if (tablesOf.GetValueOrDefault(issue.TableDefId) is not { } table)
            {
                continue;
            }

            if (!shapeOf.TryGetValue(table.Id, out var shape))
            {
                var all = table.Columns.Where(c => !c.IsDeleted).ToList();
                shape = (all, all
                    .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));
                shapeOf[table.Id] = shape;
            }

            var (columns, byCode) = shape;
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

            // 2. Порожні колонки, які згадує правило категорії. AN-164: рядок без входів (`MissingInputs`) — порожні
            //    колонки, які читають формули версії (будь-яка з них могла б дати значення).
            //    AN-164b: для `MissingInputs` — САМЕ ті аргументи, яких бракувало рядку в прогоні (профіль прогону,
            //    `ModuleProfile.ReadMissingArguments`); здогад за формулами — лише коли прогін їх не записав (старий прогін).
            var allFormulas = issue.Kind == ICalculationIssueStore.MissingInputs;
            IReadOnlyList<string>? fromRun = null;
            if (allFormulas)
            {
                runArguments ??= await RunArgumentsAsync(documentId, period, ct).ConfigureAwait(false);
                fromRun = runArguments.GetValueOrDefault((instance.TableInstanceId, issue.RowKey, issue.MethodologyId));
            }

            IEnumerable<string> arguments;
            if (fromRun is not null)
            {
                arguments = fromRun;
            }
            else
            {
                if (!argumentsOf.TryGetValue((versionId, allFormulas), out var guessed))
                {
                    guessed = await RuleArgumentsAsync(versionId, bounds.PeriodEnd, allFormulas, ct).ConfigureAwait(false);
                    argumentsOf[(versionId, allFormulas)] = guessed;
                }

                arguments = guessed;
            }

            var named = new List<ColumnDef>();
            var inputs = new List<string>();
            foreach (var argument in arguments)
            {
                if (!byCode.TryGetValue(argument, out var column))
                {
                    // Аргумент прогону, якого серед колонок таблиці немає (не та таблиця, довідник, похідний) — вхід.
                    if (fromRun is not null)
                    {
                        inputs.Add(argument);
                    }

                    continue;
                }

                if (OtherMonth(column) || TextOf(column).Length > 0)
                {
                    continue;
                }

                // Обчислювана колонка: людина заповнює джерела її формули. Джерела вже заповнені — колонка порахується
                // наступним перерахунком, називати нічого (сама вона лише для читання). Місячні джерела — лише місяця
                // періоду: формула шаблону бере один із 12 (doc 96 @202610: «Running hours per month» ×12).
                if (!sourcesOf.TryGetValue((table.Id, column.Id), out var sources))
                {
                    sources = [.. TemplateSources(table, column, byCode)];
                    sourcesOf[(table.Id, column.Id)] = sources;
                }

                named.AddRange(sources.Count > 0 ? sources.Where(s => !OtherMonth(s) && TextOf(s).Length == 0) : [column]);
            }

            var distinct = named.DistinctBy(c => c.Id).OrderBy(c => c.Ordinal).ThenBy(c => c.Id).ToList();

            // ⛔ Прихована колонка — жодної (див. remarks класу), і входів теж: перелік або повний, або порожній.
            result[(issue.TableDefId, issue.RowKey, issue.MethodologyId)] = new CalculationRejectionReason(
                ruleCode,
                distinct.All(c => canReadColumn(c.Id))
                    ?
                    [
                        .. distinct.Select(c => new CalculationRejectionColumn(c.Code, LabelOf(c), c.Id)),
                        .. inputs.Distinct(StringComparer.OrdinalIgnoreCase)
                            .Select(code => new CalculationRejectionColumn(code, new LocalizedText(), null)),
                    ]
                    : []);
        }

        return result;
    }

    /// <summary>
    /// AN-164b: аргументи, яких бракувало рядкам у прогоні, — з профілю актуального прогону документа за період (той
    /// самий прогін, що записав проблеми). Немає сховища чи профілю — порожньо (читання візьме здогад за формулами).
    /// </summary>
    private async Task<IReadOnlyDictionary<(long TableInstanceId, string RowKey, int? MethodologyId), IReadOnlyList<string>>>
        RunArgumentsAsync(long documentId, PeriodKey period, CancellationToken ct)
    {
        var run = results is null
            ? null
            : await results.ReadCurrentRunProfileAsync(documentId, period.Value, ct).ConfigureAwait(false);

        return ModuleProfile.ReadMissingArguments(run?.ModulesProfileJson, documentId);
    }

    /// <summary>
    /// Аргументи <c>@Code</c> правила категорії версії разом із формулами, до яких воно веде; з
    /// <paramref name="allFormulas"/> (AN-164, <see cref="ICalculationIssueStore.MissingInputs"/>) — усіх формул версії.
    /// </summary>
    private async Task<IReadOnlySet<string>> RuleArgumentsAsync(
        int versionId, DateOnly onDate, bool allFormulas, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rule = allFormulas ? null : await methodologies.GetCategoryRuleAsync(versionId, ct).ConfigureAwait(false);
        if (!allFormulas && string.IsNullOrWhiteSpace(rule))
        {
            return found;
        }

        var own = await methodologies.GetFormulasAsync(versionId, ct).ConfigureAwait(false);
        var formulas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var formula in own)
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
        if (allFormulas)
        {
            foreach (var formula in own)
            {
                visited.Add(formula.Code);
                pending.Push(formula.Expression);
            }
        }
        else
        {
            pending.Push(rule!);
        }

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
