// src/Ecr.Infrastructure/Jobs/RegistryImpactRecalculationJob.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Розкладає запуск «перерахувати зачеплені правкою довідника» на перерахунки документів (RT-25).
/// </summary>
/// <remarks>
/// ⛔ Набір звіряється з ПОТОЧНИМ станом <see cref="IRegistryImpactStore"/>, а не береться з payload
/// як є: між постановкою й виконанням період міг закритись, а прогін — втратити залежність. Закритий
/// період сюди не потрапить навіть з payload (<c>D-39</c>), і <see cref="ICalculationTrigger"/>
/// повторно перевіряє політику запису (закритий період, поданий аркуш).
/// <para>
/// ⚠ Право перевірено ПРИ ПОСТАНОВЦІ (<c>RecalculateImpactedHandler</c>); тут задача працює від імені
/// системи, як і будь-який автоперерахунок. Завершення — «поставлено», не «перераховано»: кожен
/// документ рахує власна <see cref="IRecalculationJob"/> зі злиттям без витіснення.
/// </para>
/// </remarks>
public sealed class RegistryImpactRecalculationJob(
    IRegistryImpactStore impact,
    ICalculationTrigger trigger) : IRegistryImpactRecalculationJob
{
    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "registry-impact-recalculation";

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = RegistryImpactRecalculationRequest.Parse(payload);
        var wanted = request.DocumentIds.ToHashSet();

        await progress.ReportKeyAsync(10, "jobs.registryImpactReading", ct).ConfigureAwait(false);

        // Усі проєкти: набір уже перевірено правами людини в обробнику, задача лише звіряє його зі станом.
        var rows = await impact
            .ListImpactedAsync(request.RegistryDefId, projectIds: null, IRegistryImpactStore.MaxRows, ct)
            .ConfigureAwait(false);

        // Документ × період: одна методологія — один рядок, а перерахунок від неї не залежить.
        var targets = rows
            .Where(r => wanted.Contains(r.DocumentId))
            .Select(r => (r.DocumentId, r.PeriodKey))
            .Distinct()
            .ToList();

        var queued = 0;
        var failed = new List<long>();
        foreach (var (documentId, periodKey) in targets)
        {
            // ⛔ Збій одного документа (гонитва, тимчасова помилка БД) не перериває решту набору:
            // решту все одно ставимо, а про збої — гучно в кінці. Скасування не ковтаємо.
            try
            {
                var jobId = await trigger.RequestAsync(documentId, new PeriodKey(periodKey), ct).ConfigureAwait(false);
                if (jobId is not null)
                {
                    queued++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(documentId);
            }
        }

        await progress
            .ReportKeyAsync(
                100,
                "jobs.registryImpactDone",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["queued"] = queued.ToString(CultureInfo.InvariantCulture),
                    ["skipped"] = (targets.Count - queued).ToString(CultureInfo.InvariantCulture),
                    ["gone"] = (wanted.Count - targets.Select(t => t.DocumentId).Distinct().Count())
                        .ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);

        // Задача не вдає успіх, коли частину документів не поставлено: людина бачить збій, а решта вже в черзі.
        if (failed.Count > 0)
        {
            throw new InvalidOperationException(
                $"Не вдалось поставити перерахунок для документів: {string.Join(", ", failed)}; решту поставлено ({queued}).");
        }
    }
}
