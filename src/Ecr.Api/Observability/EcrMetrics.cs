using System.Diagnostics.Metrics;

namespace Ecr.Api.Observability;

/// <summary>
/// Метрики. Правило просте: якщо ендпоінт є в таблиці бюджету
/// (tz/08 §8.2) — він **зобов'язаний** мати метрику. Інакше твердження
/// «вкладаємося в бюджет» нічим не перевірити.
/// </summary>
public sealed class EcrMetrics
{
    /// <summary>Ім'я лічильника для OpenTelemetry.</summary>
    public const string MeterName = "Ecr";

    /// <summary>Відкриття зрізу таблиці.</summary>
    public const string CellsRead = "ecr.cells.read";

    /// <summary>Пакетний запис комірок.</summary>
    public const string CellsWrite = "ecr.cells.write";

    /// <summary>Перерахунок формул.</summary>
    public const string FormulaEvaluate = "ecr.formula.evaluate";

    /// <summary>Побудова профілю доступу; має траплятися раз на сесію.</summary>
    public const string AccessProfileBuild = "ecr.access.profile.build";

    /// <summary>Тривалість фонової або довгої синхронної операції.</summary>
    public const string JobDuration = "ecr.job.duration";

    /// <summary>Повний річний перерахунок; бюджет 600 с (ПРД-13).</summary>
    public const string CalcFullYear = "ecr.calc.full_year";

    /// <summary>Конфлікти паралельного редагування.</summary>
    public const string ConflictCount = "ecr.conflict.count";

    /// <summary>Знахідки нічної перевірки інваріантів.</summary>
    public const string ConsistencyIssues = "ecr.consistency.issues";

    private readonly Histogram<double> _cellsRead;
    private readonly Histogram<double> _cellsWrite;
    private readonly Histogram<double> _formulaEvaluate;
    private readonly Histogram<double> _accessProfileBuild;
    private readonly Histogram<double> _jobDuration;
    private readonly Histogram<double> _calcFullYear;
    private readonly Counter<long> _conflicts;
    private readonly Counter<long> _consistencyIssues;

    /// <summary>Створює набір метрик.</summary>
    public EcrMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(MeterName);
        _cellsRead = meter.CreateHistogram<double>(CellsRead, "ms", "Відкриття зрізу таблиці");
        _cellsWrite = meter.CreateHistogram<double>(CellsWrite, "ms", "Пакетний запис комірок");
        _formulaEvaluate = meter.CreateHistogram<double>(FormulaEvaluate, "ms", "Перерахунок формул таблиці");
        _accessProfileBuild = meter.CreateHistogram<double>(AccessProfileBuild, "ms", "Побудова AccessProfile");
        _jobDuration = meter.CreateHistogram<double>(JobDuration, "s", "Тривалість фонової задачі");
        _calcFullYear = meter.CreateHistogram<double>(CalcFullYear, "s", "Повний річний перерахунок");
        _conflicts = meter.CreateCounter<long>(ConflictCount, "1", "Конфлікти паралельного редагування");
        _consistencyIssues = meter.CreateCounter<long>(ConsistencyIssues, "1", "Знахідки ConsistencyCheckJob");
    }

    /// <summary>Фіксує тривалість читання зрізу.</summary>
    public void RecordCellsRead(double ms, int cellCount)
        => _cellsRead.Record(ms, new KeyValuePair<string, object?>("cells", cellCount));

    /// <summary>Фіксує тривалість запису.</summary>
    public void RecordCellsWrite(double ms, int cellCount)
        => _cellsWrite.Record(ms, new KeyValuePair<string, object?>("cells", cellCount));

    /// <summary>
    /// Фіксує повний річний перерахунок. **Бюджет — 600 с** (ПРД-13);
    /// перевищення має бути видно на графіку одразу.
    /// </summary>
    public void RecordFullYearCalculation(double seconds, int documentCount)
        => _calcFullYear.Record(seconds, new KeyValuePair<string, object?>("documents", documentCount));

    /// <summary>Фіксує конфлікт.</summary>
    public void RecordConflict() => _conflicts.Add(1);

    /// <summary>Фіксує перерахунок формул.</summary>
    /// <param name="ms">Тривалість.</param>
    /// <param name="formulaCount">Скільки формул перераховано.</param>
    public void RecordFormulaEvaluate(double ms, int formulaCount)
        => _formulaEvaluate.Record(ms, new KeyValuePair<string, object?>("formulas", formulaCount));

    /// <summary>
    /// Фіксує побудову профілю доступу.
    /// </summary>
    /// <param name="ms">Тривалість.</param>
    /// <remarks>
    /// ⚠ Метрика існує не заради часу, а заради ЧАСТОТИ: профіль будується раз
    /// на сесію (ФВ-6.10). Сплеск кількості означає, що кеш не працює, і
    /// бюджет прав у 50 мс на запит уже не тримається.
    /// </remarks>
    public void RecordAccessProfileBuild(double ms)
        => _accessProfileBuild.Record(ms);

    /// <summary>Фіксує тривалість довгої операції.</summary>
    /// <param name="operation">Назва операції — тег на гістограмі.</param>
    /// <param name="seconds">Тривалість.</param>
    public void RecordDuration(string operation, double seconds)
        => _jobDuration.Record(seconds, new KeyValuePair<string, object?>("operation", operation));

    /// <summary>
    /// Фіксує знахідки нічної перевірки інваріантів.
    /// </summary>
    /// <param name="count">Скільки знайдено.</param>
    /// <param name="kind">Різновид знахідки.</param>
    /// <remarks>
    /// ⚠ Ненульове значення — **баг, а не шум** (ФВ-7.7). Якщо перевірка
    /// регулярно щось знаходить і це вважають нормою, вона перестає працювати
    /// як сигнал.
    /// </remarks>
    public void RecordConsistencyIssues(int count, string kind)
        => _consistencyIssues.Add(count, new KeyValuePair<string, object?>("kind", kind));
}
