// src/Ecr.Infrastructure/Jobs/RegistryImpactRecalculationJob.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Розкладає запуск «перерахувати зачеплені правкою довідника» на перерахунки документів (RT-25).
/// </summary>
/// <remarks>
/// ⛔ Набір звіряється з ПОТОЧНИМ станом <see cref="IRegistryImpactStore"/>, а не береться з payload
/// як є: між постановкою й виконанням період міг закритись, а прогін — втратити залежність. Закритий
/// період сюди не потрапить навіть з payload (<c>D-39</c>), і <see cref="ICalculationTrigger"/>
/// повторно перевіряє політику запису (закритий період; ✎ B63 — пропуск лише затверджених аркушів,
/// <see cref="ICalculationTrigger.RequestDependentsAsync(long, Ecr.Domain.ValueObjects.PeriodKey, CancellationToken)"/>).
/// <para>
/// ⚠ Право перевірено ПРИ ПОСТАНОВЦІ (<c>RecalculateImpactedHandler</c>); тут задача працює від імені
/// системи, як і будь-який автоперерахунок. Завершення — «поставлено», не «перераховано»: кожен
/// документ рахує власна <see cref="IRecalculationJob"/> зі злиттям без витіснення.
/// </para>
/// <para>
/// ⚠ Журнал у конструкторі необов'язковий лише для прямого конструювання в тестах; у DI
/// <c>ILogger&lt;T&gt;</c> зареєстрований завжди, тож причина кожного збою постановки йде в журнал.
/// </para>
/// </remarks>
public sealed partial class RegistryImpactRecalculationJob(
    IRegistryImpactStore impact,
    ICalculationTrigger trigger,
    ILogger<RegistryImpactRecalculationJob>? logger = null,
    IDependencyGraphProvider? graph = null) : IRegistryImpactRecalculationJob
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "registry-impact-recalculation";

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = RegistryImpactRecalculationRequest.Parse(payload);
        var wanted = request.DocumentIds.ToHashSet();

        await progress.ReportKeyAsync(10, "jobs.registryImpactReading", ct).ConfigureAwait(false);

        // Набір уже перевірено правами людини в обробнику, задача лише звіряє його зі станом.
        //
        // ⛔ D2-04: звіряються саме ВИБРАНІ документи, а не перша тисяча рядків довідника по всіх
        // проєктах. Доти документ із більшим Id (пізніший проєкт) за спільним довідником не потрапляв у
        // `MaxRows` глобального переліку, рахувався «зниклим» (`gone`), і задача завершувалась
        // `Succeeded` з `queued = 0` — результати лишались на старому довіднику.
        //
        // HSE400 3b: зміна документа-джерела — системна постановка без переліку людини; набір — усі залежні
        // документи року, тим самим запитом сховища (відкриті періоди, методологія читає довідник).
        IReadOnlyList<RegistryImpactRow> rows;
        if (request.IsSourceDocumentChange)
        {
            // B179 L2: споживачі — з графа залежностей (формули шаблонів і методології, що читають довідник зв'язки),
            // а не з актуальних результатів: документ-споживач без жодного прогону теж залежний (дизайн §1.3, §4).
            rows = await SourceDocumentConsumersAsync(request, ct).ConfigureAwait(false);
            wanted = [.. rows.Select(r => r.DocumentId)];
        }
        else if (request.IsSourceChange)
        {
            // FLERT F6 (і задачі старої форми «довідник × рік» у черзі): залежні — за результатами методологій року.
            rows = await impact
                .ListImpactedForYearAsync(request.RegistryDefId, request.Year!.Value, ct)
                .ConfigureAwait(false);
            wanted = [.. rows.Select(r => r.DocumentId)];
        }
        else
        {
            rows = await impact
                .ListImpactedForDocumentsAsync(request.RegistryDefId, [.. wanted], ct)
                .ConfigureAwait(false);
        }

        // Документ × період: одна методологія — один рядок, а перерахунок від неї не залежить.
        var targets = rows
            .Where(r => wanted.Contains(r.DocumentId))
            .Select(r => (r.DocumentId, r.PeriodKey))
            .Distinct()
            .ToList();

        var queued = 0;
        var failed = 0;
        var failedDocuments = new SortedSet<long>();
        var errors = new List<Exception>();

        // ✎ PERF-06 (D-566, рішення людини 22.11): каскад від правки документа-джерела — ОДНА задача на документ-споживач і рік
        // з періодами, які читають змінений місяць (обсяг ребра графа вище); правки за вікно каскаду зливаються об'єднанням
        // періодів, ланцюжок, що вже йде, дописує нові. Перекриття адміністратора (затверджені) — поперіодно, як доти.
        var cascade = request.IsSourceDocumentChange && !request.IncludeApproved && trigger.CoalescesCascadePeriods;
        if (cascade)
        {
            foreach (var group in targets.GroupBy(t => (t.DocumentId, Year: t.PeriodKey / 100)).OrderBy(g => g.Key))
            {
                try
                {
                    var enqueued = await trigger
                        .RequestSourceCascadeAsync(group.Key.DocumentId, group.Key.Year, [.. group.Select(t => t.PeriodKey)], request.Hop + 1, ct)
                        .ConfigureAwait(false);
                    if (enqueued.JobId is not null)
                    {
                        queued += enqueued.Periods.Count;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCascadeFailed(_logger, group.Key.DocumentId, group.Key.Year, ex);
                    failed += group.Count();
                    failedDocuments.Add(group.Key.DocumentId);
                    errors.Add(ex);
                }
            }
        }

        foreach (var (documentId, periodKey) in targets.Where(_ => !cascade))
        {
            // ⛔ Збій одного документа (гонитва, тимчасова помилка БД) не перериває решту набору:
            // решту все одно ставимо, а про збої — гучно в кінці. Скасування не ковтаємо.
            try
            {
                // ⛔ B63: залежні — по аркушу (пропуск лише затверджених), а не «будь-який поданий знімає документ».
                // B179 L2: зміна джерела несе ланку далі — споживач, що сам є джерелом, поставить наступну.
                // ✎ B80 N5: перекриття адміністратора (затверджені теж) несе хто й чому — у журнал записів аркушів.
                var jobId = request.IsSourceDocumentChange
                    ? request.IncludeApproved
                        ? await trigger.RequestDependentsAsync(
                                documentId, new PeriodKey(periodKey), request.Hop + 1,
                                new ApprovedRecalculationOverride(request.OverrideUserId ?? 0, request.OverrideReason ?? string.Empty), ct)
                            .ConfigureAwait(false)
                        : await trigger.RequestDependentsAsync(documentId, new PeriodKey(periodKey), request.Hop + 1, ct).ConfigureAwait(false)
                    : await trigger.RequestDependentsAsync(documentId, new PeriodKey(periodKey), ct).ConfigureAwait(false);
                if (jobId is not null)
                {
                    queued++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // P3 (аудит ent3): причина збою — у журнал і в InnerException, а не лише номер документа.
                LogRequestFailed(_logger, documentId, periodKey, ex);
                failed++;
                failedDocuments.Add(documentId);
                errors.Add(ex);
            }
        }

        await progress
            .ReportKeyAsync(
                100,
                "jobs.registryImpactDone",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["queued"] = queued.ToString(CultureInfo.InvariantCulture),
                    // Провалені — окремо: «пропущено» означає закритий період чи затверджені всі аркуші, а не збій.
                    ["skipped"] = (targets.Count - queued - failed).ToString(CultureInfo.InvariantCulture),
                    ["failed"] = failed.ToString(CultureInfo.InvariantCulture),
                    ["gone"] = (wanted.Count - targets.Select(t => t.DocumentId).Distinct().Count())
                        .ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);

        // Задача не вдає успіх, коли частину документів не поставлено: людина бачить збій, а решта вже в черзі.
        if (failed > 0)
        {
            throw new InvalidOperationException(
                $"Не вдалось поставити перерахунок для документів: {string.Join(", ", failedDocuments)}; решту поставлено ({queued}).",
                new AggregateException(errors));
        }
    }

    /// <summary>
    /// B179 L2: документи × періоди споживачів зв'язки за графом залежностей і обсягом ребра (<see cref="PeriodScope"/>).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>Month</c> — лише період, що володіє місяцем зміни (без місяця — увесь рік);</item>
    /// <item><c>Year</c> — усі періоди року; з літеральними місяцями (<c>MonthMask</c>) — лише коли маска містить
    /// місяць зміни;</item>
    /// <item><c>All</c> — усі відкриті періоди.</item>
    /// </list>
    /// ⛔ Ланка ≥ <see cref="RegistryImpactRecalculationRequest.MaxHops"/> — нічого (журнал помилки): ланцюжок джерел
    /// глибший за стелю — це цикл чи помилка налаштування, а не робота. Споживач у тій самій компоненті циклу графа,
    /// що й джерело (зокрема саме джерело), не ставиться зовсім (журнал): ланку циклу можна скинути правкою формули в
    /// іншому періоді (задача формул не несе ланки), тож стеля сама цикл не зупинила б. Відмову публікації циклу дає L3.
    /// </remarks>
    private async Task<IReadOnlyList<RegistryImpactRow>> SourceDocumentConsumersAsync(
        RegistryImpactRecalculationRequest request, CancellationToken ct)
    {
        if (request.Hop >= RegistryImpactRecalculationRequest.MaxHops)
        {
            LogHopCapReached(_logger, request.BindingId!.Value, request.Hop);
            return [];
        }

        if (graph is null)
        {
            return [];
        }

        var snapshot = await graph.GetAsync(ct).ConfigureAwait(false);
        var edges = snapshot.TemplateDependencies.Where(d => d.BindingId == request.BindingId).ToList();
        if (edges.Count == 0)
        {
            return [];
        }

        var source = edges[0].SourceTemplateId;
        var cycle = snapshot.Cycles.FirstOrDefault(c => c.Contains(source)) ?? [];
        var year = request.Year!.Value;
        var month = request.Month is >= 1 and <= 12 ? request.Month : null;

        var rows = new List<RegistryImpactRow>();
        foreach (var consumer in edges.GroupBy(e => e.ConsumerTemplateId))
        {
            if (cycle.Contains(consumer.Key))
            {
                LogCycleSkipped(_logger, request.BindingId!.Value, source, consumer.Key);
                continue;
            }

            var scope = consumer.Select(e => e.Scope).Aggregate((a, b) => a.Join(b));
            if (scope.Kind == PeriodScopeKind.Year && scope.MonthMask != 0 && month is { } m && (scope.MonthMask & (1 << m)) == 0)
            {
                continue;
            }

            var (first, last) = scope.Kind == PeriodScopeKind.All ? (int.MinValue, int.MaxValue) : (year * 100, (year * 100) + 99);
            var onlyMonth = scope.Kind == PeriodScopeKind.Month ? month : null;
            rows.AddRange(await impact
                .ListTemplateConsumersAsync([consumer.Key], first, last, onlyMonth, ct)
                .ConfigureAwait(false));
        }

        return rows;
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Перерахунок зачеплених довідником: не вдалось поставити документ {DocumentId}, період {PeriodKey}.")]
    private static partial void LogRequestFailed(ILogger logger, long documentId, int periodKey, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "PERF-06: каскад від правки джерела — не вдалось поставити документ-споживач {DocumentId} за рік {Year}.")]
    private static partial void LogCascadeFailed(ILogger logger, long documentId, int year, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "B179: ланцюжок документів-джерел зв'язки {BindingId} дійшов ланки {Hop} — стеля, залежних не поставлено (цикл чи помилка налаштування).")]
    private static partial void LogHopCapReached(ILogger logger, int bindingId, int hop);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "B179: зв'язка {BindingId}: шаблон-споживач {ConsumerTemplateId} у циклі з джерелом {SourceTemplateId} — не ставиться.")]
    private static partial void LogCycleSkipped(ILogger logger, int bindingId, int sourceTemplateId, int consumerTemplateId);
}
