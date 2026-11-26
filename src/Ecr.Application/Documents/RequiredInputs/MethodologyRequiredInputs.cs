// src/Ecr.Application/Documents/RequiredInputs/MethodologyRequiredInputs.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.RequiredInputs;

/// <summary>
/// Обов'язкові входи методологій (<c>ECR-CALC-0437</c>): які методології таблиці чинні на дату й що вони вимагають від
/// рядка. ✎ D-577 (L6): ВИНЕСЕНО без змін семантики з <c>PatchCellsHandler</c> — тепер тим самим обчислювачем
/// користуються ворота запису (PATCH, книга), перелік «чого бракує» на таблицю при відкритті
/// (<c>GET …/required-inputs</c>) і подання (Q6).
/// </summary>
/// <param name="methodologies">Сховище методологій.</param>
/// <param name="columnMapper">C1: переклад Id колонок на версію документа; <c>null</c> — Id локальні.</param>
/// <param name="rowActivity">D-425: фільтр активності рядків; <c>null</c> — фільтра немає.</param>
public sealed class MethodologyRequiredInputs(
    IMethodologyStore methodologies, IColumnPathMapper? columnMapper, IRowActivityFilterSource? rowActivity)
{
    /// <summary>
    /// Колонки для зіставлення правил (<c>MatchJson</c>) і перевірки вимог,
    /// разом — щоб прочитати їх ОДНИМ пакетним запитом на всі рядки батчу.
    /// </summary>
    internal static List<int> ColumnIds(IReadOnlyList<ApplicableMethodology> applicable)
        => applicable
            .SelectMany(a => a.RequiredInputs.Where(r => r.IsMapped).Select(r => r.ColumnDefId))
            .Concat(applicable.SelectMany(a => a.Rules.SelectMany(r => MethodologyRuleMatcher.ColumnIds(r.MatchJson))))
            .Concat(applicable.SelectMany(a => a.StatusColumnIds))
            .Distinct()
            .ToList();

    /// <summary>Вимоги методологій до ОДНОГО рядка — без звернень до бази.</summary>
    /// <param name="applicable">Чинні методології таблиці (<see cref="ResolveAsync"/>).</param>
    /// <param name="neededColumnIds">Колонки зіставлення й вимог (<see cref="ColumnIds"/>).</param>
    /// <param name="valueOf">Значення колонки рядка як текст; <c>null</c> — порожньо.</param>
    /// <param name="isFilled">Чи вимога заповнена (значення або дефолт колонки, C-5).</param>
    /// <param name="humanBatch">Блокує лише людині (F1-01); інтеграції — попередження.</param>
    internal static RequiredInputCollector EvaluateRow(
        IReadOnlyList<ApplicableMethodology> applicable,
        IReadOnlyList<int> neededColumnIds,
        Func<int, string?> valueOf,
        Func<int, bool> isFilled,
        bool humanBatch)
    {
        var matchValues = neededColumnIds.ToDictionary(
            columnId => columnId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            valueOf,
            StringComparer.Ordinal);

        // ✎ 25.11 (живий прохід, банер «2 required column(s)» із 10 рядками й повторами): вимоги рядка зводяться
        // ПО КОЛОНЦІ — одне повідомлення на (рядок, колонку) з переліком методологій, а не на кожну методологію;
        // повтор тієї самої методології (дві прив'язки, обидві гілки) — один код.
        var missing = new RequiredInputCollector();

        foreach (var applied in applicable)
        {
            // ⛔ D-425: рядок, який прогін відсіює (статус не активний або порожній), не має вимог: інакше
            // gate блокував би збереження через входи, які ніхто не прочитає.
            if (applied.StatusColumnIds.Count > 0
                && !RowActivityFilterSpec.Admits(
                    [.. applied.StatusColumnIds.Select(c => matchValues.GetValueOrDefault(
                        c.ToString(System.Globalization.CultureInfo.InvariantCulture)))],
                    applied.ActiveEntryIdText!))
            {
                continue;
            }

            // ✎ D-577 (perf, 174 тис. рядків): предикати розібрано раз на методологію (ParsedRules), не на рядок; семантика —
            // та сама, що MethodologyRuleMatcher.Matches (бите правило не збігається, порожнє — збігається з усім).
            var winner = applied.Rules
                .Where((rule, i) => applied.ParsedRules[i] is { } pairs
                                    && pairs.All(p => matchValues.TryGetValue(p.Key, out var actual)
                                                      && string.Equals(actual, p.Value, StringComparison.Ordinal)))
                .FirstOrDefault();

            if (winner is null && !applied.MatchesEveryRow)
            {
                // Варіант B (рішення людини 11.11): порожній обов'язковий вхід, що сам є КЛЮЧЕМ правил, — причина,
                // чому правило не збіглося; без цієї гілки його вимога не спрацьовувала ніколи.
                foreach (var required in MissingRuleKeyInputs(applied, matchValues))
                {
                    missing.Add(applied, required, humanBatch);
                }

                continue;
            }

            foreach (var required in applied.RequiredInputs)
            {
                // ⛔ C1: вимога посилається на колонку, якої немає в цій версії шаблону (шлях змінено/вилучено).
                // Тихо пропустити = тихо зняти вимогу; блокувати — назавжди заблокувати збереження через те, чого
                // користувач не може заповнити. Тому — явне ПОПЕРЕДЖЕННЯ без назви колонки й методології
                // (читач міг не мати права їх бачити); перенос проєкту на таку версію відмовляється заздалегідь.
                // ✎ D-578 (Q3): виняток методолога «Optional» — поле може бути порожнім.
                if (required.Severity == RequiredInputSeverity.Optional)
                {
                    continue;
                }

                if (!required.IsMapped)
                {
                    missing.Unmapped = true;
                    continue;
                }

                if (isFilled(required.ColumnDefId))
                {
                    continue;
                }

                missing.Add(applied, required, humanBatch);
            }
        }

        return missing;
    }

    /// <summary>
    /// Обов'язкові входи-КЛЮЧІ правил, порожні в рядку, з яким жодне правило методології не збіглося (варіант B).
    /// </summary>
    /// <remarks>
    /// Рядок вважається в межах методології, якщо є правило, для якого одночасно:
    /// (1) кожен ЗАПОВНЕНИЙ ключ дорівнює очікуваному (розбіжність — рядок іншої методології/варіанта, тиша як і до
    /// фіксу); (2) бодай один ключ порожній і він зареєстрований як вимога — саме його й названо;
    /// (3) рядок «розпочато»: методологія має фільтр активності D-425 (рядок сюди дійшов, отже його пройшов), або
    /// заповнено бодай один ключ правила, що сам НЕ є вимогою (напр. статус). Умова (3) — проти дедлоку поклітинного
    /// введення: ключі-вимоги можна вводити в будь-якому порядку, поки рядок не віднесено до методології.
    /// Звичайні (не-ключові) вимоги без збігу правила не перевіряються — як і до фіксу. Правило з ключем без
    /// відповідника у версії документа (C1) пропускається: воно не збіжеться ніколи. Без звернень до бази: ключі
    /// правил уже в <c>neededColumnIds</c> (пакетне читання базового стану).
    /// </remarks>
    internal static IEnumerable<LocalizedRequiredInput> MissingRuleKeyInputs(
        ApplicableMethodology applied, IReadOnlyDictionary<string, string?> matchValues)
    {
        var requiredByKey = new Dictionary<string, LocalizedRequiredInput>(StringComparer.Ordinal);
        foreach (var required in applied.RequiredInputs.Where(r => r.IsMapped))
        {
            requiredByKey.TryAdd(
                required.ColumnDefId.ToString(System.Globalization.CultureInfo.InvariantCulture), required);
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < applied.Rules.Count; index++)
        {
            var rule = applied.Rules[index];
            if (rule.UnmappedColumnIds.Count > 0
                || applied.ParsedRules[index] is not { Count: > 0 } pairs)
            {
                continue;
            }

            var missing = new List<string>();
            var started = applied.StatusColumnIds.Count > 0;
            var mismatch = false;
            foreach (var (key, expected) in pairs)
            {
                var actual = matchValues.GetValueOrDefault(key);
                if (actual is null)
                {
                    missing.Add(key);
                }
                else if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    mismatch = true;
                    break;
                }
                else if (!requiredByKey.ContainsKey(key))
                {
                    started = true;
                }
            }

            if (mismatch || !started)
            {
                continue;
            }

            foreach (var key in missing)
            {
                if (requiredByKey.TryGetValue(key, out var required) && reported.Add(key))
                {
                    yield return required;
                }
            }
        }
    }

    /// <summary>
    /// Те саме для кількох методологій — пакетом: версії, правила, вимоги й коди читаються ОДНИМ зверненням на
    /// вид даних (≤ 4 на будь-яке число методологій), а не 3-5 на кожну.
    /// </summary>
    /// <remarks>
    /// ⛔ Семантика поштучного шляху не змінюється: вибір версії той самий, методологія без чинної версії, без
    /// активних правил або без вимог пропускається; порядок результату — порядок <paramref name="methodologyIds"/>.
    /// Кешу тут немає навмисно: gate запису читає методологію на кожен запис
    /// (<c>MethodologyRequiredColumnsCache</c>, зауваження «стеля несвіжості»).
    /// </remarks>
    internal async Task<List<ApplicableMethodology>> ResolveAsync(
        IReadOnlyList<int> methodologyIds, DateOnly onDate, int templateVersionId, int tableDefId, CancellationToken ct,
        TableDef? derivedFor = null)
    {
        var result = new List<ApplicableMethodology>();
        if (methodologyIds.Count == 0)
        {
            return result;
        }

        var published = (await methodologies.GetPublishedVersionsOfAsync(methodologyIds, ct).ConfigureAwait(false))
            .ToLookup(v => v.MethodologyId);

        var chosen = new List<(int MethodologyId, MethodologyVersion Version)>();
        foreach (var methodologyId in methodologyIds)
        {
            // ⚠ Те саме правило вибору, що й `MethodologyResolver.ResolveVersionAsync`
            // (пізніша `EffectiveFrom` → старша версія → більший Id) — той самий
            // компаратор домену, не друга копія.
            var version = published[methodologyId]
                .Where(v => v.EffectiveFrom is not null && v.EffectiveFrom <= onDate)
                .OrderByDescending(
                    v => new MethodologyVersionKey(v.EffectiveFrom, v.Version, v.Id),
                    MethodologyVersionKey.Currency)
                .FirstOrDefault();

            if (version is not null)
            {
                chosen.Add((methodologyId, version));
            }
        }

        if (chosen.Count == 0)
        {
            return result;
        }

        var versionIds = chosen.Select(c => c.Version.Id).Distinct().ToList();
        var rulesByVersion = (await methodologies.GetRulesOfVersionsAsync(versionIds, ct).ConfigureAwait(false))
            .ToLookup(r => r.MethodologyVersionId);

        // Версія без активних правил не перевіряє нічого — вимоги для неї не питаються (як і поштучно).
        // ✎ P-1124-1 (UI-прохід редактора скрипта, 24.11): ВЕРСІЯ-СКРИПТ правил відбору не має — рядки вибирає сам
        // скрипт (`rows from …`), і прив'язана таблиця вся в її межах. Без цього винятку вимога, заведена у вкладці
        // «Required columns» нової версії-скрипта, не блокувала й не попереджала НІКОЛИ (gate тихо пропускав рядок).
        var scriptWithoutRules = chosen
            .Where(c => c.Version.Engine == MethodologyEngine.Script && !rulesByVersion[c.Version.Id].Any())
            .Select(c => c.Version.Id)
            .ToHashSet();
        var withRules = versionIds.Where(id => rulesByVersion[id].Any() || scriptWithoutRules.Contains(id)).ToList();
        if (withRules.Count == 0)
        {
            return result;
        }

        var inputsByVersion = (await methodologies.GetRequiredInputsOfVersionsAsync(withRules, ct).ConfigureAwait(false))
            .ToLookup(r => r.MethodologyVersionId);

        // ✎ D-578 (рішення людини 26.11): вимоги, ВИВЕДЕНІ з полів, які читає скрипт версії (лише коли викликач передав
        // таблицю: перелік при відкритті, «Перевірити», подання; ворота PATCH їх не мають — збереження комірки не блокується).
        var derived = derivedFor is null
            ? new Dictionary<int, IReadOnlyList<int>>()
            : await DerivedColumnsAsync(withRules, derivedFor, ct).ConfigureAwait(false);

        var gated = withRules.Where(id => inputsByVersion[id].Any() || derived.ContainsKey(id)).ToHashSet();
        if (gated.Count == 0)
        {
            return result;
        }

        // Код методології — для тексту вимоги; раніше `FindByVersionAsync` (агрегат з усіма версіями, 2 звернення
        // на методологію, ще й у відстеженні контексту запису), тепер підпис версії одним зверненням.
        var labels = await methodologies.GetVersionLabelsAsync([.. gated], ct).ConfigureAwait(false);

        // ⛔ C1: переклад Id колонок на версію шаблону документа — ОДНИМ зверненням на всі версії: переклад кожного Id
        // не залежить від сусідів у наборі (шлях аркуш/таблиця/колонка), тож підмножина спільної відповіді — рівно те,
        // що дав би окремий виклик.
        var mapper = await PrefetchedColumnMapper
            .CreateAsync(
                columnMapper,
                templateVersionId,
                gated.SelectMany(id => rulesByVersion[id].SelectMany(r => MethodologyRuleMatcher.ColumnIds(r.MatchJson))
                    .Concat(inputsByVersion[id].Select(i => i.ColumnDefId))),
                ct)
            .ConfigureAwait(false);

        // D-425: фільтри активності — одним читанням прапорців на всі методології, що дійшли до перевірки.
        var activities = rowActivity is null
            ? null
            : await rowActivity
                .ResolveForMethodologiesAsync(
                    [.. chosen.Where(c => gated.Contains(c.Version.Id)).Select(c => c.MethodologyId).Distinct()],
                    [tableDefId],
                    ct)
                .ConfigureAwait(false);

        foreach (var (methodologyId, version) in chosen)
        {
            if (!gated.Contains(version.Id))
            {
                continue;
            }

            // ⛔ C1: Id у правилах і вимогах — колонки тієї версії шаблону, де їх писали; переклад на колонки версії
            // ДОКУМЕНТА (за шляхом аркуш/таблиця/колонка) — до будь-якого зіставлення з комірками.
            var localized = await MethodologyKeyLocalizer
                .LocalizeAsync(
                    mapper, templateVersionId, [.. rulesByVersion[version.Id]], [.. inputsByVersion[version.Id]], ct)
                .ConfigureAwait(false);

            // D-425: колонки статусу таблиці (за довідником із прапорця методології); порожньо — фільтр не діє.
            var activity = activities?.GetValueOrDefault(methodologyId);
            var statusColumns = activity?.ColumnsOf(tableDefId) ?? [];

            // ✎ D-578: виведені — лише колонки, яких немає в переліку методолога (його Block/Warn/Optional — виняток, Q3).
            var declared = localized.RequiredInputs.Where(i => i.IsMapped).Select(i => i.ColumnDefId).ToHashSet();
            IReadOnlyList<LocalizedRequiredInput> requiredInputs =
            [
                .. localized.RequiredInputs,
                .. (derived.GetValueOrDefault(version.Id) ?? [])
                    .Where(c => !declared.Contains(c))
                    .Select(c => new LocalizedRequiredInput(c, c, true, RequiredInputSeverity.Block, null, Derived: true)),
            ];

            result.Add(new ApplicableMethodology(
                labels.TryGetValue(version.Id, out var label)
                    ? label.MethodologyCode
                    : methodologyId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                localized.Rules,
                requiredInputs,
                statusColumns,
                activity?.ActiveEntryIdText,
                MatchesEveryRow: scriptWithoutRules.Contains(version.Id)));
        }

        return result;
    }

    /// <summary>
    /// ✎ D-578: колонки таблиці <paramref name="table"/>, які читає скрипт кожної версії (за кодом поля), — одним зверненням на
    /// всі версії. Версія без запису (скрипт не дозаписано), з маркером «читає будь-що» (<c>*</c> — скрипт не скомпілювався)
    /// чи без полів — без виведених вимог (лише перелік методолога). Поля за ІНШИЙ місяць і поля інших таблиць — поза межами
    /// (етап 2).
    /// </summary>
    internal async Task<Dictionary<int, IReadOnlyList<int>>> DerivedColumnsAsync(
        IReadOnlyCollection<int> versionIds, TableDef table, CancellationToken ct)
    {
        var result = new Dictionary<int, IReadOnlyList<int>>();
        if (versionIds.Count == 0)
        {
            return result;
        }

        var byCode = table.Columns
            .Where(c => !c.IsDeleted)
            .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        foreach (var version in (await methodologies.GetReadFieldsOfVersionsAsync(versionIds, ct).ConfigureAwait(false))
                     .GroupBy(f => f.MethodologyVersionId))
        {
            if (version.Any(f => f.FieldCode == MethodologyReadField.AllFields))
            {
                continue;
            }

            var columns = version
                .Where(f => f.FieldCode.Length > 0 && !f.OtherMonths && byCode.ContainsKey(f.FieldCode))
                .Select(f => byCode[f.FieldCode])
                .Distinct()
                .Order()
                .ToList();
            if (columns.Count > 0)
            {
                result[version.Key] = columns;
            }
        }

        return result;
    }


    /// <summary>Значення комірки як текст — та сама умова «заповнено», що й у зіставленні правил.</summary>
    /// <remarks>
    /// ⛔ Обробляються ВСІ шість полів <c>CellValueData</c>, як і в
    /// <c>Describe()</c> (аудит 2026-09-16, §3.1). Бракувало
    /// <c>ValueDate</c>/<c>ValueUnitId</c>, і це давало два тихі дефекти
    /// одночасно. Перший: методологія позначає Date-колонку як
    /// <c>RequiredInput</c>/Block — <c>valueOf()</c> завжди бачив <c>null</c>,
    /// тож рядок БЛОКУВАВСЯ НАЗАВЖДИ (`ECR-CALC-0437` на кожній спробі), навіть
    /// коли комірка заповнена. Другий, дзеркальний: <c>MethodologyRule.MatchJson</c>
    /// визначає гілку за Date/Unit-колонкою — <c>matchValues</c> для неї завжди
    /// <c>null</c>, і правило обов'язковості тихо НЕ спрацьовувало ніколи.
    /// </remarks>
    internal static string? Text(CellValueData value)
        => value.ValueString
           ?? value.ValueNumeric?.ToString(System.Globalization.CultureInfo.InvariantCulture)
           ?? value.ValueRegistryEntryId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
           ?? value.ValueBool?.ToString()
           ?? value.ValueDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
           ?? value.ValueUnitId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// ✎ 25.11: незаповнені обов'язкові входи ОДНОГО рядка, зведені по колонці (порядок — перша поява).
