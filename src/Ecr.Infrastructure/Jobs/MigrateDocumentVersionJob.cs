using System.Diagnostics;
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Перенос проєкту на нову версію шаблону у фоні (ФВ-7.5, D-2 RC15B).
/// </summary>
/// <remarks>
/// ⚠ Той самий шлях запису, що й синхронний, — <see cref="MigrateDocumentVersionHandler"/>, а не друга
/// копія логіки: задача лише переносить виклик у чергу й дає прогрес. Перенос лишається ОДНІЄЮ
/// транзакцією (атомарність): збій у будь-якій пачці відкочує все, документи лишаються на старій версії,
/// задача стає <c>Failed</c> з кодом каталогу винятку.
/// <para>
/// ⛔ Задача виконується ВІД ІМЕНІ автора (<see cref="MigrateDocumentVersionTask.Actor"/>), як
/// <see cref="ExcelImportJob"/> (F-01): права й гранти — автора на мить виконання, журнал безпеки
/// підписано ним. Завдання без автора відмовляє <c>ECR-AUTH-0401</c>, а не пише від імені системи.
/// </para>
/// <para>
/// ⛔ Прогрес пишеться ОКРЕМИМ скоупом (власний <c>EcrDbContext</c>, власне з'єднання), а не каналом
/// <see cref="IJobProgress"/> задачі: той ділить контекст з транзакцією переносу, тож запис кроку лягав би
/// в неї — невидимий опитувачу до коміту й відкочений разом із нею при збої. Запис не частіше за
/// <see cref="MinReportInterval"/> (ціна — запит у базу на кожну пачку по 5000 рядків).
/// </para>
/// <para>
/// ⚠ Тривалість. Задача йде лейном <c>default</c> у процесі API: Quartz-режим (типовий,
/// <c>Jobs:Queue:Mode = Quartz</c>) і воркер черги в базі межі тривалості для неї не мають. Межа
/// <c>Jobs:Workers:MaxDuration</c> (типово 30 хв) стосується лише дочірніх воркерів перерахунку
/// (лейн <c>recalc</c>) і сюди не діє. Оренда черги в базі подовжується окремим циклом кожні 30 с.
/// Кожна команда переносу має власний <c>CommandTimeout</c> 300 с.
/// </para>
/// </remarks>
public sealed class MigrateDocumentVersionJob(
    MigrateDocumentVersionHandler handler,
    JobActorScope actorScope,
    IServiceScopeFactory scopes,
    IClock clock) : IMigrateDocumentVersionJob
{
    /// <summary>Найрідший запис прогресу в межах однієї стадії.</summary>
    public static readonly TimeSpan MinReportInterval = TimeSpan.FromSeconds(1);

    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "migrate-document-version";

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var task = MigrationPayload.Parse(payload);

        // ⚠ Завдання без автора лишається без нього — і відмовляє тією самою `ECR-AUTH-0401`.
        using var actor = task.Actor is { } author ? actorScope.Enter(author) : null;

        if (!Enum.TryParse<VersionMigrationMode>(task.Mode, ignoreCase: false, out var mode))
        {
            throw new InvalidOperationException(
                "Завдання переносу версії не розбирається: невідомий режим.");
        }

        var reporter = new ThrottledReporter(progress, scopes, clock);

        var report = await handler
            .RunQueuedAsync(task.DocumentId, task.TargetVersionId, mode, reporter.ReportAsync, ct)
            .ConfigureAwait(false);

        // ⚠ Результат — у повідомленні прогресу (структурований конверт, мовою читача при GET /jobs):
        // окремого поля результату в `JobStatus` немає й додавати його для всіх задач заради однієї не треба.
        await progress
            .ReportKeyAsync(
                100,
                "jobs.migrateDone",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["version"] = report.ToVersion,
                    ["documents"] = report.DocumentCount.ToString(CultureInfo.InvariantCulture),
                    ["values"] = report.TransferredValues.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Пише прогрес окремим скоупом і не частіше за інтервал, окрім зміни стадії.</summary>
    private sealed class ThrottledReporter(IJobProgress inner, IServiceScopeFactory scopes, IClock clock)
    {
        private readonly string? jobId = (inner as IJobIdentity)?.JobId;
        private string? lastKey;
        private long lastTicks;

        public async Task ReportAsync(
            int percent, string messageKey, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
        {
            var now = Stopwatch.GetTimestamp();
            var sameStage = string.Equals(lastKey, messageKey, StringComparison.Ordinal);
            if (sameStage && Stopwatch.GetElapsedTime(lastTicks, now) < MinReportInterval)
            {
                return;
            }

            lastKey = messageKey;
            lastTicks = now;

            var message = JobProgressMessageCodec.Encode(
                parameters is null
                    ? new JobProgressMessageEnvelope(messageKey)
                    : new JobProgressMessageEnvelope(messageKey, parameters));

            if (jobId is null)
            {
                // Канал без ідентифікатора (тести): лишається власний канал задачі.
                await inner.ReportAsync(percent, message, ct).ConfigureAwait(false);
                return;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IJobProgressStore>();
                await store.ReportAsync(jobId, percent, message, clock.UtcNow, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Збій запису прогресу не повинен ламати перенос: скасування передаємо далі.
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
#pragma warning restore CA1031
            {
                // Прогрес — зручність, а не результат: перенос важливіший за рядок стану.
                _ = ex;
            }
        }
    }
}

/// <summary>Розбір завдання фонового переносу.</summary>
internal static class MigrationPayload
{
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Розбирає завдання черги.</summary>
    public static MigrateDocumentVersionTask Parse(object? payload)
    {
        if (payload is MigrateDocumentVersionTask typed)
        {
            return typed;
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);

        return System.Text.Json.JsonSerializer.Deserialize<MigrateDocumentVersionTask>(json, Options)
               ?? throw new InvalidOperationException(
                   "Завдання переносу версії не розбирається: невідома форма payload.");
    }
}
