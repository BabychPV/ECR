// tools/Ecr.MethodologyScriptMigration/Flaring/SgFlaringBinding.cs
using System.Text;
using System.Text.RegularExpressions;
using Ecr.Application.Registries.Flert;

namespace Ecr.MethodologyScriptMigration.Flaring;

/// <summary>Споживач складу газу серед методологій факелів кислого газу (DESIGN-sg-flaring §4.2, §5.6).</summary>
public enum SgFlaringConsumer
{
    /// <summary>Періодичні факели, суша (стара HSE300_Int_SG, TypeReportID 300).</summary>
    IntOnshore,

    /// <summary>Періодичні факели, острови A/D (стара HSE30X_Int_SG, TypeReportID 301/302).</summary>
    IntOffshore,

    /// <summary>Постійні факели, сухопутні LP-аналізатори (стара HSE300_Cont_SG, потік 416 = id 317).</summary>
    ContOnshore,

    /// <summary>
    /// Постійні факели, LP-аналізатори острова D (стара HSE30X_Cont_SG): ОДНА методологія, рядок таблиці несе слот 1/2/3
    /// розподілу 19/21/146 (рішення людини 26.11).
    /// </summary>
    ContOffshore,

    /// <summary>Термальний окисник, SG-частина (стара HSE_TO_SG_&lt;SOR|EOR&gt;), рядок = поїзд × режим × день × стовпець газу.</summary>
    ToxDaily,

    /// <summary>
    /// Термальний окисник, місячна SG-частина (DESIGN §5.4, <c>TOX_SG_MONTHLY</c>): статичні рядки <c>TOX_MONTHLY</c> (поїзд × режим ×
    /// тип × стовпець) з датою <c>MONTH</c> (1-ше число), входи — SUM денних обсягів (script §13); склад — на 1-ше число місяця.
    /// </summary>
    ToxMonthly,
}

/// <summary>
/// ✎ SGF-flaring (H8, H9, H11; DESIGN-sg-flaring §5.1–§5.6; D-580): «міст GCV» — прив'язка складу газу ECR до перенесених без
/// змін формул старої системи. Конвертер (<c>ScriptConverter</c>) дає тіло з <c>row.GCV_&lt;X&gt;</c>; тут ці аргументи стають
/// змінними скрипта, які читають довідники складу на ДАТУ РЯДКА (<c>registry as of</c>), а перед ними стоять запобіжники
/// «бракує складу/кейсу → рядок відхилено з причиною, ніколи не 0» (H9, H12, AC-2).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Склад: <c>REGSUM("GAS_COMPOSITION", ROW.CASE == кейс &amp;&amp; ROW.COMPONENT == компонент, ROW.MOL_PCT)</c> (D-580):
/// компонент без рядка в ІСНУЮЧОМУ кейсі — 0, як стара система (NULL → "0"); перша частина умови — індексний шлях.</item>
/// <item>Потік — <c>REGONE("STREAM", ROW.LEGACY_ID == row.GAS_COMPOSITION_ID)</c>; кейс, закріплення, слот — теж <c>REGONE</c> з умовою ROW.* (значення рядка документа на кшталт «481» не завжди текст, тож ключовий REGFIND дав би #REF)
/// (первинний ключ, версія — на дату рядка).</item>
/// <item>Потік/кейс/закріплення не знайдено, кейс «без складу» чи Σ = 0 — рядок відхилено; причина — ім'я змінної-умови.</item>
/// <item>Helium, C2H6S (є лише у формулах ТОх, у FLERT і ECR їх немає) — літерал 0, як стара система.</item>
/// <item>ТОх: <c>GCV_SOR_*</c> і <c>GCV_EOR_*</c> — з кейсу ТИПУ РЯДКА (H3: EOR — склад EOR, L1 виправлено); поїзд обирає
/// закріплення <c>TOX_GAS_STREAM</c> (стовпець + поїзд, H10/L6); 417 → потік 575 (H5). Передформула 481
/// <c>HSE_TO_SG_481_&lt;T&gt;_H2S</c> підміняє <c>GCV_&lt;T&gt;_H2S</c> для стовпця 481 — у тексті скрипта ОДРАЗУ після неї
/// (рішення людини 26.11), до першого читання.</item>
/// <item>Сезон (острів D): <c>asof.Month</c> 4…9 → <c>CASE_SUMMER</c>, інакше <c>CASE_WINTER</c> (H6); слот — <c>row.SLOT</c>.</item>
/// </list>
/// Генератор нічого не публікує й у БД не пише.
/// </remarks>
public static partial class SgFlaringBinding
{
    /// <summary>Довідник місць факела (AF Location → TypeReportID / суша).</summary>
    public const string LocationRegistry = "FLARE_LOCATION";