/// </summary>
/// <remarks>
/// Текст — мовою читача (<c>validation.methodology.requiredInput*</c>, <see cref="Validation.ValidationMessageTemplates"/>):
/// до фіксу він був українським літералом із кодом колонки за будь-якої мови інтерфейсу, а власна підказка
/// методології бралась лише англійською. Колонка — заголовком мовою читача, як у зрізі
/// (<c>GetTableSliceHandler</c>: <c>HeaderL10n.Get(language) ?? Code</c>).
/// </remarks>
internal sealed class RequiredInputCollector
{
    private readonly List<(LocalizedRequiredInput Required, List<string> Codes, bool Error, bool SaveBlocks)> columns = [];

    /// <summary>Вимога з колонкою поза версією шаблону (C1) — одне попередження на рядок.</summary>
    public bool Unmapped { get; set; }

    public void Add(ApplicableMethodology applied, LocalizedRequiredInput required, bool humanBatch)
    {
        // ✎ D-578: Block (і заведений, і виведений) — помилка (блокує подання); збереження комірки блокує лише ЗАВЕДЕНИЙ Block.
        var error = required.Severity == RequiredInputSeverity.Block && humanBatch;
        var saveBlocks = error && !required.Derived;
        var index = columns.FindIndex(c => c.Required.ColumnDefId == required.ColumnDefId);
        if (index < 0)
        {
            columns.Add((required, [applied.MethodologyCode], error, saveBlocks));
            return;
        }

        var entry = columns[index];
        if (!entry.Codes.Contains(applied.MethodologyCode, StringComparer.Ordinal))
        {
            entry.Codes.Add(applied.MethodologyCode);
        }

        // Будь-яка методологія з Block блокує колонку; підказка — першої, що її має.
        columns[index] = (entry.Required.HintL10n is null && required.HintL10n is not null ? required : entry.Required,
            entry.Codes, entry.Error || error, entry.SaveBlocks || saveBlocks);
    }

