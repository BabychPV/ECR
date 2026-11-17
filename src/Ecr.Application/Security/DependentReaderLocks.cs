using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>✎ B79 (квіз 16.11): аркуш-читач стовпця (інший, ніж аркуш правки) — для замків і відмови.</summary>
/// <param name="SheetDefId">Аркуш формули-читача.</param>
/// <param name="SheetCode">Код аркуша (подробиця <c>sheets</c> відмови).</param>
/// <param name="CopyIds">
/// ✎ Квіз 16.11 (координатор): КОПІЇ аркуша-читача в документі (<c>doc.DocumentSheet.Id</c>) — копія має власний стан, і
/// її затверджений місяць — теж залежний затверджений місяць; <c>null</c>/порожньо — копій немає.
/// </param>
public sealed record DependentReaderSheet(int SheetDefId, string SheetCode, IReadOnlyList<long>? CopyIds = null);

/// <summary>
/// ✎ B79 (квіз 16.11; координатор: «перевірка після факту без замка — не досить»): спільні замки аркушів-ЧИТАЧІВ стовпця
/// за місяці року разом із замками аркуша правки — в ОДНОМУ каноничному порядку (аркуш ↑, копія ↑, період ↑, як
/// <c>RecalculationService.EnterSheetsAsync</c>), і повторна перевірка правила під ними.
/// </summary>
/// <remarks>
/// ⚠ Порядок — не «спершу аркуш правки»: аркуш-читач із меншим Id береться раніше. Затвердження місяця читача бере ВИНЯТКОВИЙ
/// замок того самого ключа, тож тут або воно ще не почалось (і чекатиме коміту правки), або вже зафіксоване — і стан
/// <c>Approved</c> відхиляє запис. Блокуються й КОПІЇ аркушів-читачів (кожна — свій ключ і свій стан; копія йде після
/// оригіналу свого аркуша за зростанням Id). Кількість замків: (1 + копій) × місяців на аркуш-читач; спільний замок —
/// один виклик застосункового замка, без даних.
/// </remarks>
public static class DependentReaderLocks
{
    /// <summary>Крок плану замків: аркуш правки (<c>Reader = false</c>) чи аркуш-читач.</summary>
    /// <param name="SheetDefId">Аркуш.</param>
    /// <param name="CopyId">Копія; <c>null</c> — оригінал.</param>
    /// <param name="Period">Ключ періоду.</param>
    /// <param name="Reader">Аркуш-читач.</param>
    public readonly record struct Step(int SheetDefId, long? CopyId, int Period, bool Reader);

    /// <summary>Усі замки запису в каноничному порядку: аркуш ↑, копія ↑, період ↑; без повторів.</summary>
    /// <param name="sheetDefId">Аркуш правки.</param>
    /// <param name="copyId">Копія аркуша правки.</param>
    /// <param name="ownPeriods">Періоди аркуша правки.</param>
    /// <param name="readers">Аркуші-читачі.</param>
    /// <param name="readerPeriods">Місяці року для аркушів-читачів.</param>
    public static IReadOnlyList<Step> Plan(
        int sheetDefId, long? copyId, IEnumerable<int> ownPeriods,
        IEnumerable<DependentReaderSheet> readers, IEnumerable<int> readerPeriods)
    {
        ArgumentNullException.ThrowIfNull(ownPeriods);
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(readerPeriods);

        var months = readerPeriods.Distinct().ToList();
        var steps = ownPeriods.Distinct().Select(p => new Step(sheetDefId, copyId, p, Reader: false))
            .Concat(readers
                .Where(r => r.SheetDefId != sheetDefId)
                .DistinctBy(r => r.SheetDefId)
                .SelectMany(r => new long?[] { null }
                    .Concat((r.CopyIds ?? []).Distinct().Select(c => (long?)c))
                    .SelectMany(copy => months.Select(p => new Step(r.SheetDefId, copy, p, Reader: true)))));

        return [.. steps.OrderBy(s => s.SheetDefId).ThenBy(s => s.CopyId ?? 0).ThenBy(s => s.Period)];
    }

    /// <summary>Повторна перевірка під замками: затверджений місяць аркуша-читача відхиляє запис з <c>sheets</c>.</summary>
    /// <param name="readers">Аркуші-читачі.</param>
    /// <param name="periods">Місяці року.</param>
    /// <param name="statuses">Стани аркушів-читачів (оригінал і копії), прочитані під замками: (аркуш, копія, період).</param>
    /// <param name="deniedCount">Скільки комірок відхиляє відмова.</param>
    /// <exception cref="Errors.AccessDeniedException"><c>DependentMonthsLocked</c> з <c>sheets</c>.</exception>
    public static void ThrowIfLocked(
        IEnumerable<DependentReaderSheet> readers, IEnumerable<int> periods,
        IReadOnlyDictionary<(int SheetDefId, long? CopyId, int Period), DocumentStatus> statuses, int deniedCount)
    {
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentNullException.ThrowIfNull(statuses);

        var months = periods.Distinct().ToList();
        var blocks = readers
            .DistinctBy(r => r.SheetDefId)
            .OrderBy(r => r.SheetCode, StringComparer.Ordinal)
            .Select(r => new DependentMonthsBlock(
                r.SheetDefId, r.SheetCode,
                [.. statuses
                    .Where(s => s.Key.SheetDefId == r.SheetDefId
                                && months.Contains(s.Key.Period)
                                && DependentMonthsRule.Blocks(s.Value))
                    .Select(s => s.Key.Period)
                    .Distinct()
                    .Order()]))
            .Where(b => b.Months.Count > 0)
            .ToList();

        if (blocks.Count == 0)
        {
            return;
        }

        throw DependentMonthsRule.Denied(
            deniedCount, [.. blocks.SelectMany(b => b.Months).Distinct().Order()], blocks);
    }

    /// <summary>
    /// Бере замки плану через <paramref name="gate"/>: аркуш правки — <paramref name="enterOwn"/> (у викликача там свої
    /// виняткові кроки), аркуш-читач — спільний замок оригіналу чи копії. Повертає стани аркушів-читачів.
    /// </summary>
    /// <param name="gate">Замок аркушів.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="plan">План (<see cref="Plan"/>).</param>
    /// <param name="enterOwn">Як узяти замок аркуша правки за період.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<Dictionary<(int SheetDefId, long? CopyId, int Period), DocumentStatus>> EnterAsync(
        ISheetEditGate gate, long documentId, IReadOnlyList<Step> plan, Func<int, Task> enterOwn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(enterOwn);

        var readers = new Dictionary<(int, long?, int), DocumentStatus>();
        foreach (var step in plan)
        {
            if (!step.Reader)
            {
                await enterOwn(step.Period).ConfigureAwait(false);
                continue;
            }

            readers[(step.SheetDefId, step.CopyId, step.Period)] = await gate
                .EnterEditOfAsync(documentId, step.SheetDefId, new PeriodKey(step.Period), step.CopyId, ct)
                .ConfigureAwait(false);
        }

        return readers;
    }
}
