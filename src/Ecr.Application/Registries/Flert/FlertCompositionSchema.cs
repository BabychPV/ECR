// src/Ecr.Application/Registries/Flert/FlertCompositionSchema.cs
namespace Ecr.Application.Registries.Flert;

/// <summary>
/// Контракт дзеркала складу газу FLERT (<c>D-476</c>): колонки джерела
/// (<c>FLERT.dbo.usp_GetAllStreamDataFlattened</c>), коди довідників і полів ECR
/// (FEATURE-REGISTRY-TABLES §11.4) і зіставлення компонентів.
/// </summary>
/// <remarks>
/// ⚠ Код компонента = ім'я колонки FLERT (= суфікс <c>GCV_&lt;X&gt;</c> / <c>CST.MW_GCV_&lt;X&gt;</c> старих формул AF) —
/// імпортовані формули не перейменовуються. Три пари дублів (<c>CO</c>/<c>Carbon_Monoxide</c>,
/// <c>H2</c>/<c>Hydrogen</c>, <c>O2</c>/<c>Oxygen</c>) зводяться до КОРОТКОГО коду (людина 11.11).
/// <para>
/// ⚠ Відхилення від §11.4 (судження, не нова вимога): <c>STREAM_CASE.STREAM</c> — <b>посилання</b>, а не
/// композиція. Кейс темпоральний (вікно дії версії складу FLERT), а дитина композиції за правилом опису
/// (<c>err.ECR-REG-0422.compositionChildTemporal</c>) темпоральною бути не може. Композиція лишається на
/// <c>GAS_COMPOSITION.CASE</c> (нетемпоральна дитина темпорального кейсу — видима рівно тоді, коли кейс).
/// </para>
/// </remarks>
public static class FlertCompositionSchema
{
    /// <summary>
    /// Ключ запиту знімка в каналі налаштувань SQL-джерела (фізично <c>Secrets:Sql:&lt;код джерела&gt;:CompositionQuery</c>
    /// або спільний <c>Secrets:Sql:CompositionQuery</c>). Типового тексту немає; еталонний —
    /// <c>EXEC dbo.usp_GetAllStreamDataFlattened;</c>.
    /// </summary>
    public const string QueryKey = "Sql:CompositionQuery";

    // ─── Довідники ECR ──────────────────────────────────────────────────────

    /// <summary>Потоки FLERT (master — FLERT).</summary>
    public const string StreamRegistry = "STREAM";

    /// <summary>Кейси потоку (версія складу з вікном дії; master — FLERT).</summary>
    public const string CaseRegistry = "STREAM_CASE";

    /// <summary>Рядки складу «кейс × компонент → мол.%» (master — FLERT).</summary>
    public const string CompositionRegistry = "GAS_COMPOSITION";

    /// <summary>Компоненти (властивості M, nC, nS — з Add Stream; master — ECR).</summary>
    public const string ComponentRegistry = "COMPONENT";

    /// <summary>Правило «Σ мол.% = 100 ± 0.5» на <see cref="CaseRegistry"/> (<c>D-195</c>, <c>D-197</c>).</summary>
    public const string Sum100Rule = "SUM_100";

    // ─── Поля ───────────────────────────────────────────────────────────────

    /// <summary>STREAM: Id потоку FLERT (альтернативний ключ <c>BY_LEGACY_ID</c>).</summary>
    public const string StreamLegacyId = "LEGACY_ID";

    /// <summary>STREAM: номер потоку (не унікальний — 15 дублів на копії 11.11).</summary>
    public const string StreamNumber = "NUMBER";

    /// <summary>STREAM: назва (FLERT унікальна лише разом з <c>isOnshore</c>).</summary>
    public const string StreamName = "NAME";

    /// <summary>STREAM: суша (<c>true</c>) чи море.</summary>
    public const string StreamIsOnshore = "IS_ONSHORE";

    /// <summary>STREAM: кислий потік.</summary>
    public const string StreamIsSour = "IS_SOUR";

    /// <summary>STREAM: потік HMB.</summary>
    public const string StreamIsHmb = "IS_HMB";

    /// <summary>STREAM: документ HMB (<c>streamHMB</c>).</summary>
    public const string StreamHmbDoc = "HMB_DOC";

    /// <summary>STREAM: група потоку.</summary>
    public const string StreamGroup = "GROUP";

    /// <summary>STREAM: не показувати в списках (<c>excludeInListBox</c>).</summary>
    public const string StreamExcludeInList = "EXCLUDE_IN_LIST";

    /// <summary>STREAM_CASE: потік (посилання).</summary>
    public const string CaseStream = "STREAM";

    /// <summary>STREAM_CASE: назва кейсу (<c>FlaringStreamType.StreamType</c>).</summary>
    public const string CaseName = "CASE_NAME";

    /// <summary>STREAM_CASE: температура, °C.</summary>
    public const string CaseTemperature = "T_C";

    /// <summary>STREAM_CASE: робоча густина HYSYS (0 у FLERT → порожньо).</summary>
    public const string CaseDensityOp = "DENSITY_OP";

    /// <summary>STREAM_CASE: молярна маса HYSYS (0 у FLERT → порожньо).</summary>
    public const string CaseMwHysys = "MW_HYSYS";

    /// <summary>STREAM_CASE: коефіцієнт стисливості (0 у FLERT → порожньо).</summary>
    public const string CaseZ = "Z";

    /// <summary>STREAM_CASE: <c>RuleParse</c> FLERT (довідково).</summary>
    public const string CaseRuleParse = "RULE_PARSE";

    /// <summary>STREAM_CASE: Id <c>FlaringStreamType</c> FLERT.</summary>
    public const string CaseLegacyTypeId = "LEGACY_TYPE_ID";

