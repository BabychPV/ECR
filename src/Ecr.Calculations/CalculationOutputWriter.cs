using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Calculations;

/// <summary>Записує результати прогону.</summary>
/// <remarks>
/// Пише **тільки** в <c>calc.CalculationResult</c>. У <c>doc.CellValue</c>
/// результати методологій не потрапляють ніколи (D-69): інакше нічний
/// перерахунок писав би десятки мільйонів рядків у партиції документів і
/// роздував <c>aud.CellChange</c>.
/// <para>
/// ⛔ Чанки <c>SaveChanges</c> (15.10, EcrLandUi). Один <c>SaveChanges</c> на всю методологію — це десятки
/// тисяч рядків однією довгою командою/транзакцією: під зупинкою I/O вона впиралась у CommandTimeout, а
/// відкат після таймауту коштував стільки ж. Набір виходів ріжеться за <see cref="MaxRowsPerSave"/> рядків
/// (результати + кроки трейсу + входи), по <c>SaveChanges</c> на чанк.
/// </para>
/// <para>
/// ⚠ Атомарність ЗБЕРЕЖЕНА: коли чанків більше одного, усі вони йдуть в ОДНІЙ транзакції
/// (<see cref="IUnitOfWork.ExecuteInTransactionAsync"/>, відкат стану трекера на повторі стратегії EF —
/// <c>ChangeTrackerCheckpoint</c> у <c>UnitOfWork</c>): методологія або записана цілком, або не записана.
/// Прогін, чия методологія не дописалась, і так лишається <c>Failed</c> і результатів не віддає
/// (<c>ReadCurrentAsync</c> читає лише <c>Current</c>), але напівзаписаних рядків під ним тепер не буває.
/// Один чанк — рівно та сама одна команда <c>SaveChanges</c>, що й раніше.
/// </para>
/// </remarks>
public sealed class CalculationOutputWriter(ICalculationResultStore store, IUnitOfWork uow)
{
    /// <summary>Типова стеля рядків на один <c>SaveChanges</c>.</summary>
    public const int DefaultMaxRowsPerSave = 5_000;

    /// <summary>Скільки рядків (результати + кроки + входи) вміщає один <c>SaveChanges</c>; ≥ 1.</summary>
    /// <remarks>⚠ <c>init</c> — для тестів: чанкування видно на десятках рядків, а не на тисячах.</remarks>
    public int MaxRowsPerSave { get; init; } = DefaultMaxRowsPerSave;

    /// <summary>Записує результати і трейс пакетно.</summary>
    /// <param name="calculationRunId">Прогін.</param>
    /// <param name="outputs">Результати рядків.</param>
    /// <param name="traceLevel">Рівень трейсу версії методології.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task WriteAsync(
        long calculationRunId,
        IReadOnlyList<CalculationOutput> outputs,
        TraceLevel traceLevel,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        if (outputs.Count == 0)
        {
            return;
        }

        var chunks = Chunk(outputs, traceLevel);

        if (chunks.Count == 1)
        {
            // Пакетно, одним проходом: SaveChanges у циклі за РЯДКОМ заборонений — бюджет
            // річного перерахунку 10 хвилин (ПРД-13), і коміт на рядок з'їдає його сам собою.
            await WriteChunkAsync(calculationRunId, chunks[0], traceLevel, ct).ConfigureAwait(false);
            return;
        }

        // Кілька чанків — одна транзакція: атомарність методології як у одного SaveChanges.
        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                foreach (var chunk in chunks)
                {
                    await WriteChunkAsync(calculationRunId, chunk, traceLevel, token).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);
    }

    private async Task WriteChunkAsync(
        long calculationRunId, IReadOnlyList<CalculationOutput> chunk, TraceLevel traceLevel, CancellationToken ct)
    {
        await store.WriteResultsAsync(calculationRunId, chunk, ct).ConfigureAwait(false);

        // Трейс — лише те, що передбачає рівень версії. Керуємо тим, ЩО
        // пишемо, а не скільки зберігаємо (ЗБР-3). ⚠ Результати чанка вже в трекері: крок прив'язується до
        // свого результату в тому самому наборі змін (`CalculationResultStore.PendingResults`), тому ріжемо
        // за ВИХОДАМИ, а не за рядками окремо.
        await store.WriteTraceAsync(calculationRunId, chunk, traceLevel, ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Ріже виходи на чанки не більше за <see cref="MaxRowsPerSave"/> рядків (вихід не ділиться).</summary>
    private List<IReadOnlyList<CalculationOutput>> Chunk(IReadOnlyList<CalculationOutput> outputs, TraceLevel traceLevel)
    {
        var limit = Math.Max(1, MaxRowsPerSave);
        var chunks = new List<IReadOnlyList<CalculationOutput>>();
        var current = new List<CalculationOutput>();
        var rows = 0;

        foreach (var output in outputs)
        {
            var size = RowsOf(output, traceLevel);
            if (current.Count > 0 && rows + size > limit)
            {
                chunks.Add(current);
                current = [];
                rows = 0;
            }

            current.Add(output);
            rows += size;
        }

        if (current.Count > 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }

    private static int RowsOf(CalculationOutput output, TraceLevel traceLevel)
        => output.Values.Count
           + (traceLevel == TraceLevel.Off ? 0 : output.Trace.Count + (output.Inputs?.Count ?? 0));
}
