namespace Ecr.Calculations;

/// <summary>
/// Ліміти прогону методологій (ФВ-9.8): скільки прив'язок пакета виконується
/// одночасно і скільки комірок входу дозволено одній прив'язці.
/// </summary>
/// <remarks>
/// ⛔ Ліміти — всередині процесу служби, потоками-воркерами, а не окремими
/// процесами (<c>D-205</c>): окремий worker-процес чи Job Object — це MSI,
/// служба й IPC заради одного ліміту, а <c>GCHeapHardLimit</c> обмежив би ВЕСЬ
/// процес, тобто бив би по запитах операторів, і OOM ще й ретраївся б тричі.
///
/// ⚠ Секція конфігурації <c>Calculations</c>; недійсне значення зупиняє старт
/// (<c>EcrConfigurationValidation</c>), а не підміняється дефолтом мовчки.
/// </remarks>
public sealed class CalculationLimits
{
    /// <summary>Секція конфігурації.</summary>
    public const string SectionName = "Calculations";

    /// <summary>Типовий паралелізм — той, що стояв константою до ФВ-9.8.</summary>
    public const int DefaultMaxParallelism = 4;

    /// <summary>
    /// Типовий бюджет комірок входу однієї прив'язки: <c>300 000</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Звідки число. Найбільша законна таблиця системи — <c>500 рядків × 60
    /// колонок = 30 000</c> комірок (обсяг директиви: 91 таблиця × 500 × 60;
    /// <c>PatchCellsRequest.MaxCells</c>, <c>TvpBatchWriteTests</c>,
    /// <c>ExcelExporterPerformanceTests</c>). Золотий тест HSE301
    /// (<c>Hse301GoldenTests</c>) — 1 рядок × 5 аргументів = 5 комірок на прив'язку.
    /// Бюджет — найбільша таблиця × 10 запасу: законна прив'язка до нього не
    /// доходить, а прив'язка, що на порядок більша за будь-яку форму, — ознака
    /// зіпсованих даних чи правила, яке зачепило не ту таблицю.
    /// </remarks>
    public const int DefaultMaxInputCellsPerBinding = 300_000;

    /// <summary>
    /// Типовий бюджет кроків ОДНОГО рядка скрипта на всі його оператори (PSS-21): <c>20 000 000</c> — тисяча стель
    /// одного оператора (<c>Evaluator.MaxEvaluationSteps</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Звідки число. До PSS-21 межа була лише на оператор, а кількість операторів обмежував лише розмір тексту:
    /// ~4 000 операторів по 20 000 кроків — 80 млн кроків на рядок (хвилини процесора). Найбільші версії корпусу мають
    /// сотні операторів по десятки кроків; агрегат над таблицею-псевдонімом упирається у стелю оператора, і таких
    /// операторів у законному скрипті одиниці. Тисяча стель — запас на порядки для законного скрипта й секунди, а не
    /// хвилини, для зловмисного. Бюджет детермінований (рахується робота, а не час): той самий рядок дає той самий
    /// результат на будь-якому вузлі, тож число однакове для Api й воркера.
    /// </remarks>
    public const int DefaultMaxRowEvaluationSteps = 1_000 * Ecr.Expressions.Evaluation.Evaluator.MaxEvaluationSteps;

    /// <summary>
    /// Найменший бюджет рядка — стеля одного оператора (<c>Evaluator.MaxEvaluationSteps</c>, 20 000): менший обрізав би
    /// навіть один законний оператор до того, як спрацює його власна межа.
    /// </summary>
    public const int MinRowEvaluationSteps = Ecr.Expressions.Evaluation.Evaluator.MaxEvaluationSteps;

    /// <summary>Скільки методологій одного пакета виконувати одночасно (≥ 1).</summary>
    public int MaxParallelism { get; set; } = DefaultMaxParallelism;

    /// <summary>Найбільше комірок входу однієї прив'язки (≥ 1).</summary>
    public int MaxInputCellsPerBinding { get; set; } = DefaultMaxInputCellsPerBinding;

    /// <summary>
    /// Бюджет кроків одного рядка скрипта на всі оператори (≥ <see cref="MinRowEvaluationSteps"/>); вичерпаний —
    /// <c>#BUDGET</c> на кожен наступний оператор рядка (PSS-21). Діє і в перерахунку, і в перегляді.
    /// </summary>
    public int MaxRowEvaluationSteps { get; set; } = DefaultMaxRowEvaluationSteps;

    /// <summary>Повертає ці ж ліміти, якщо вони допустимі.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Ліміт менший за 1 (бюджет рядка — менший за <see cref="MinRowEvaluationSteps"/>).
    /// </exception>
    /// <remarks>
    /// ⛔ Нуль і від'ємне — не «без ліміту»: <c>MaxDegreeOfParallelism = -1</c> у
    /// <c>Parallel.ForEachAsync</c> означає саме «без межі», тобто рівно те, від
    /// чого ліміт існує. На старті служби це ловить
    /// <c>EcrConfigurationValidation</c>; тут — для будь-якого іншого складача.
    /// </remarks>
    public CalculationLimits EnsureValid()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxParallelism, 1, $"{SectionName}:{nameof(MaxParallelism)}");
        ArgumentOutOfRangeException.ThrowIfLessThan(
            MaxInputCellsPerBinding, 1, $"{SectionName}:{nameof(MaxInputCellsPerBinding)}");
        ArgumentOutOfRangeException.ThrowIfLessThan(
            MaxRowEvaluationSteps, MinRowEvaluationSteps, $"{SectionName}:{nameof(MaxRowEvaluationSteps)}");
        return this;
    }
}