    /// <summary>Довідник розподілу LP-аналізаторів острова D (слоти 1–3, темпоральний).</summary>
    public const string SplitRegistry = "LP_SOUR_SPLIT";

    /// <summary>Довідник закріплень «стовпець газу ТОх × поїзд → потік + кейси SOR/EOR» (+ рядок постійних суші).</summary>
    public const string PinRegistry = "TOX_GAS_STREAM";

    /// <summary>Ключ закріплення постійних факелів суші в <see cref="PinRegistry"/>.</summary>
    public const string ContOnshorePin = "CONT_ONSHORE";

    /// <summary>Поїзд закріплення постійних факелів суші.</summary>
    public const string ContOnshoreTrain = "ALL";

    /// <summary>Стовпець газу ТОх, для якого діє передформула H2S.</summary>
    public const string Tox481Column = "481";

    /// <summary>Компоненти, яких немає у FLERT/ECR, — стара ТОх примусово 0 (R_Air_HSE_Thermaloxidizer_SG_Calculate.cs:40-43).</summary>
    public static IReadOnlyList<string> LegacyZero { get; } = ["Helium", "C2H6S"];

    /// <summary>Властивості кейсу, які формули можуть читати як <c>GCV_&lt;X&gt;</c> (поле <c>STREAM_CASE</c>).</summary>
    public static IReadOnlyDictionary<string, string> CaseProperties { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Temperature"] = FlertCompositionSchema.CaseTemperature,
        ["Density"] = FlertCompositionSchema.CaseDensityOp,
        ["Molecular_Weight"] = FlertCompositionSchema.CaseMwHysys,
        ["Z_CompressibilityFactor"] = FlertCompositionSchema.CaseZ,
    };

    /// <summary>D-580 (огляд M3): змінна-умова «у кейсу немає властивості» — вона ж причина відхилення рядка.</summary>
    public static IReadOnlyDictionary<string, string> CasePropertyGuards { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Temperature"] = "case_has_no_temperature",
        ["Density"] = "case_has_no_density",
        ["Molecular_Weight"] = "case_has_no_molecular_weight",
        ["Z_CompressibilityFactor"] = "case_has_no_compressibility_factor",
    };

    /// <summary>Коди <see cref="LocationRegistry"/> гілки споживача (5 значень AF, DESIGN §5.4).</summary>
    /// <param name="consumer">Споживач.</param>
    public static IReadOnlyList<string> LocationsOf(SgFlaringConsumer consumer) => consumer switch
    {
        SgFlaringConsumer.IntOnshore => ["ONSHORE_HP", "ONSHORE_LP"],
        SgFlaringConsumer.IntOffshore => ["A_ISLAND_HP", "D_ISLAND_HP", "D_ISLAND_LP"],
        _ => [],
    };

    /// <summary>Таблиця шаблону (Dynamic), з якої беруться рядки споживача, і колонка дати рядка.</summary>
    /// <param name="consumer">Споживач.</param>
    public static (string Table, string DateColumn) RowsOf(SgFlaringConsumer consumer) => consumer switch
    {
        SgFlaringConsumer.IntOnshore or SgFlaringConsumer.IntOffshore => ("INT_SG_EVENTS", "EVENT_START"),
        SgFlaringConsumer.ContOnshore => ("CONT_SG_ONSHORE", "DAY"),
        SgFlaringConsumer.ContOffshore => ("CONT_SG_DISLAND", "DAY"),
        SgFlaringConsumer.ToxDaily => ("TOX_DAILY", "DATE"),
        SgFlaringConsumer.ToxMonthly => ("TOX_MONTHLY", "MONTH"),
        _ => throw new ArgumentOutOfRangeException(nameof(consumer)),
    };