    public IEnumerable<Validation.ValidationMessage> Messages(
        int tableDefId, IReadOnlyDictionary<int, ColumnDef> columnsById, string rowKey, string? language)
        => Messages(tableDefId, columnsById, rowKey, language, rendered: null);

    /// <summary>
    /// Те саме з пам'яттю відрендерених повідомлень (✎ D-577 perf): у переліку на всю таблицю повідомлення однакові для всіх
    /// рядків, окрім ключа рядка, — текст збирається раз на (колонка, методології, рівень, підказка), а не на кожен рядок.
    /// </summary>
    public IEnumerable<Validation.ValidationMessage> Messages(
        int tableDefId, IReadOnlyDictionary<int, ColumnDef> columnsById, string rowKey, string? language,
        Dictionary<(int Column, string Codes, bool Error, bool Blocks, LocalizedText? Hint), Validation.ValidationMessage>? rendered)
    {
        if (Unmapped)
        {
            yield return new Validation.ValidationMessage(
                ValidationSeverity.Warning,
                "ECR-CALC-0437",
                Validation.ValidationMessageTemplates.Render(
                    Validation.ValidationMessageTemplates.RequiredInputUnmapped, language, EmptyParams),
                tableDefId,
                rowKey,
                ColumnCode: null,
                BlocksSave: false,
                MessageKey: Validation.ValidationMessageTemplates.RequiredInputUnmapped,
                Params: EmptyParams);
        }

        foreach (var (required, codes, error, blocks) in columns)
        {
            var memo = (required.ColumnDefId, string.Join('', codes), error, blocks, required.HintL10n);
            if (rendered is not null && rendered.TryGetValue(memo, out var known))
            {
                yield return known with { RowKey = rowKey };
                continue;
            }

            var column = columnsById.GetValueOrDefault(required.ColumnDefId);
            var columnCode = column?.Code
                             ?? required.ColumnDefId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["column"] = column?.HeaderL10n?.Get(language ?? "en") ?? columnCode,
                ["methodologies"] = string.Join(", ", codes),
            };
            var key = codes.Count == 1
                ? Validation.ValidationMessageTemplates.RequiredInputOne
                : Validation.ValidationMessageTemplates.RequiredInputMany;
            var hint = required.HintL10n?.Get(language ?? "en");

            var message = new Validation.ValidationMessage(
                error ? ValidationSeverity.Error : ValidationSeverity.Warning,
                "ECR-CALC-0437",
                hint ?? Validation.ValidationMessageTemplates.Render(key, language, parameters),
                tableDefId,
                rowKey,
                columnCode,
                BlocksSave: blocks,
                MessageKey: hint is null ? key : null,
                Params: hint is null ? parameters : null,
                Methodologies: [.. codes]);
            rendered?.TryAdd(memo, message);
            yield return message;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyParams = new Dictionary<string, string>();
}

/// <summary>
/// Методологія, чия версія на дату дійсно має що перевіряти в рядку. <c>MatchesEveryRow</c> — версія-скрипт без
/// правил відбору: кожен рядок прив'язаної таблиці в її межах (рядки вибирає скрипт).
/// </summary>
public sealed record ApplicableMethodology(
    string MethodologyCode,
    IReadOnlyList<LocalizedRule> Rules,
    IReadOnlyList<LocalizedRequiredInput> RequiredInputs,
    IReadOnlyList<int> StatusColumnIds,
    string? ActiveEntryIdText,
    bool MatchesEveryRow = false)
{
    private IReadOnlyList<IReadOnlyList<KeyValuePair<string, string?>>?>? _parsedRules;

    /// <summary>✎ D-577 (perf): пари предикатів правил (<see cref="MethodologyRuleMatcher.CanonicalPairs"/>), розібрані раз.</summary>
    internal IReadOnlyList<IReadOnlyList<KeyValuePair<string, string?>>?> ParsedRules
        => _parsedRules ??= [.. Rules.Select(r => MethodologyRuleMatcher.CanonicalPairs(r.MatchJson))];
}