    /// <summary>STREAM_CASE: Id <c>FlaringStreamGasComposition</c> FLERT.</summary>
    public const string CaseLegacyCompositionId = "LEGACY_COMPOSITION_ID";

    /// <summary>
    /// STREAM_CASE: складу немає (Σ = 0, кейси «No composition»). Позначка для розрахунку: такий кейс — проблема
    /// (<c>MissingInputs</c>, крок F7), а не нульові викиди (людина 11.11).
    /// </summary>
    public const string CaseNoComposition = "NO_COMPOSITION";

    /// <summary>GAS_COMPOSITION: кейс (композиція).</summary>
    public const string RowCase = "CASE";

    /// <summary>GAS_COMPOSITION: компонент (посилання).</summary>
    public const string RowComponent = "COMPONENT";

    /// <summary>GAS_COMPOSITION: мол.%.</summary>
    public const string RowMolPct = "MOL_PCT";

    /// <summary>COMPONENT: ім'я колонки FLERT / HYSYS (псевдонім імпорту).</summary>
    public const string ComponentHysysName = "HYSYS_NAME";

    // ─── Колонки FLERT ──────────────────────────────────────────────────────

    /// <summary>Колонки потоку.</summary>
    public static readonly IReadOnlyList<string> StreamColumns =
    [
        "Stream_ID", "StreamNumber", "Stream_Name", "isOnshore", "isSour", "streamHMB",
        "excludeInListBox", "StreamGroup", "isHMB",
    ];

    /// <summary>Колонки кейсу. Вікно дії версії — вікно складу (<c>GasComposition_*</c>).</summary>
    public static readonly IReadOnlyList<string> CaseColumns =
    [
        "StreamType_ID", "StreamType", "RuleParse",
        "GasComposition_ID", "GasComposition_ValidFrom", "GasComposition_ValidTo",
    ];

    /// <summary>
    /// Колонки форми процедури, які зіставлення знає, але не читає (вікна дії потоку й кейсу FLERT: копія
    /// версіонується вікном складу). Їх відсутність не помилка, а наявність — не «невідома колонка».
    /// </summary>
    public static readonly IReadOnlyList<string> KnownUnusedColumns =
    [
        "Stream_ValidFrom", "Stream_ValidTo", "StreamType_ValidFrom", "StreamType_ValidTo",
    ];

    /// <summary>Властивості кейсу серед 52 значень складу.</summary>
    public static readonly IReadOnlyList<string> PropertyColumns =
    [
        "Temperature", "Density", "Molecular_Weight", "Z_CompressibilityFactor",
    ];

    /// <summary>
    /// 49 колонок компонентів у порядку FLERT → код компонента ECR (46 кодів). Три довгі назви дублів ведуть на
    /// короткий код.
    /// </summary>
    public static readonly IReadOnlyList<(string Column, string Component)> ComponentColumns =
    [
        ("Nitrogen", "Nitrogen"), ("CO2", "CO2"), ("H2S", "H2S"), ("Methane", "Methane"), ("Ethane", "Ethane"),
        ("Propane", "Propane"), ("IC4_1", "IC4_1"), ("NC4_1", "NC4_1"), ("IC5_1", "IC5_1"), ("NC5_1", "NC5_1"),
        ("C6_1", "C6_1"), ("Benzene", "Benzene"), ("C7_1", "C7_1"), ("Toluene", "Toluene"), ("C8_1", "C8_1"),
        ("pXylene", "pXylene"), ("EBenzene", "EBenzene"), ("C9_1", "C9_1"), ("C10_1", "C10_1"), ("C11_1", "C11_1"),
        ("C12_1", "C12_1"), ("C13_1", "C13_1"), ("C14_1", "C14_1"), ("CN1_2", "CN1_2"), ("CN2_2", "CN2_2"),
        ("CN3_1", "CN3_1"), ("MMercaptan", "MMercaptan"), ("EMercaptan", "EMercaptan"), ("nPMercaptan", "nPMercaptan"),
        ("nBMercaptan", "nBMercaptan"), ("CS2", "CS2"), ("COS", "COS"), ("H2O", "H2O"), ("TEGlycol", "TEGlycol"),
        ("Carbon_Monoxide", "CO"), ("Hydrogen", "H2"), ("Oxygen", "O2"),
        ("SO2", "SO2"), ("O2", "O2"), ("NH3", "NH3"), ("H2", "H2"), ("CO", "CO"), ("C2H7NO", "C2H7NO"),
        ("Sulfur", "Sulfur"), ("C2H6S2", "C2H6S2"), ("C3H8S2", "C3H8S2"), ("C4H10S2", "C4H10S2"),
        ("C4H11NO2", "C4H11NO2"), ("Methanol", "Methanol"),
    ];

    /// <summary>Коди компонентів ECR (46, як <c>COMPONENT</c> §11.4), у порядку першої появи.</summary>
    public static IReadOnlyList<string> Components { get; } =
        [.. ComponentColumns.Select(c => c.Component).Distinct(StringComparer.Ordinal)];

    /// <summary>Усі колонки, які читає зіставлення: бракує хоч однієї — синк відмовляє (дрейф схеми).</summary>
    public static IReadOnlyList<string> MappedColumns { get; } =
        [.. StreamColumns, .. CaseColumns, .. PropertyColumns, .. ComponentColumns.Select(c => c.Column)];

    /// <summary>Чи є колонка довгою назвою дубля (<c>Carbon_Monoxide</c>, <c>Hydrogen</c>, <c>Oxygen</c>).</summary>
    /// <param name="column">Ім'я колонки FLERT.</param>
    public static bool IsLongAlias(string column)
        => ComponentColumns.Any(c => c.Column == column && c.Component != column);
}