    /// <summary>Імена <c>GCV_…</c>, які читає тіло (після конвертера — <c>row.GCV_…</c>), у порядку першої появи.</summary>
    /// <param name="body">Тіло скрипта.</param>
    public static IReadOnlyList<string> ReferencedGcv(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return [.. GcvReference().Matches(body).Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Повний скрипт споживача: власне <c>rows from</c> + <c>registry as of</c> + запобіжники + змінні GCV, далі тіло конвертера
    /// без його <c>rows from</c> і з <c>row.GCV_X</c> → <c>GCV_X</c>; для ТОх — підміна H2S передформулою 481.
    /// </summary>
    /// <param name="body">Тіло скрипта (вихід конвертера формул <c>Flert</c>/<c>Thermaloxidizer</c>).</param>
    /// <param name="consumer">Споживач.</param>
    /// <exception cref="InvalidOperationException">
    /// Тіло читає <c>GCV_X</c>, якого немає серед компонентів і властивостей кейсу; або H2S читається до передформули 481.
    /// </exception>
    public static string Bind(string body, SgFlaringConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(body);

        // ✎ огляд L5: власний `rows from … where … active when …` тіла не відкидається мовчки — відбір рядків задає міст.
        if (RowsClause().IsMatch(body))
        {
            throw new InvalidOperationException(
                "SGF bridge: the body has its own 'rows from' clause; the bridge sets row selection itself. "
                + "Remove it from the converted body (or merge its where/active-when into the bridge explicitly).");
        }

        var stripped = body;
        var names = ReferencedGcv(stripped);
        var isTox = consumer is SgFlaringConsumer.ToxDaily or SgFlaringConsumer.ToxMonthly;
        var overrides = isTox ? Tox481Overrides(stripped) : [];

        // ✎ огляд M2: тіло ТОх читає H2S типу, але передформули 481 цього типу немає — стовпець 481 тихо взяв би склад.
        foreach (var type in (string[])["SOR", "EOR"])
        {
            if (isTox && names.Contains($"GCV_{type}_H2S") && !overrides.Exists(o => o.Type == type))
            {
                throw new InvalidOperationException(
                    $"SGF bridge: the TOx body reads GCV_{type}_H2S but has no HSE_TO_SG_481_{type}_H2S pre-formula for column 481.");
            }
        }
        foreach (var (type, statementEnd) in overrides.OrderByDescending(o => o.StatementEnd))
        {
            var target = $"GCV_{type}_H2S";
            stripped = stripped.Insert(
                statementEnd, $"\nvar {target} = row.GAS_COLUMN == \"{Tox481Column}\" ? HSE_TO_SG_481_{type}_H2S : {target}_FLERT;");
        }

        stripped = GcvReference().Replace(stripped, m => m.Groups["name"].Value);
        var overridden = overrides.Select(o => o.Type).ToHashSet(StringComparer.Ordinal);
        names = [.. names.Concat(overridden.Select(t => $"GCV_{t}_H2S")).Distinct(StringComparer.Ordinal)];
        return Prelude(consumer, names, overridden) + stripped.TrimStart('\r', '\n');
    }

    /// <summary>Голова скрипта: рядки, дата, запобіжники, змінні складу.</summary>
    /// <param name="consumer">Споживач.</param>
    /// <param name="gcvNames">Імена <c>GCV_…</c>, які читає тіло.</param>
    /// <param name="h2sOverridden">ТОх: типи (<c>SOR</c>/<c>EOR</c>), для яких <c>GCV_&lt;T&gt;_H2S</c> задає передформула 481.</param>
    public static string Prelude(SgFlaringConsumer consumer, IReadOnlyCollection<string> gcvNames, IReadOnlySet<string>? h2sOverridden = null)
    {
        ArgumentNullException.ThrowIfNull(gcvNames);
        var (table, date) = RowsOf(consumer);
        var s = new StringBuilder();
        s.Append("// SGF bridge (generated by SgFlaringBinding, ").Append(consumer).Append("): do not edit by hand.\n");
        s.Append("rows from ").Append(table).Append(Where(consumer)).Append(";\n");
        s.Append("registry as of row.").Append(date).Append(";\n");

        var prefixes = new List<string>();
        switch (consumer)
        {
            case SgFlaringConsumer.IntOnshore or SgFlaringConsumer.IntOffshore:
                Line(s, "var flare_location_not_mapped = row.LOCATION == null;");
                Line(s, "reject when flare_location_not_mapped;");
                Line(s, "var event_without_stream_or_case = row.GAS_COMPOSITION_ID == null || row.STREAM_TYPE == null;");
                Line(s, "reject when event_without_stream_or_case;");
                Line(s, "var stream_not_found = REGCOUNT(\"STREAM\", ROW.LEGACY_ID == row.GAS_COMPOSITION_ID) != 1;");
                Line(s, "reject when stream_not_found;");
                Line(s, "var gas_stream = REGONE(\"STREAM\", ROW.LEGACY_ID == row.GAS_COMPOSITION_ID);");
                Line(s, "var stream_onshore_mismatch = REGFIELD(gas_stream, \"IS_ONSHORE\") != REGFIELD(row.LOCATION, \"IS_ONSHORE\");");
                Line(s, "reject when stream_onshore_mismatch;");
                Line(s, "var case_name = row.STREAM_TYPE;");
                prefixes.Add(string.Empty);
                break;
            case SgFlaringConsumer.ContOnshore:
                Line(s, $"var stream_pin_not_found = REGCOUNT(\"{PinRegistry}\", ROW.COLUMN == \"{ContOnshorePin}\" && ROW.TRAIN == \"{ContOnshoreTrain}\") != 1;");
                Line(s, "reject when stream_pin_not_found;");
                Line(s, $"var gas_pin = REGONE(\"{PinRegistry}\", ROW.COLUMN == \"{ContOnshorePin}\" && ROW.TRAIN == \"{ContOnshoreTrain}\");");
                Line(s, "var gas_stream = REGFIELD(gas_pin, \"STREAM\");");
                Line(s, "var case_name = REGFIELD(gas_pin, \"CASE_SOR\");");
                prefixes.Add(string.Empty);
                break;
            case SgFlaringConsumer.ContOffshore:
                Line(s, "var split_slot_empty = row.SLOT == null;");
                Line(s, "reject when split_slot_empty;");
                Line(s, $"var split_slot_not_found = REGCOUNT(\"{SplitRegistry}\", ROW.SLOT == row.SLOT) != 1;");
                Line(s, "reject when split_slot_not_found;");
                Line(s, $"var split_not_three_slots_or_sum_not_one = REGCOUNT(\"{SplitRegistry}\", true) != 3"
                        + $" || Abs(REGSUM(\"{SplitRegistry}\", true, ROW.COEF) - 1) > {SplitSumTolerance};");
                Line(s, "reject when split_not_three_slots_or_sum_not_one;");
                Line(s, $"var split_slot = REGONE(\"{SplitRegistry}\", ROW.SLOT == row.SLOT);");
                Line(s, "var split_coef = REGFIELD(split_slot, \"COEF\");");
                Line(s, "var gas_stream = REGFIELD(split_slot, \"STREAM\");");
                Line(s, "var case_name = asof.Month >= 4 && asof.Month <= 9 ? REGFIELD(split_slot, \"CASE_SUMMER\") : REGFIELD(split_slot, \"CASE_WINTER\");");
                prefixes.Add(string.Empty);
                break;
            case SgFlaringConsumer.ToxDaily or SgFlaringConsumer.ToxMonthly:
                Line(s, "var tox_row_without_column_train_or_type = row.GAS_COLUMN == null || row.TRAIN == null || row.TYPE == null;");
                Line(s, "reject when tox_row_without_column_train_or_type;");
                Line(s, "var tox_train_unknown = row.TRAIN in (\"Train 1\", \"Train 2\") ? false : true;");
                Line(s, "reject when tox_train_unknown;");
                Line(s, "var tox_type_unknown = row.TYPE in (\"SOR\", \"EOR\") ? false : true;");
                Line(s, "reject when tox_type_unknown;");
                Line(s, $"var stream_pin_not_found = REGCOUNT(\"{PinRegistry}\", ROW.COLUMN == row.GAS_COLUMN && ROW.TRAIN == row.TRAIN) != 1;");
                Line(s, "reject when stream_pin_not_found;");
                Line(s, $"var gas_pin = REGONE(\"{PinRegistry}\", ROW.COLUMN == row.GAS_COLUMN && ROW.TRAIN == row.TRAIN);");
                Line(s, "var gas_stream = REGFIELD(gas_pin, \"STREAM\");");
                Line(s, "var case_name = row.TYPE == \"EOR\" ? REGFIELD(gas_pin, \"CASE_EOR\") : REGFIELD(gas_pin, \"CASE_SOR\");");
                prefixes.Add("SOR_");
                prefixes.Add("EOR_");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(consumer));
        }

        Line(s, "var composition_case_not_found = REGCOUNT(\"STREAM_CASE\", ROW.STREAM == gas_stream && ROW.CASE_NAME == case_name) != 1;");
        Line(s, "reject when composition_case_not_found;");
        Line(s, "var gas_case = REGONE(\"STREAM_CASE\", ROW.STREAM == gas_stream && ROW.CASE_NAME == case_name);");
        Line(s, "var case_marked_no_composition = REGFIELD(gas_case, \"NO_COMPOSITION\") == true;");
        Line(s, "reject when case_marked_no_composition;");
        Line(s, "var case_composition_empty = REGSUM(\"GAS_COMPOSITION\", ROW.CASE == gas_case, ROW.MOL_PCT) == 0;");
        Line(s, "reject when case_composition_empty;");

        var components = FlertCompositionSchema.ComponentColumns.ToDictionary(p => p.Column, p => p.Component, StringComparer.Ordinal);
        var unknown = new List<string>();
        var guardedProperties = new HashSet<string>(StringComparer.Ordinal);
        var readComponents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in gcvNames)
        {
            if (!TrySplit(name, prefixes, out var prefix, out var x))
            {
                unknown.Add(name);
                continue;
            }

            // ТОх H2S, підмінений передформулою 481: склад — під іменем _FLERT, сама змінна — після передформули (Bind).
            var target = x == "H2S" && h2sOverridden is not null && prefix.Length > 0 && h2sOverridden.Contains(prefix.TrimEnd('_'))
                ? name + "_FLERT"
                : name;
            if (LegacyZero.Contains(x, StringComparer.Ordinal))
            {
                Line(s, $"var {target} = 0;");
            }
            else if (CaseProperties.TryGetValue(x, out var field))
            {
                // ✎ D-580 (людина 26.11, огляд M3): порожня властивість кейсу, яку ЧИТАЄ формула (T_C, …), — відсутні дані: рядок
                // відхилено з причиною «case_has_no_<властивість>», а не розрахунок при 0 °C. Властивість, якої тіло не читає, не
                // генерується й нічого не відхиляє.
                var guard = CasePropertyGuards[x];
                if (guardedProperties.Add(x))
                {
                    Line(s, $"var {guard} = REGFIELD(gas_case, \"{field}\") == null;");
                    Line(s, $"reject when {guard};");
                }

                Line(s, $"var {target} = REGFIELD(gas_case, \"{field}\");");
            }
            else if (components.TryGetValue(x, out var component))
            {
                // ✎ вимір 26.11: компонент читається з кейсу рядка ОДИН раз; GCV_EOR_X ТОх — той самий кейс (H3), тож посилання.
                if (readComponents.TryGetValue(component, out var already))
                {
                    Line(s, $"var {target} = {already};");
                }
                else
                {
                    Line(s, $"var {target} = REGSUM(\"GAS_COMPOSITION\", ROW.CASE == gas_case && ROW.COMPONENT == REGFIND(\"COMPONENT\", \"{component}\"), ROW.MOL_PCT);");
                    readComponents[component] = target;
                }
            }
            else
            {
                unknown.Add(name);
            }
        }

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                "SGF bridge: the body reads " + string.Join(", ", unknown)
                + " — not a FLERT component column, case property or legacy zero; refusing to bind it to 0.");
        }

        // ✎ огляд H4: ключ паритету постійних факелів у старій системі — EFID|FCID (склад кейсу); вихід SGF_FCID дає його й ECR.
        if (consumer is SgFlaringConsumer.ContOnshore or SgFlaringConsumer.ContOffshore)
        {
            Line(s, $"output {FcidOutput} : one = REGFIELD(gas_case, \"{FlertCompositionSchema.CaseLegacyCompositionId}\");");
        }

        return s.Append('\n').ToString();
    }

    /// <summary>
    /// ТОх: де в тілі оголошено передформулу <c>HSE_TO_SG_481_&lt;T&gt;_H2S</c> (кінець оператора). Перевіряє порядок обчислення:
    /// <c>GCV_&lt;T&gt;_H2S</c> не читається ні до неї, ні в ній самій (інакше підміна не мала б сенсу — помилка генератора).
    /// </summary>
    private static List<(string Type, int StatementEnd)> Tox481Overrides(string body)
    {
        var found = new List<(string, int)>();
        foreach (Match m in Tox481Statement().Matches(body))
        {
            var type = m.Groups["type"].Value;
            var end = body.IndexOf(';', m.Index);
            if (end < 0)
            {
                throw new InvalidOperationException($"SGF bridge: the 481 pre-formula for {type} has no ';'.");
            }

            var reader = new Regex(@"\brow\.GCV_" + type + @"_H2S\b", RegexOptions.CultureInvariant);
            if (reader.IsMatch(body[..(end + 1)]))
            {
                throw new InvalidOperationException(
                    $"SGF bridge: GCV_{type}_H2S is read before or inside HSE_TO_SG_481_{type}_H2S; the 481 override needs it after.");
            }

            found.Add((type, end + 1));
        }

        return found;
    }

    /// <summary>
    /// ✎ огляд H1: кожен рядок події відбирає РІВНО ОДНА методологія INT — порожнє/незіставлене місце AF відбирає гілка суші
    /// (<c>null</c> у переліку), і її <c>reject when flare_location_not_mapped</c> робить рядок видимо відхиленим. ТОх відсіює
    /// лише сміття старого Excel <c>Train = '0'</c> (DESIGN §5.4); порожній чи інший поїзд/тип відбирається й відхиляється.
    /// </summary>
    private static string Where(SgFlaringConsumer consumer)
    {
        var locations = LocationsOf(consumer);
        if (locations.Count > 0)
        {
            var nullFirst = consumer == SgFlaringConsumer.IntOnshore ? "null, " : string.Empty;
            return " where row.LOCATION in (" + nullFirst
                   + string.Join(", ", locations.Select(l => $"entry(\"{LocationRegistry}\", \"{l}\")")) + ")";
        }

        return consumer is SgFlaringConsumer.ToxDaily or SgFlaringConsumer.ToxMonthly ? " where row.TRAIN != \"0\"" : string.Empty;
    }

    /// <summary>
    /// ✎ огляд H2: допуск Σ коефіцієнтів розподілу — значення AF 2024 (0.4285714 + 0.4285714 + 0.1428571 = 0.9999999) мусять
    /// проходити; той самий допуск — у генераторі й у провізії.
    /// </summary>
    public const string SplitSumTolerance = "0.000001";

    /// <summary>Вихід постійних факелів: Id складу FLERT розв'язаного кейсу (ключ паритету EFID|FCID, огляд H4).</summary>
    public const string FcidOutput = "SGF_FCID";

    private static bool TrySplit(string name, List<string> prefixes, out string prefix, out string x)
    {
        foreach (var p in prefixes.OrderByDescending(p => p.Length))
        {
            var head = "GCV_" + p;
            if (name.StartsWith(head, StringComparison.Ordinal) && name.Length > head.Length)
            {
                prefix = p;
                x = name[head.Length..];
                return true;
            }
        }

        prefix = string.Empty;
        x = string.Empty;
        return false;
    }

    private static void Line(StringBuilder s, string line) => s.Append(line).Append('\n');

    [GeneratedRegex(@"\brow\.(?<name>GCV_\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex GcvReference();

    [GeneratedRegex(@"^[ \t]*rows[ \t]+from\b[^;]*;[ \t]*\r?\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RowsClause();

    [GeneratedRegex(@"^[ \t]*(?:output|visible[ \t]+var|var)[ \t]+HSE_TO_SG_481_(?<type>SOR|EOR)_H2S\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Tox481Statement();
}
