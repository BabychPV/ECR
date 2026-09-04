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
        _cellsRead = meter.CreateHistogram<double>("ecr.cells.read", "ms", "Відкриття зрізу таблиці");
        _cellsWrite = meter.CreateHistogram<double>("ecr.cells.write", "ms", "Пакетний запис комірок");
        _formulaEvaluate = meter.CreateHistogram<double>("ecr.formula.evaluate", "ms", "Перерахунок формул таблиці");
        _accessProfileBuild = meter.CreateHistogram<double>("ecr.access.profile.build", "ms", "Побудова AccessProfile");
        _jobDuration = meter.CreateHistogram<double>("ecr.job.duration", "s", "Тривалість фонової задачі");
        _calcFullYear = meter.CreateHistogram<double>("ecr.calc.full_year", "s", "Повний річний перерахунок");
        _conflicts = meter.CreateCounter<long>("ecr.conflict.count", "1", "Конфлікти паралельного редагування");
        _consistencyIssues = meter.CreateCounter<long>("ecr.consistency.issues", "1", "Знахідки ConsistencyCheckJob");
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
}
