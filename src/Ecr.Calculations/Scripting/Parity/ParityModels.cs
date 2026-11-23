using System.Globalization;
using Ecr.Domain.Enums;

namespace Ecr.Calculations.Scripting.Parity;

/// <summary>Назви аспектів розбіжності (MS, план §8.4); у звіті це поле <see cref="ParityMismatch.Aspect"/>.</summary>
public static class ParityAspect
{
    /// <summary>Рядок відібраний лише однією зі сторін.</summary>
    public const string RowSelected = "rows.selected";

    /// <summary>Рядок виключений (<c>active when</c> / фільтр активності) лише однією зі сторін.</summary>
    public const string RowExcluded = "rows.excluded";

    /// <summary>Рядок є в переліку «усіх рядків» лише однієї зі сторін.</summary>
    public const string RowAll = "rows.all";

    /// <summary>Вихід є лише в старого модуля.</summary>
    public const string ValueMissing = "value.missing";

    /// <summary>Вихід є лише в модуля скрипта.</summary>
    public const string ValueExtra = "value.extra";

    /// <summary>Значення різні (точна рівність <see cref="decimal"/>).</summary>
    public const string ValueNumber = "value.number";

    /// <summary>Одиниці різні.</summary>
    public const string ValueUnit = "value.unit";

    /// <summary>Однакова трійка (вихід, речовина, вид) двічі в одній зі сторін.</summary>
    public const string ValueDuplicate = "value.duplicateKey";

    /// <summary>Наявність <c>MissingArguments</c> різна.</summary>
    public const string MissingArgumentsPresence = "missingArguments.presence";

    /// <summary>Множини <c>MissingArguments</c> різні.</summary>
    public const string MissingArgumentsSet = "missingArguments.set";

    /// <summary>Наявність <c>StepError</c> різна.</summary>
    public const string StepErrorPresence = "stepError.presence";

    /// <summary>
    /// Кроки-причини рядка (місяць, провал чи замаскований нуль) різні — лише рівень <c>Module</c> (HSE400): там помилка
    /// кроку живе в трасуванні, а не в <c>StepError</c>.
    /// </summary>
    public const string TraceSteps = "trace.steps";

    /// <summary>Рядок відхилено (<c>ECR-CALC-0422</c> правило категорії) лише однією зі сторін.</summary>
    public const string Rejected = "rejected";

    /// <summary>Виконання одного зі шляхів завершилося винятком (іншим, ніж відхилення рядка).</summary>
    public const string Error = "error";
}

/// <summary>Одне обчислене значення рядка в порівнянні: (вихід, речовина, вид) → число й одиниця.</summary>
/// <param name="OutputCode">Код виходу (чи видимої величини).</param>
/// <param name="SubstanceEntryId">Речовина; <c>null</c> — вихід без речовини.</param>
/// <param name="Kind">Вихід чи проміжне значення.</param>
/// <param name="Value">Число.</param>
/// <param name="UnitId">Одиниця.</param>
public sealed record ParityValue(
    string OutputCode, int? SubstanceEntryId, CalculationResultKind Kind, decimal Value, int UnitId)
{
    /// <summary>Ключ порівняння (вихід, речовина, вид).</summary>
    public string Key => string.Create(
        CultureInfo.InvariantCulture, $"{OutputCode}|{SubstanceEntryId?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{Kind}");
}

/// <summary>Результат одного рядка на одному зі шляхів.</summary>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="Values">Значення.</param>
/// <param name="MissingArguments">Перелік відсутніх аргументів; <c>null</c> — немає.</param>
/// <param name="HasStepError">Є <c>StepError</c>.</param>
/// <param name="Rejected">Рядок відхилено правилом категорії.</param>
/// <param name="Error">Текст винятку, якщо виконання впало з іншої причини; <c>null</c> — ні.</param>
/// <param name="Steps">
/// Кроки-причини (код, провал, причина маскування) — лише для рівня <c>Module</c>; <c>null</c> — шлях їх не порівнює
/// (текст виразу в кроці різниться між діалектами, тож порівнюється не він).
/// </param>
public sealed record ParityRowOutcome(
    string RowKey,
    IReadOnlyList<ParityValue> Values,
    IReadOnlyList<string>? MissingArguments = null,
    bool HasStepError = false,
    bool Rejected = false,
    string? Error = null,
    IReadOnlyList<string>? Steps = null);

/// <summary>Один шлях (старий чи скрипт) для екземпляра таблиці: вибір рядків і результати відібраних.</summary>
/// <param name="Selected">Відібрані рядки.</param>
/// <param name="Excluded">Виключені рядки.</param>
/// <param name="AllRows">Усі рядки таблиці (<c>null</c> — шлях їх не повертає).</param>
/// <param name="Rows">Результати за ключем рядка.</param>
public sealed record ParitySide(
    IReadOnlyList<string> Selected,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<string>? AllRows,
    IReadOnlyDictionary<string, ParityRowOutcome> Rows);

/// <summary>Адреса порівняння: що саме порівнюється (для запису розбіжності).</summary>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="Version">Номер версії.</param>
/// <param name="VersionId">Id версії.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
public sealed record ParityTarget(
    string MethodologyCode, string Version, int VersionId, long DocumentId, int PeriodKey, long TableInstanceId);

/// <summary>Запис розбіжності: (методологія, версія, документ, період, таблиця, рядок, що саме, старе, нове).</summary>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="Version">Номер версії.</param>
/// <param name="VersionId">Id версії.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="RowKey">Ключ рядка; <c>null</c> — розбіжність на рівні екземпляра.</param>
/// <param name="Aspect">Що саме розійшлося (<see cref="ParityAspect"/>).</param>
/// <param name="Old">Старе значення (текстом).</param>
/// <param name="New">Нове значення (текстом).</param>
public sealed record ParityMismatch(
    string MethodologyCode,
    string Version,
    int VersionId,
    long DocumentId,
    int PeriodKey,
    long TableInstanceId,
    string? RowKey,
    string Aspect,
    string? Old,
    string? New);

