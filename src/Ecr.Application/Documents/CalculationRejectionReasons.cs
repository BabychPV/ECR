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
/// ✎ AN-164c (приймання F13 #834, T7b_00): місячна колонка — і та, чий код є назвою місяця (<c>January…December</c>).
/// Проблема <c>MissingInputs</c> не лишається без переліку: без аргументів прогону — входи поза колонками зі здогаду,
/// далі колонки, які читає методологія за місяць періоду (навіть заповнені), далі колонки введення місяця періоду.
/// </para>
/// <para>
/// ⛔ Безпека: хоч одна названа колонка прихована від читача — колонок немає зовсім (як у підказках).
/// </para>
/// <para>
/// ⛔ Q-17 (LAND-YEAR G5): значення рядка — ЛОГІЧНІ, як у сітці. У таблиці «місяці в колонках» колонка з іншим власником
/// (не-місячна — якір року, M{m} — період m) лежить в екземплярі ВЛАСНИКА; фізичне читання екземпляра перегляду давало
/// порожні статус і ключі правил у не-власних місяцях — правило «не збігалося», а заповнена колонка називалась порожньою.
/// Тепер, як <c>PatchCellsHandler.PlanOwnerBaselineAsync</c>: власник — <see cref="IMonthColumnRouter.ForeignColumnsAsync"/>,
/// рядок власника — той самий <c>RowKey</c>, комірки власників — у ТОМУ САМОМУ пакетному <c>ReadCellsAsync</c>; фізичне
/// введене значення чужої колонки в екземплярі перегляду «затінене», обчислене зі значенням — лишається. Значення
/// власника береться ЛИШЕ для колонки, яку читач бачить на логічній адресі І в періоді-власнику (D-214); інакше колонка
/// для правила порожня (лише звужує). Маршрутизація вимкнена, копія аркуша, таблиця поза Q-17 — читання як було, без
/// звернень до бази (таблиця «місяці в колонках» — зрізом AN-164b); інакше на таблицю +1 запит екземплярів року і по
/// 1 запиту рядків на період-власник (≤ 13), а зріз AN-164b не читається (план Q-17 його заміняє).
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
    ICalculationResultStore? results = null,
    IMonthColumnRouter? monthRouter = null)
{
    /// <summary>Скільки відхилених рядків читається одним зверненням до комірок (рядки × колонки таблиці).</summary>
    internal const int ReadChunkRows = 200;

    /// <summary>Будує причини для відхилених рядків.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="period">Період.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <param name="rejected">Видимі проблеми виду <see cref="ICalculationIssueStore.CategoryRuleRejected"/>.</param>
    /// <param name="canReadColumn">Чи бачить читач колонку.</param>
    /// <param name="canReadColumnInOwner">
    /// Q-17: чи бачить читач колонку в періоді-власнику (межа D-214, <c>InPeriod(owner)</c>); <c>false</c> — значення
    /// власника не береться.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>(TableDefId, RowKey, MethodologyId) → причина; немає ключа — причину не виведено.</returns>
    public async Task<IReadOnlyDictionary<(int TableDefId, string RowKey, int? MethodologyId), CalculationRejectionReason>>
        BuildAsync(
            long documentId,
            PeriodKey period,
            long? sheetCopyId,
            IReadOnlyList<CalculationIssueRow> rejected,
            Func<int, bool> canReadColumn,
            Func<PeriodKey, int, bool> canReadColumnInOwner,
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

            // Q-17 (G5): план логічного читання — ПЕРШИМ. Є план (роутер увімкнено, оригінал, у періоду перегляду є
            // колонки з іншим власником) — значення власників збираються нижче пакетним ReadCellsAsync з межею D-214
            // періоду-власника; логічний зріз декоратора (`MonthRoutedCellStore`) цієї межі не знає.
            var columnIds = groupTable.Columns.Where(c => !c.IsDeleted).Select(c => c.Id).ToList();
            var owners = await PlanOwnerReadAsync(
                    documentId, period, sheetCopyId, groupTable,
                    [.. group.Select(r => (r.RowId, r.Issue.RowKey)).Distinct()],
                    canReadColumn, canReadColumnInOwner, ct)
                .ConfigureAwait(false);

            // AN-164b: «місяці в колонках» — ЛОГІЧНИЙ зріз (введені комірки з екземпляра-власника місяця), як читає прогін
            // (`CalculationInputBuilder` → `ReadSliceAsync`). Фізичні комірки перегляду тут порожні, і правило, що закрило
            // рядок у прогоні, класифікувалось би інакше («rule unknown», doc 96 @202610). ✎ G5: лише коли плану Q-17
            // немає (роутер вимкнено/відсутній, копія аркуша, період перегляду — сам власник) — тоді, як і досі.
            if (owners is null && groupTable.LayoutKind == TableLayoutKind.MonthsInColumns)
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

            foreach (var chunk in group.Select(r => r.RowId).Distinct().Chunk(ReadChunkRows))
            {
                var addresses = chunk.SelectMany(row => columnIds.Select(c => new CellAddress(period, row, c))).ToList();
                List<(CellAddress Owner, CellAddress Logical)> ownerPairs = owners is null
                    ? []
                    : [.. chunk.SelectMany(row => owners.ByRow.TryGetValue(row, out var pairs) ? pairs : [])];

                var read = await cellStore
                    .ReadCellsAsync([.. addresses, .. ownerPairs.Select(p => p.Owner)], ct)
                    .ConfigureAwait(false);

                if (owners is null)
                {
                    foreach (var (address, value) in read)
                    {
                        cells[address] = value;
                    }

                    continue;
                }

                // Логічна розкладка, як MonthRoutedCellStore: фізичне введене значення чужої колонки затінене власником,
                // обчислене зі значенням — лишається й приховує власника.
                foreach (var address in addresses)
                {
                    if (read.TryGetValue(address, out var value)
                        && (!owners.Shadowed.Contains(address.ColumnDefId)
                            || (value.IsCalculated && MethodologyRuleMatcher.Text(value) is not null)))
                    {
                        cells[address] = value;
                    }
                }

                foreach (var (owner, logical) in ownerPairs)
                {
                    if (read.TryGetValue(owner, out var value))
                    {
                        cells.TryAdd(logical, value);
                    }
                }
            }
        }

        // ⚡ Структура таблиці й джерела формул обчислюваних колонок — раз на таблицю, не на кожну з ~1000 проблем.
        var shapeOf = new Dictionary<int, (List<ColumnDef> Columns, Dictionary<string, ColumnDef> ByCode)>();
        var sourcesOf = new Dictionary<(int Table, int Column), List<ColumnDef>>();
        IReadOnlyDictionary<(long TableInstanceId, string RowKey, int? MethodologyId), IReadOnlyList<string>>? runArguments = null;

        // AN-164b: місячна колонка іншого місяця, ніж період, людині не потрібна — прогін її не читав.
        // AN-164c: місяць колонки — прив'язка (IsMonthColumn) або сам код-назва місяця (T7b_00: January…December).
        var month = period.Value % 100;
        bool OtherMonth(ColumnDef column)
            => MonthOf(column) is { } number && month is >= 1 and <= 12 && number != month;

        // ⚠ Колонка без заголовка — підпис кодом: порожній заголовок у відповіді означає «вхід поза колонками».
        // Колонка, чий код — назва місяця, уже називає місяць заголовком: без другої назви місяця.
        LocalizedText LabelOf(ColumnDef column)
            => column.HeaderL10n.Values.Count == 0
                ? new LocalizedText(new Dictionary<string, string> { ["en"] = column.Code })
                : column.IsMonthColumn && MonthByName(column.Code) is null && month is >= 1 and <= 12
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

            var argumentList = arguments.ToList();
            var named = new List<ColumnDef>();
            var inputs = new List<string>();
            Collect(onlyEmpty: true, nonColumnInputs: fromRun is not null);

            // AN-164c (приймання F13 #834, doc 96 T7b_00 S1_6/S2_7/S3_9 → columns = null): рядок, закритий правилом без
            // жодного значення, мусить назвати, що заповнити. Прогону з аргументами може не бути (аркушний перерахунок
            // іншого аркуша лишив проблеми таблиці від попереднього прогону, а профіль актуального прогону їх не має), а
            // здогад за формулами для T7b_00 не дає жодної колонки таблиці (аргументи формули — не колонки; місяці названо
            // January…December). Тоді — по черзі, доки є що назвати:
            //   1) аргументи, яких серед колонок таблиці немає, — входами (рядок їх не має за побудовою: аргумент = код
            //      колонки, CalculationInputBuilder);
            //   2) колонки, які читає методологія за місяць періоду, навіть заповнені (що перевірити);
            //   3) колонки введення місяця періоду (місячні або названі місяцем), спершу порожні.
            if (allFormulas && named.Count == 0 && inputs.Count == 0)
            {
                Collect(onlyEmpty: true, nonColumnInputs: true);
            }

            if (allFormulas && named.Count == 0 && inputs.Count == 0)
            {
                Collect(onlyEmpty: false, nonColumnInputs: true);
            }

            if (allFormulas && named.Count == 0 && inputs.Count == 0)
            {
                var monthInputs = columns
                    .Where(c => month is >= 1 and <= 12 && MonthOf(c) == month && !c.IsComputed && !c.IsReadOnly)
                    .ToList();
                var empty = monthInputs.Where(c => TextOf(c).Length == 0).ToList();
                named.AddRange(empty.Count > 0 ? empty : monthInputs);
            }

            void Collect(bool onlyEmpty, bool nonColumnInputs)
            {
                foreach (var argument in argumentList)
                {
                    if (!byCode.TryGetValue(argument, out var column))
                    {
                        // Аргумент, якого серед колонок таблиці немає (не та таблиця, довідник, похідний) — вхід.
                        if (nonColumnInputs)
                        {
                            inputs.Add(argument);
                        }

                        continue;
                    }

                    if (OtherMonth(column) || (onlyEmpty && TextOf(column).Length > 0))
                    {
                        continue;
                    }

                    // Обчислювана колонка: людина заповнює джерела її формули. Джерела вже заповнені — колонка
                    // порахується наступним перерахунком, називати нічого (сама вона лише для читання). Місячні джерела —
                    // лише місяця періоду: формула шаблону бере один із 12 (doc 96 @202610: «Running hours per month» ×12).
                    if (!sourcesOf.TryGetValue((table.Id, column.Id), out var sources))
                    {
                        sources = [.. TemplateSources(table, column, byCode)];
                        sourcesOf[(table.Id, column.Id)] = sources;
                    }

                    named.AddRange(sources.Count > 0
                        ? sources.Where(s => !OtherMonth(s) && (!onlyEmpty || TextOf(s).Length == 0))
                        : [column]);
                }
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

    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    /// <summary>Місяць колонки: прив'язка <see cref="ColumnDef.MonthNumber"/>, інакше код — назва місяця (AN-164c).</summary>
    private static int? MonthOf(ColumnDef column)
        => column.IsMonthColumn && column.MonthNumber is { } number ? number : MonthByName(column.Code);

    /// <summary>Номер місяця за кодом колонки — повна англійська назва або перші три літери; інакше <c>null</c>.</summary>
    private static int? MonthByName(string code)
    {
        for (var i = 0; i < MonthNames.Length; i++)
        {
            if (string.Equals(code, MonthNames[i], StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, MonthNames[i][..3], StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return null;
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

    /// <summary>Q-17: як читати логічні значення рядків однієї таблиці.</summary>
    /// <param name="Shadowed">Колонки з іншим власником: їхнє введене фізичне значення в екземплярі перегляду затінене.</param>
    /// <param name="ByRow">Рядок перегляду → пари (адреса у власника, логічна адреса) — лише видимих читачеві колонок.</param>
    private sealed record OwnerReadPlan(
        IReadOnlySet<int> Shadowed, IReadOnlyDictionary<long, List<(CellAddress Owner, CellAddress Logical)>> ByRow);

    /// <summary>
    /// Q-17 (G5): план логічного читання таблиці «місяці в колонках» у періоді, що не володіє частиною колонок
    /// (див. remarks класу). <c>null</c> — читання фізичне, як було (без звернень до бази за вимкненого роутера).
    /// </summary>
    private async Task<OwnerReadPlan?> PlanOwnerReadAsync(
        long documentId,
        PeriodKey period,
        long? sheetCopyId,
        TableDef table,
        IReadOnlyList<(long RowId, string RowKey)> rows,
        Func<int, bool> canReadColumn,
        Func<PeriodKey, int, bool> canReadColumnInOwner,
        CancellationToken ct)
    {
        // RS: копії аркушів Q-17 не маршрутизуються — їхні значення в їхніх комірках.
        if (monthRouter is not { Enabled: true } || sheetCopyId is not null || !monthRouter.AppliesToTable(table))
        {
            return null;
        }

        var document = await monthRouter.GetDocumentAsync(documentId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var foreign = (await monthRouter.ForeignColumnsAsync(document, table, period, ct).ConfigureAwait(false))
            .Where(f => f.OwnerPeriod != period)
            .ToList();
        if (foreign.Count == 0)
        {
            return null;
        }

        var byRow = new Dictionary<long, List<(CellAddress Owner, CellAddress Logical)>>();

        // ⛔ Лише звужує: значення власника — тільки колонкам, видимим на логічній адресі І в періоді-власнику.
        var readable = foreign
            .Where(f => canReadColumn(f.Column.Id) && canReadColumnInOwner(f.OwnerPeriod, f.Column.Id))
            .ToList();
        if (readable.Count > 0)
        {
            var instances = (await rowStore
                    .GetTableInstancesOfTableInYearAsync(documentId, table.Id, period.Year, ct)
                    .ConfigureAwait(false))
                .Where(i => i.SheetCopyId is null)
                .GroupBy(i => i.PeriodKey)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var byOwner in readable.GroupBy(f => f.OwnerPeriod).OrderBy(g => g.Key.Value))
            {
                if (!instances.TryGetValue(byOwner.Key.Value, out var instance))
                {
                    continue; // екземпляра власника немає — у сітці ці колонки порожні
                }

                var ownerRows = await rowStore.GetRowIdsAsync(instance.TableInstanceId, byOwner.Key, ct).ConfigureAwait(false);
                foreach (var (rowId, rowKey) in rows)
                {
                    if (!ownerRows.TryGetValue(rowKey, out var ownerRowId))
                    {
                        continue;
                    }

                    if (!byRow.TryGetValue(rowId, out var pairs))
                    {
                        byRow[rowId] = pairs = [];
                    }

                    foreach (var column in byOwner)
                    {
                        pairs.Add((new CellAddress(byOwner.Key, ownerRowId, column.Column.Id),
                            new CellAddress(period, rowId, column.Column.Id)));
                    }
                }
            }
        }

        return new OwnerReadPlan(foreign.Select(f => f.Column.Id).ToHashSet(), byRow);
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
