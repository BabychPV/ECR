using System.Collections.Concurrent;
using System.Diagnostics;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>
/// Оркеструє прогін розрахунку: підбирає методології, будує порядок, виконує
/// модулі, записує результати.
/// </summary>
/// <remarks>
/// **Бюджет повного річного перерахунку — ≤ 10 хвилин** (ПРД-13). Базова лінія
/// чинної системи — 20 хвилин, тому «не гірше» тут не працює: потрібне
/// щонайменше дворазове прискорення. Це і диктує архітектуру нижче.
/// </remarks>
public sealed class CalculationOrchestrator(
    MethodologyResolver resolver,
    IEnumerable<ICalculationModule> modules,
    CalculationInputBuilder inputBuilder,
    CalculationOutputWriter outputWriter,
    IPeriodStore periods) : ICalculationRunner
{
    /// <summary>
    /// Скільки методологій одного пакета виконувати одночасно.
    /// </summary>
    /// <remarks>
    /// Обмеження обов'язкове: прогін не має з'їдати p95 операторів, які в цей
    /// час заповнюють форми. Ізоляція від інтерактивного піку — вимога, а не
    /// побажання (ПРД-13).
    /// </remarks>
    private const int MaxParallelism = 4;

    /// <inheritdoc />
    /// <param name="calculationRunId">Прогін, створений use-case.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="bindings">Прив'язки методологій до таблиць документа.</param>
    /// <param name="progress">Канал прогресу для UI.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Профіль по модулях — заповнюється завжди (J-1).</returns>
    public async Task<ModuleProfile> RunAsync(
        long calculationRunId,
        long documentId,
        PeriodKey periodKey,
        IReadOnlyList<CalculationBindingRef> bindings,
        IJobProgress progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(progress);

        var profile = new ModuleProfile();
        if (bindings.Count == 0)
        {
            return profile;
        }

        var onDate = await PeriodDateAsync(documentId, periodKey, ct).ConfigureAwait(false);

        // 1. Версія методології — за ДАТОЮ ПЕРІОДУ, не за «поточною» (ФВ-9.3).
        var resolved = new List<ResolvedBinding>(bindings.Count);
        foreach (var binding in bindings)
        {
            var descriptor = await resolver
                .ResolveVersionAsync(binding.MethodologyId, onDate, ct)
                .ConfigureAwait(false);

            if (descriptor is not null)
            {
                resolved.Add(new ResolvedBinding(binding.TableInstanceId, descriptor));
            }
        }

        // 2. Пакети незалежних методологій. Залежності між ними беруть участь
        //    у порядку нарівні з формулами; поки їх немає в схемі, кожна
        //    методологія незалежна — і весь набір лягає в один пакет.
        var batches = CalculationPlan.Build(
            resolved.Select(r => new CalculationNode(r.Descriptor.MethodologyVersionId, [])).ToList());

        var byVersion = resolved.ToLookup(r => r.Descriptor.MethodologyVersionId);
        var done = 0;

        foreach (var batch in batches)
        {
            // 3. ⚠ Усередині пакета — ПАРАЛЕЛЬНО. Послідовний прогін у 10
            //    хвилин не вкладається: методології одного пакета незалежні за
            //    побудовою, і виконувати їх по черзі означає платити сумою там,
            //    де можна платити максимумом.
            var measured = new ConcurrentBag<(string Module, TimeSpan Elapsed, int Rows)>();

            await Parallel.ForEachAsync(
                batch.MethodologyVersionIds,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = ct },
                async (versionId, token) =>
                {
                    foreach (var binding in byVersion[versionId])
                    {
                        var stat = await ExecuteAsync(
                            calculationRunId, documentId, periodKey, binding, token).ConfigureAwait(false);

                        measured.Add(stat);
                    }
                }).ConfigureAwait(false);

            foreach (var (module, elapsed, rowCount) in measured)
            {
                profile.Record(module, elapsed, rowCount);
            }

            done += batch.MethodologyVersionIds.Count;

            await progress
                .ReportAsync(done * 100 / resolved.Count, $"Пакет {batch.Ordinal + 1} із {batches.Count}", ct)
                .ConfigureAwait(false);
        }

        return profile;
    }

    /// <summary>Виконує одну прив'язку і повертає її внесок у профіль.</summary>
    private async Task<(string Module, TimeSpan Elapsed, int Rows)> ExecuteAsync(
        long calculationRunId,
        long documentId,
        PeriodKey periodKey,
        ResolvedBinding binding,
        CancellationToken ct)
    {
        var module = modules.FirstOrDefault(m => m.CanHandle(binding.Descriptor));
        if (module is null)
        {
            // ⛔ Немає модуля, здатного виконати рівень методології — це
            // помилка конфігурації, а не порожній результат. Найчастіша
            // причина: рівень 2 (скрипти), який не зареєстрований без дозволу
            // ІБ (K-1). Мовчазний нуль тут виглядав би як «викидів немає».
            throw new Domain.Abstractions.DomainException(
                "ECR-CALC-0422",
                $"Немає модуля для методології {binding.Descriptor.Code} "
                + $"рівня {binding.Descriptor.Level}.");
        }

        var rowKeys = await resolver
            .MatchRowsAsync(binding.Descriptor.MethodologyVersionId, binding.TableInstanceId, ct)
            .ConfigureAwait(false);

        // 4. Входи ПАКЕТНО: один запит на методологію × період, не N на рядок.
        var inputs = await inputBuilder
            .BuildAsync(binding.TableInstanceId, rowKeys, periodKey, binding.Descriptor, ct)
            .ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var outputs = new List<CalculationOutput>(inputs.Count);

        foreach (var input in inputs)
        {
            // 5. Проміжні значення живуть у пам'яті воркера: модуль нічого не
            //    пише і не читає з бази між рядками.
            outputs.Add(await module.ExecuteAsync(input, ct).ConfigureAwait(false));
        }

        stopwatch.Stop();

        await outputWriter
            .WriteAsync(calculationRunId, outputs, binding.Descriptor.TraceLevel, ct)
            .ConfigureAwait(false);

        return (module.Code, stopwatch.Elapsed, inputs.Count);
    }

    /// <summary>Останній день періоду — дата, на яку резолвиться версія.</summary>
    /// <remarks>
    /// ⛔ Межі беруться з реальних меж періоду документа, а НЕ виводяться
    /// арифметикою з <c>PeriodKey</c>. <c>PeriodKey = Year*100 + Sequence</c>
    /// (R-A6), і для квартального/річного проєкту <c>Sequence</c> — це номер
    /// кварталу/року, не місяць: попередня версія цього методу читала
    /// <c>Sequence % 100</c> як номер місяця (`Math.Clamp(..., 1, 12)`) для
    /// БУДЬ-ЯКОГО <c>PeriodKind</c> — для кварталу 2 це резолвило версію
    /// методології на 28 лютого замість справжнього кінця кварталу (30/31
    /// червня), без жодної помилки, що це впіймала б (D-112, той самий клас
    /// дефекту, якого <see cref="GenericCalculationModule.PeriodAsync"/> у
    /// цьому ж проєкті явно уникає тим самим способом — через реальні межі
    /// періоду, а не арифметику ключа).
    /// </remarks>
    private async Task<DateOnly> PeriodDateAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        var bounds = await periods
            .FindPeriodBoundsAsync(documentId, periodKey.Value, ct)
            .ConfigureAwait(false)
            ?? throw new Domain.Abstractions.DomainException(
                "ECR-PRD-0404",
                $"Періоду {periodKey.Value} для документа {documentId} не існує: "
                + "дату резолвінгу методології обчислити нема з чого.");

        return bounds.PeriodEnd;
    }

    /// <summary>Прив'язка з уже підібраною версією.</summary>
    private sealed record ResolvedBinding(long TableInstanceId, MethodologyDescriptor Descriptor);
}