/// <summary>Звіт порівняння: розбіжності й лічильники.</summary>
/// <param name="Mismatches">Розбіжності.</param>
/// <param name="RowsCompared">Скільки рядків, відібраних обома шляхами, порівняно за значеннями.</param>
/// <param name="ValuesCompared">Скільки значень (трійок вихід/речовина/вид) порівняно.</param>
public sealed record ParityReport(IReadOnlyList<ParityMismatch> Mismatches, int RowsCompared, int ValuesCompared)
{
    /// <summary>Звіт без жодного порівняння.</summary>
    public static ParityReport Empty { get; } = new([], 0, 0);

    /// <summary>Розбіжностей немає.</summary>
    public bool IsEqual => Mismatches.Count == 0;
}

/// <summary>Підсумок прогону <see cref="ParityRunner"/> по всіх документах, періодах і екземплярах.</summary>
/// <param name="Targets">Скільки екземплярів таблиць порівняно (кожен — версія × документ × період × таблиця).</param>
/// <param name="RowsCompared">Скільки рядків порівняно.</param>
/// <param name="ValuesCompared">Скільки значень порівняно.</param>
/// <param name="Mismatches">Усі розбіжності.</param>
/// <param name="Unproven">
/// Версії, яких прогін НЕ довів: тіньова версія без жодного порівняного екземпляра, а також версії рушія <c>Formulas</c>,
/// що паритет не покриває зовсім (без тіньового тексту, без дати чинності). Кожен запис — «код версія:
/// причина». Бібліотеки сюди не потрапляють: їх доводять імпортери.
/// </param>
/// <param name="PerVersion">✎ D-571: підсумок за кожною затіненою версією (ворота перемикання — за версією).</param>
public sealed record ParityRunReport(
    int Targets,
    int RowsCompared,
    int ValuesCompared,
    IReadOnlyList<ParityMismatch> Mismatches,
    IReadOnlyList<string>? Unproven = null,
    IReadOnlyList<ParityVersionResult>? PerVersion = null)
{
    /// <summary>Версії, яких прогін не довів; ніколи не <c>null</c>.</summary>
    public IReadOnlyList<string> UnprovenVersions => Unproven ?? [];

    /// <summary>Підсумок за кожною затіненою версією; ніколи не <c>null</c>.</summary>
    public IReadOnlyList<ParityVersionResult> Versions => PerVersion ?? [];

    /// <summary>
    /// ✎ D-571: версії, які ДОВЕДЕНО й які можна перемикати на <c>Script</c>: хоч одне порівняне значення й жодної
    /// розбіжності. Бібліотеки сюди не входять (їх доводять імпортери, перемикаються разом з ними).
    /// </summary>
    public IReadOnlyList<ParityVersionResult> Eligible => [.. Versions.Where(v => v.IsProven)];

    /// <summary>
    /// Розбіжностей немає. ⚠ Порожній прогін (<see cref="Targets"/> = 0) теж «без розбіжностей», але нічого не доводить:
    /// чесний підсумок — <see cref="IsProven"/>.
    /// </summary>
    public bool IsEqual => Mismatches.Count == 0;

    /// <summary>Розбіжностей немає І щось справді порівняно (хоч одне значення).</summary>
    public bool IsProven => Mismatches.Count == 0 && Targets > 0 && ValuesCompared > 0;
}

/// <summary>Підсумок паритету однієї затіненої версії (✎ D-571: ворота перемикання — за версією, не за прогоном).</summary>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="VersionId">Версія.</param>
/// <param name="Version">Номер версії.</param>
/// <param name="Targets">Скільки екземплярів таблиць порівняно для цієї версії.</param>
/// <param name="RowsCompared">Скільки рядків.</param>
/// <param name="ValuesCompared">Скільки значень.</param>
/// <param name="MismatchCount">Скільки розбіжностей.</param>
/// <param name="IsLibrary">Бібліотека: доводиться імпортерами, сама в перелік перемикання не потрапляє.</param>
/// <param name="ScriptHash">
/// SHA-256 (hex, нижній регістр) ТОГО тексту, що порівнювався: вікно перемикає лише версію, чий <c>ScriptHash</c> у базі досі такий
/// самий (доведено саме цей текст).
/// </param>
public sealed record ParityVersionResult(
    string MethodologyCode,
    int VersionId,
    string Version,
    int Targets,
    int RowsCompared,
    int ValuesCompared,
    int MismatchCount,
    bool IsLibrary = false,
    string? ScriptHash = null)
{
    /// <summary>
    /// Доведено: порівняно хоч одне значення і розбіжностей немає. ⛔ Екземпляр без жодного відібраного рядка (правила
    /// нічого не вибрали) — не доказ: «0 розбіжностей із 0 значень» нічого не каже про числа скрипта.
    /// </summary>
    public bool IsProven => !IsLibrary && MismatchCount == 0 && Targets > 0 && ValuesCompared > 0;

    /// <summary>Причина, з якої версію не доведено; <c>null</c> — доведено.</summary>
    public string? UnprovenReason => IsProven || IsLibrary
        ? null
        : MismatchCount > 0
            ? "mismatches"
            : Targets == 0
                ? "no table instance was compared (no active binding with data, or the version is not the effective one on any document period)"
                : "no values compared (the rows of every compared table instance were not selected by the version)";
}
