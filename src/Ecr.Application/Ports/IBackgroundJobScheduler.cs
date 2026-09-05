// src/Ecr.Application/Ports/IBackgroundJobScheduler.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Планувальник фонових задач. Порт існує, щоб заміна реалізації
/// (Quartz ↔ Hangfire) коштувала день, а не рефакторинг (D-09): допустимість
/// LGPL — відкрите питання до ІБ.
/// </summary>
public interface IBackgroundJobScheduler
{
    /// <summary>Ставить задачу в чергу негайно.</summary>
    public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct) where TJob : IBackgroundJob;

    /// <summary>Планує задачу за cron-виразом.</summary>
    public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct) where TJob : IBackgroundJob;

    /// <summary>Скасовує задачу.</summary>
    public Task CancelAsync(string jobId, CancellationToken ct);

    /// <summary>Стан виконання для UI прогресу.</summary>
    public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct);
}

/// <summary>Фонова задача.</summary>
public interface IBackgroundJob
{
    /// <summary>Виконує задачу. Має бути ідемпотентною і відновлюваною.</summary>
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct);
}

/// <summary>Канал прогресу для довгих операцій (усе довше ~5 с — у фон).</summary>
public interface IJobProgress
{
    public Task ReportAsync(int percent, string? message, CancellationToken ct);
}

/// <summary>Стан фонової задачі.</summary>
public sealed record JobStatus(string JobId, string State, int Percent, string? Message, string? Error);

/// <summary>
/// Маркер задачі перерахунку.
/// </summary>
/// <remarks>
/// ⚠ Потрібен тому, що <see cref="IBackgroundJobScheduler.EnqueueAsync{TJob}"/>
/// обмежений <c>where TJob : IBackgroundJob</c>, а конкретні задачі живуть в
/// <c>Ecr.Infrastructure</c>, якого <c>Ecr.Application</c> не бачить і бачити
/// не має. Маркер дає use-case назвати задачу, не знаючи її реалізації.
/// </remarks>
public interface IRecalculationJob : IBackgroundJob;

/// <summary>Маркер задачі експорту документа у <c>.xlsx</c>.</summary>
/// <remarks>
/// Той самий прийом, що й <see cref="IRecalculationJob"/>: use-case називає
/// задачу, не знаючи, що її реалізація живе в <c>Ecr.Infrastructure</c> і
/// спирається на адаптер Excel.
/// </remarks>
public interface IExcelExportJob : IBackgroundJob;

/// <summary>Маркер задачі побудови зрізу звітності.</summary>
public interface IReportSnapshotJob : IBackgroundJob;

/// <summary>Маркер задачі збору із зовнішнього джерела.</summary>
public interface ICollectionJob : IBackgroundJob;
